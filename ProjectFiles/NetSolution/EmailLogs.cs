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
using System.IO; 
using System.Text.Json;
using System.Collections.Generic; 
using System.Linq;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class EmailLogs : BaseNetLogic
{
    
    private string deviceName; 
    private string emailLogFilePath; 
    private WebBrowser webBrowser;
    
    public override void Start()
    {
        // Get the Tag alias
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("EmailLogs", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("EmailLogs", "Device name is null or empty");
            return;
        }

        // Set up file paths
        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        emailLogFilePath = Path.Combine(baseDirectory, deviceName, "NewEventsDetection", $"{deviceName}_EmailLog.json");

        // Get the WebBrowser control
        webBrowser = Owner.Get<WebBrowser>("Webbrowser_Email");
        if (webBrowser == null)
        {
            Log.Error("EmailLogs", "WebBrowser control not found");
            return;
        }

        // Generate and set the HTML file
        GenerateEmailLogHtml();
    }

    public override void Stop()
    {
        // Cleanup if needed
    }

    private void GenerateEmailLogHtml()
    {
        // Read email logs from JSON
        List<EmailLogEntry> logs = LoadEmailLogs();
        
        // Initialize HTML content for table or no-logs message
        string logContent;
        if (logs == null || logs.Count == 0)
        {
            logContent = @"
                <div class='no-logs-message' style='text-align: center; padding: 30px;'>
                    <h3 style='color: var(--text-secondary); font-weight: 500; margin-bottom: 10px;'>No Email Logs Available</h3>
                    <p style='color: var(--text-secondary);'>No email logs have been recorded for this device yet.</p>
                </div>";
            Log.Warning("EmailLogs", "No email logs found");
        }
        else
        {
            logContent = $@"
                <div class='table-container'>
                    <table class='log-table'>
                        <thead>
                            <tr>
                                <th>Timestamp</th>
                                <th>Recipient</th>
                                <th>Subject</th>
                                <th>Status</th>
                                <th>Device Name</th>
                            </tr>
                        </thead>
                        <tbody>
                            {string.Join("", logs.Select(log => $@"
                                <tr>
                                    <td>{log.Timestamp}</td>
                                    <td>{log.Recipient}</td>
                                    <td>{log.Subject}</td>
                                    <td class='{(log.Status.ToLower().Contains("success") ? "status-success" : 
                                                 log.Status.ToLower().Contains("fail") ? "status-error" : "")}'>{log.Status}</td>
                                    <td>{log.DeviceName}</td>
                                </tr>"))}
                        </tbody>
                    </table>
                </div>";
        }

        // Generate HTML content with improved styling
        string htmlContent = $@"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <title>Email Logs - {deviceName}</title>
                <style>
                    :root {{
                        --primary-color: #475CA7;
                        --secondary-color: #00457c;
                        --accent-color: #f0f7ff;
                        --border-color: #D8D8D8;
                        --text-primary: #333333;
                        --text-secondary: #666666;
                        --success-color: #28a745;
                        --error-color: #dc3545;
                    }}
                    
                    * {{
                        box-sizing: border-box;
                        margin: 0;
                        padding: 0;
                    }}
                    
                    body {{
                        font-family: 'Segoe UI', Arial, sans-serif;
                        color: var(--text-primary);
                        background-color: #E0E0E0;
                        line-height: 1.6;
                        padding: 20px;
                    }}
                    
                    .container {{
                        max-width: 100%;
                        margin: 0 auto;
                        background: #E8E8E8;
                        border-radius: 8px;
                        box-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
                        padding: 25px;
                        border-top: 5px solid var(--primary-color);
                    }}
                    
                    .header {{
                        margin-bottom: 25px;
                        border-bottom: 1px solid var(--border-color);
                        padding-bottom: 15px;
                    }}
                    
                    h2 {{
                        color: var(--primary-color);
                        font-weight: 600;
                        font-size: 1.5rem;
                        margin-bottom: 10px;
                    }}
                    
                    .timestamp {{
                        color: var(--text-secondary);
                        font-size: 0.85rem;
                        display: block;
                        margin-bottom: 5px;
                    }}
                    
                    .info-section {{
                        margin-bottom: 30px;
                    }}
                    
                    .log-table {{
                        width: 100%;
                        border-collapse: collapse;
                        margin-top: 15px;
                        font-size: 0.95rem;
                        box-shadow: 0 1px 3px rgba(0, 0, 0, 0.05);
                    }}
                    
                    .log-table th {{
                        background-color: var(--primary-color);
                        color: white;
                        padding: 12px 15px;
                        text-align: left;
                        font-weight: 500;
                        position: sticky;
                        top: 0;
                    }}
                    
                    .log-table td {{
                        padding: 10px 15px;
                        border-bottom: 1px solid var(--border-color);
                    }}
                    
                    .log-table tr:nth-child(even) {{
                        background-color: var(--accent-color);
                    }}
                    
                    .log-table tr:hover {{
                        background-color: #eaf5ff;
                    }}
                    
                    .status-success {{
                        color: var(--success-color);
                        font-weight: 500;
                    }}
                    
                    .status-error {{
                        color: var(--error-color);
                        font-weight: 500;
                    }}
                    
                    .footer {{
                        margin-top: 30px;
                        border-top: 1px solid var(--border-color);
                        padding-top: 20px;
                        font-size: 0.9rem;
                        color: var(--text-secondary);
                    }}
                    
                    .resources {{
                        margin-top: 15px;
                    }}
                    
                    .resources h3 {{
                        font-size: 1rem;
                        margin-bottom: 10px;
                        color: var(--primary-color);
                    }}
                    
                    .resource-link {{
                        display: block;
                        margin-bottom: 8px;
                        color: var(--primary-color);
                        text-decoration: none;
                    }}
                    
                    .resource-link:hover {{
                        text-decoration: underline;
                    }}
                    
                    .table-container {{
                        overflow-x: auto;
                        max-height: 500px;
                        overflow-y: auto;
                    }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'>
                        <h2>Email Logs for {deviceName}</h2>
                        <span class='timestamp'>Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}</span>
                    </div>
                    
                    {logContent}
                    
                    <div class='footer'>
                        <div class='resources'>
                            <h3>Additional Resources</h3>
                            <a class='resource-link' href='https://literature.rockwellautomation.com/idc/groups/literature/documents/um/1426-um001_-en-p.pdf' target='_blank'>
                                PowerMonitor 5000 Unit User Manual - Literature Library
                            </a>
                            <a class='resource-link' href='https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/device-rm100_-en-p.pdf' target='_blank'>
                                Power Device Library
                            </a>
                        </div>
                    </div>
                </div>
            </body>
            </html>";

        // Define the HTML file path
        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string emaillogHtmlPath = Path.Combine(baseDirectory, deviceName, "NewEventsDetection", $"EmailLogs_{deviceName}.html");

        // Ensure the res directory exists
        string directoryPath = Path.GetDirectoryName(emaillogHtmlPath);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        // Write the HTML file
        File.WriteAllText(emaillogHtmlPath, htmlContent);

        // Set the WebBrowser URL
        var deviceHtmlUri = ResourceUri.FromProjectRelativePath(emaillogHtmlPath);
        webBrowser.URL = deviceHtmlUri;
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
