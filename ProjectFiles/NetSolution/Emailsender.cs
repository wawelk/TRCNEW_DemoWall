#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SerialPort;
using FTOptix.Core;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.IO;
using System.Text.Json;
using System.Linq;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class Emailsender : BaseNetLogic
{
    private IUAVariable newEventTrigger;
    private IUAVariable emailStatus;
    private IUAVariable maxDelay;
    private PeriodicTask retryPeriodicTask;
    private Stack<MailMessageWithRetries> failedMessagesQueue = new Stack<MailMessageWithRetries>();
    private SmtpClient smtpClient;
    private string senderAddress;
    private string senderPassword;
    private string smtpHostname;
    private int smtpPort;
    private bool enableSSL;
    private IUANode emailSettings;
    private IUANode deviceSettings;
    private IUANode usersFolder;
    private string deviceName;
    private IUAVariable ipVariable;
    private string jsonFilePath;
    private IUAVariable emailServiceTest; // Status: 0 - not initiated, 1 - ok, 2 - error
    private string emailLogFilePath; // Path for email logs JSON private IUAVariable emailServiceTest;

    public override void Start()
    {
        ValidateCertificate();

        emailStatus = LogicObject.GetVariable("EmailSendingStatus");
        maxDelay = LogicObject.GetVariable("DelayBeforeRetry");
        maxDelay.VariableChange += RestartPeriodicTask;

        // Initialize emailServiceTest variable
        emailServiceTest = LogicObject.GetVariable("EmailServiceTest");
        if (emailServiceTest == null)
        {
            Log.Error("EmailSenderLogic", "EmailServiceTest variable not found.");
            return;
        }
        emailServiceTest.Value = 0; // Set initial value to "not initiated"
        IUANode emailsettingsobject = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Email_GeneralSettings");
        emailSettings = InformationModel.Get(emailsettingsobject.NodeId);
        if (emailSettings == null)
        {
            Log.Error("EmailSenderLogic", "Email_generalSettings object not found.");
            return;
        }

        deviceSettings = LogicObject.GetVariable("DeviceSettings");
        if (deviceSettings == null)
        {
            Log.Error("EmailSenderLogic", "DeviceSettings object not found.");
            //return;
        }

        usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
        if (usersFolder == null)
        {
            Log.Error("EmailSenderLogic", "Users folder not found.");
            return;
        }

        // Set up JSON file path and device details
        var Tag = Owner;
        if (Tag == null)
        {
            Log.Error("EmailSenderLogic", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("EmailSenderLogic", "Device name is null or empty");
            return;
        }

        ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("EmailSenderLogic", "Val_IPAddress variable not found in Tag");
            return;
        }

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        jsonFilePath = Path.Combine(baseDirectory, deviceName, "NewEventsDetection", $"{deviceName}_EventsQueue.json");

        emailLogFilePath = Path.Combine(baseDirectory, deviceName, "NewEventsDetection", $"{deviceName}_EmailLog.json");
    

        newEventTrigger = LogicObject.GetVariable("NewEventTrigger");
        if (newEventTrigger == null)
        {
            Log.Error("EmailSenderLogic", "NewEventTrigger variable not found.");
            return;
        }
        newEventTrigger.VariableChange += OnNewEventDetected;

        // Run email service check on start
        //CheckEmailService();
    }

    public override void Stop()
    {
        retryPeriodicTask?.Cancel();
        newEventTrigger.VariableChange -= OnNewEventDetected;
    }

  

    [ExportMethod]
    public void SendEmail(string mailToAddress, string mailSubject, string mailBody, out string status)
    {
        status = "Failed to send email to " + mailToAddress + ". Check email sender configuration";

        if (!InitializeAndValidateSMTPParameters())
            return;

        if (!ValidateEmail(mailToAddress, mailSubject, mailBody))
            return;

        var fromAddress = new MailAddress(senderAddress, "From");
        var toAddress = new MailAddress(mailToAddress, "To");

        smtpClient = new SmtpClient
        {
            Host = smtpHostname,
            Port = smtpPort,
            EnableSsl = enableSSL,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(fromAddress.Address, senderPassword)
        };
        var message = CreateEmailMessage(fromAddress, toAddress, mailBody, mailSubject);

        TrySendEmail(message, out status);

        // Log the email attempt
        LogEmailActivity(mailToAddress, mailSubject, status);
    }
    
    [ExportMethod]
    public void CheckEmailService()
    {
        // Get the first active user to send the test email to
        var users = usersFolder.GetNodesByType<PMNotification_User>()
            .Where(u => (bool)(u.GetVariable("isActive")?.Value ?? false))
            .ToList();

        if (users.Count == 0)
        {
            Log.Error("EmailSenderLogic", "No active users found to send test email.");
            emailServiceTest.Value = 2; // Error
            return;
        }

        // Use the first active user's email for the test
        string testRecipient = users[0].email;
        string testSubject = "Power Monitor Event Notification - SelfTest";
        string testBody = "This message was sent to test the EmailService. Do not respond.";

        SendEmail(testRecipient, testSubject, testBody, out string status);
        emailServiceTest.Value = status.Contains("Email sent successfully") ? 1 : 2;
        //Log.Info("EmailSenderLogic", $"Email service test result: {status}");
    }

    private void OnNewEventDetected(object sender, VariableChangeEventArgs e)
    {
        var emailEnabledForDevice = LogicObject.GetVariable("EmailNotificationsEnabled")?.Value ?? true;
        if (!emailEnabledForDevice)
        {
            //Log.Info("EmailSenderLogic", "Email notifications are disabled for this device.");
            return;
        }

        var events = LoadEventsFromJson();
        if (events.Count == 0)
        {
            Log.Warning("EmailSenderLogic", "No events found in JSON.");
            newEventTrigger.Value = false;
            return;
        }

        var users = usersFolder.GetNodesByType<PMNotification_User>()
            .Where(u => (bool)(u.GetVariable("isActive")?.Value ?? false))
            .ToList();

        string detectionTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        SendDetectionEmail(users, events, detectionTimestamp, out string status);
        //Log.Info("EmailSenderLogic", $"Email attempt for detection at {detectionTimestamp}: {status}");

        if (status.Contains("Email sent successfully"))
        {
            ClearEventsFromJson();
        }
        newEventTrigger.Value = false;
    }

    private void SendDetectionEmail(List<PMNotification_User> recipients, List<PowerQualityData> events, string detectionTimestamp, out string status)
    {
        // Removed grouping by Event_Type and Local_Timestamp
        string message = BuildEmailMessage(events);
        foreach (var recipient in recipients)
        {
            SendEmail(recipient.email, $"Power Monitor Event Notification: {deviceName}", message, out status);

            var userStatus = recipient.Get("UserStatus");
            if (userStatus != null)
            {
                userStatus.GetVariable("lastError").Value = status;
                userStatus.GetVariable("succesfullConnTime").Value = DateTime.Now;
            }

            if (!status.Contains("Email sent successfully"))
            {
                var fromAddress = new MailAddress(senderAddress, "From");
                var toAddress = new MailAddress(recipient.email, "To");
                var messageToRetry = new MailMessageWithRetries(fromAddress, toAddress, detectionTimestamp)
                {
                    Body = message,
                    Subject = $"Power Monitor Event Notification: {deviceName}",
                    BodyEncoding = System.Text.Encoding.UTF8,
                    IsBodyHtml = true
                };
                EnqueueFailedMessage(messageToRetry);
                return;
            }
        }
        status = "Email sent successfully";
    }

    private void RestartPeriodicTask(object sender, VariableChangeEventArgs e)
    {
        if (e.NewValue < 10000 || e.NewValue == null)
        {
            Log.Warning("EmailSenderLogic", "Minimum delay before retrying should be 10 seconds");
            return;
        }

        retryPeriodicTask?.Cancel();
        retryPeriodicTask = new PeriodicTask(SendQueuedMessage, e.NewValue, LogicObject);
        retryPeriodicTask.Start();
    }

    private MailMessageWithRetries CreateEmailMessage(MailAddress fromAddress, MailAddress toAddress, string mailBody, string mailSubject)
    {
        MailAddress fromAddressWithDescription = new MailAddress(fromAddress.Address, "Optix Notification Alert Service");
        var mailMessage = new MailMessageWithRetries(fromAddress, toAddress, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))
        {
            From = fromAddressWithDescription,
            Body = mailBody,
            Subject = mailSubject,
            BodyEncoding = System.Text.Encoding.UTF8,
            IsBodyHtml = true
        };

        var attachment = emailSettings.GetVariable("Attachment")?.Value ?? "";
        if (!string.IsNullOrEmpty(attachment))
        {
            var attachmentUri = new ResourceUri(attachment);
            mailMessage.Attachments.Add(new Attachment(attachmentUri.Uri));
        }

        mailMessage.ReplyToList.Add(toAddress);
        return mailMessage;
    }

    private void TrySendEmail(MailMessageWithRetries message, out string status)
    {
        if (!CanRetrySendingMessage(message))
        {
            status = "Cannot retry sending the message";
            return;
        }

        using (message)
        {
            try
            {
                message.AttemptNumber++;
                status = "Sending Email";
                smtpClient.Send(message);

                emailStatus.Value = true;
                status = "Email sent successfully";
            }
            catch (SmtpException e)
            {
                emailStatus.Value = false;
                status = $"Email failed to send: {e.StatusCode} {e.Message}";

                if (CanRetrySendingMessage(message))
                    EnqueueFailedMessage(message);
            }
        }
    }

    private void SendQueuedMessage(PeriodicTask task)
    {
        if (failedMessagesQueue.Count == 0 || task.IsCancellationRequested) return;

        var message = failedMessagesQueue.Pop();
        if (CanRetrySendingMessage(message))
        {
            var retries = emailSettings.GetVariable("MaxRetriesOnFailure")?.Value ?? 0;
            //Log.Info($"Retry Sending email attempt {message.AttemptNumber} of {retries} for detection {message.DetectionTimestamp}");
            TrySendEmail(message, out var status);
        }
    }

    private void EnqueueFailedMessage(MailMessageWithRetries message)
    {
        failedMessagesQueue.Push(message);
    }

    private bool InitializeAndValidateSMTPParameters()
    {
        senderAddress = emailSettings.GetVariable("SenderEmailAddress")?.Value ?? "";
        if (string.IsNullOrEmpty(senderAddress))
        {
            Log.Error("EmailSenderLogic", "Invalid Sender Email address");
            return false;
        }

        senderPassword = emailSettings.GetVariable("SenderEmailPassword")?.Value ?? "";
        if (string.IsNullOrEmpty(senderPassword))
        {
            Log.Error("EmailSenderLogic", "Invalid sender password");
            return false;
        }

        smtpHostname = emailSettings.GetVariable("SMTPHostname")?.Value ?? "";
        if (string.IsNullOrEmpty(smtpHostname))
        {
            Log.Error("EmailSenderLogic", "Invalid SMTP hostname");
            return false;
        }

        smtpPort = emailSettings.GetVariable("SMTPPort")?.Value ?? 587;
        enableSSL = emailSettings.GetVariable("EnableSSL")?.Value ?? true;

        return true;
    }

    private bool CanRetrySendingMessage(MailMessageWithRetries message)
    {
        var maxRetries = emailSettings.GetVariable("MaxRetriesOnFailure")?.Value ?? 0;
        return maxRetries >= 0 && message.AttemptNumber <= maxRetries;
    }

    private class MailMessageWithRetries : MailMessage
    {
        public MailMessageWithRetries(MailAddress fromAddress, MailAddress toAddress, string detectionTimestamp)
            : base(fromAddress, toAddress)
        {
            DetectionTimestamp = detectionTimestamp;
        }

        public int AttemptNumber { get; set; } = 0;
        public string DetectionTimestamp { get; private set; }
    }

    private bool ValidateEmail(string receiverEmail, string emailSubject, string emailBody)
    {
        if (string.IsNullOrEmpty(emailSubject))
        {
            Log.Error("EmailSenderLogic", "Email subject is empty or malformed");
            return false;
        }

        if (string.IsNullOrEmpty(emailBody))
        {
            Log.Error("EmailSenderLogic", "Email body is empty or malformed");
            return false;
        }

        if (string.IsNullOrEmpty(receiverEmail))
        {
            Log.Error("EmailSenderLogic", "ReceiverEmail is empty or null");
            return false;
        }
        return true;
    }

    private void ValidateCertificate()
    {
        if (System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            ServicePointManager.ServerCertificateValidationCallback = (_, __, ___, ____) => { return true; };
    }

    private string BuildEmailMessage(List<PowerQualityData> events)
{
    string eventDetails = string.Join("\n", events.Select(e => $@"
        <tr>
            <td>{e.Record_Identifier}</td>
            <td>{e.Event_Type}</td>
            <td>{e.Event_Code}</td>
            <td>{e.Sub_Event_Code}</td>
            <td>{e.Sub_Event}</td>
            <td>{e.Local_Timestamp}</td>
            <td>{e.Event_Duration_mS:F3}</td>
            <td>{e.Min_or_Max}</td>
            <td>{e.Trip_Point}</td>
            <td>{e.Association_Timestamp}</td>
        </tr>"));

    string ipAddress = ipVariable?.Value?.ToString() ?? "Unknown IP";

    return $@"
        <!DOCTYPE html>
        <html lang='en'>
        <head>
            <meta charset='UTF-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0'>
            <title>Power Quality Event Notification - {deviceName}</title>
            <style>
                :root {{
                    --primary-color: #0072c6;
                    --secondary-color: #00457c;
                    --accent-color: #f0f7ff;
                    --border-color: #d0d0d0;
                    --text-primary: #333333;
                    --text-secondary: #666666;
                    --highlight-color: #ffeb3b;
                    --success-color: #28a745;
                    --warning-color: #ffc107;
                    --danger-color: #dc3545;
                }}
                
                * {{
                    box-sizing: border-box;
                    margin: 0;
                    padding: 0;
                }}
                
                body {{
                    font-family: 'Segoe UI', Arial, sans-serif;
                    color: #333333; /* Hardcoded --text-primary */
                    background-color: #f4f6f9;
                    line-height: 1.6;
                    padding: 20px;
                }}
                
                .email-container {{
                    max-width: 1000px;
                    margin: 0 auto;
                    background: #ffffff;
                    border-radius: 8px;
                    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
                    padding: 25px;
                    border-top: 5px solid #0072c6; /* Hardcoded --primary-color */
                }}
                
                .header {{
                    display: flex;
                    justify-content: space-between;
                    align-items: flex-start;
                    margin-bottom: 25px;
                    border-bottom: 1px solid #d0d0d0; /* Hardcoded --border-color */
                    padding-bottom: 15px;
                }}
                
                .header-left {{
                    flex: 1;
                }}
                
                .header-right {{
                    text-align: right;
                    color: #666666; /* Hardcoded --text-secondary */
                    font-size: 0.9rem;
                }}
                
                h2 {{
                    color: #0072c6; /* Hardcoded --primary-color */
                    font-weight: 600;
                    font-size: 1.6rem;
                    margin-bottom: 10px;
                }}
                
                h3 {{
                    color: #00457c; /* Hardcoded --secondary-color */
                    font-size: 1.2rem;
                    margin: 15px 0 10px 0;
                }}
                
                .device-info {{
                    background-color: #f0f7ff; /* Hardcoded --accent-color */
                    border-left: 4px solid #0072c6; /* Hardcoded --primary-color */
                    padding: 12px 15px;
                    margin-bottom: 20px;
                    border-radius: 4px;
                }}
                
                .device-info p {{
                    margin: 5px 0;
                    font-size: 0.95rem;
                }}
                
                .device-info b {{
                    color: #00457c; /* Hardcoded --secondary-color */
                    display: inline-block;
                    width: 120px;
                }}
                
                .events-summary {{
                    margin: 20px 0;
                    padding: 12px 15px;
                    background-color: #fff4e5;
                    border-left: 4px solid #ffc107; /* Hardcoded --warning-color */
                    border-radius: 4px;
                    font-weight: 500;
                }}
                
                .table-container {{
                    overflow-x: auto;
                    margin: 20px 0;
                    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.05);
                    border-radius: 4px;
                }}
                
                table {{
                    width: 100%;
                    border-collapse: collapse;
                    font-size: 0.9rem;
                }}
                
                th {{
                    background-color: #0072c6; /* Hardcoded --primary-color */
                    color: white;
                    padding: 12px 10px;
                    text-align: left;
                    font-weight: 500;
                    white-space: nowrap;
                }}
                
                td {{
                    padding: 10px;
                    border-bottom: 1px solid #d0d0d0; /* Hardcoded --border-color */
                }}
                
                tr:nth-child(even) {{
                    background-color: #f0f7ff; /* Hardcoded --accent-color */
                }}
                
                .additional-info {{
                    margin-top: 30px;
                    padding: 15px;
                    background-color: #f8f9fa;
                    border: 1px solid #d0d0d0; /* Hardcoded --border-color */
                    border-radius: 8px;
                }}
                
                .additional-info h3 {{
                    margin-top: 0;
                    border-bottom: 1px solid #d0d0d0; /* Hardcoded --border-color */
                    padding-bottom: 8px;
                    margin-bottom: 12px;
                }}
                
                .resource-link {{
                    display: block;
                    margin-bottom: 8px;
                    color: #0072c6; /* Hardcoded --primary-color */
                    text-decoration: none;
                }}
                
                .resource-link:hover {{
                    text-decoration: underline;
                }}
                
                .timestamp {{
                    font-style: italic;
                    color: #666666; /* Hardcoded --text-secondary */
                    font-size: 0.85rem;
                    margin-top: 5px;
                }}
                
                .footer {{
                    margin-top: 30px;
                    border-top: 1px solid #d0d0d0; /* Hardcoded --border-color */
                    padding-top: 20px;
                }}
                
                .footer__bottom-bar {{
                    margin-top: 20px;
                }}
                
                .footer__bottom-bar_inner {{
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    flex-wrap: wrap;
                    padding: 10px 0;
                }}
                
                .footer__text-container {{
                    margin-right: 10px;
                }}
                
                .footer__copyright-text {{
                    color: #666666; /* Hardcoded --text-secondary */
                    font-size: 0.85rem;
                }}
                
                .footer__logo_separator {{
                    height: 20px;
                    border-left: 1px solid #d0d0d0; /* Hardcoded --border-color */
                    margin: 0 15px;
                }}
                
                .footer__logo {{
                    display: flex;
                    align-items: center;
                }}
                
                .footer__logo img {{
                    height: 25px;
                    background-color: #0072c6; /* Hardcoded --primary-color */
                    padding: 3px 5px;
                    border-radius: 3px;
                }}
            </style>
        </head>
        <body>
            <div class='email-container'>
                <div class='header'>
                    <div class='header-left'>
                        <h2>Power Quality Event Notification</h2>
                        <div class='timestamp'>Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}</div>
                    </div>
                    <div class='header-right'>
                        <p>Total Events: <b>{events.Count}</b></p>
                    </div>
                </div>
                
                <div class='device-info'>
                    <p><b>Device Name:</b> {deviceName}</p>
                    <p><b>IP Address:</b> {ipAddress}</p>
                </div>
                
                <div class='events-summary'>
                    <p>{events.Count} Power Quality {(events.Count == 1 ? "Event has" : "Events have")} been detected</p>
                </div>
                
                <div class='table-container'>
                    <table>
                        <thead>
                            <tr>
                                <th>Record ID</th>
                                <th>Event Type</th>
                                <th>Event Code</th>
                                <th>Sub Event Code</th>
                                <th>Sub Event</th>
                                <th>Start Time</th>
                                <th>Duration (ms)</th>
                                <th>Min/Max</th>
                                <th>Trip Point</th>
                                <th>Association Time</th>
                            </tr>
                        </thead>
                        <tbody>
                            {eventDetails}
                        </tbody>
                    </table>
                </div>

                <div class='additional-info'>
                    <h3>Additional Information</h3>
                    <div>
                        <a class='resource-link' href='https://literature.rockwellautomation.com/idc/groups/literature/documents/um/1426-um001_-en-p.pdf' target='_blank'>
                            PowerMonitor 5000 Unit User Manual - Literature Library
                        </a>
                        <a class='resource-link' href='https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/device-rm100_-en-p.pdf' target='_blank'>
                            Power Device Library
                        </a>
                    </div>
                </div>
                
                <div class='footer'>
                    <div class='footer__bottom-bar'>
                        <div class='footer__bottom-bar_inner'>
                            <div class='footer__text-container'>
                                <div class='footer__copyright-text'>© 2025 <a href='https://www.rockwellautomation.com/en-us.html' style='color: #666666; text-decoration: none;'>Rockwell Automation</a></div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </body>
        </html>";
}

    private List<PowerQualityData> LoadEventsFromJson()
    {
        if (File.Exists(jsonFilePath))
        {
            string json = File.ReadAllText(jsonFilePath);
            return System.Text.Json.JsonSerializer.Deserialize<List<PowerQualityData>>(json) ?? new List<PowerQualityData>();
        }
        Log.Warning("EmailSenderLogic", $"JSON file not found at: {jsonFilePath}");
        return new List<PowerQualityData>();
    }

    private void ClearEventsFromJson()
    {
        if (File.Exists(jsonFilePath))
        {
            File.WriteAllText(jsonFilePath, "[]"); // Clear the JSON file
        }
    }

    private void LogEmailActivity(string recipient, string subject, string status)
    {
        var logEntry = new EmailLogEntry
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Recipient = recipient,
            Subject = subject,
            Status = status,
            DeviceName = deviceName
        };

        List<EmailLogEntry> logs = LoadEmailLogs();
        logs.Add(logEntry);

        // Keep only the last 50 logs
        if (logs.Count > 50)
        {
            logs = logs.Skip(logs.Count - 50).ToList();
        }

        // Save back to JSON
        string json = System.Text.Json.JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true });
        // Ensure the directory exists
        // Check if directory exists, create it if it doesn't
        string directoryPath = Path.GetDirectoryName(emailLogFilePath);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
        
        //Directory.CreateDirectory(emailLogFilePath);
        File.WriteAllText(emailLogFilePath, json);
    }

    private List<EmailLogEntry> LoadEmailLogs()
    {
        if (File.Exists(emailLogFilePath))
        {
            string json = File.ReadAllText(emailLogFilePath);
            return System.Text.Json.JsonSerializer.Deserialize<List<EmailLogEntry>>(json) ?? new List<EmailLogEntry>();
        }
        return new List<EmailLogEntry>();
    }
}


public class EmailLogEntry 
{
public string Timestamp { get; set; } 
public string Recipient { get; set; } 
public string Subject { get; set; } 
public string Status { get; set; } 
public string DeviceName { get; set; } 
}
