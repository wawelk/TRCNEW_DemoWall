#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.RAEtherNetIP;
using FTOptix.MicroController;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using Newtonsoft.Json; // Add Newtonsoft.Json for JSON serialization
using System.IO; // Add System.IO for file operations
using System.Linq;
using System.Collections.Generic;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Threading.Tasks;
using System.Net.Http;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using DeviceLogin; 
#endregion

public class Log_AlarmTypes : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private LongRunningTask downloadTask;
    private Label logLabel;

    public override void Start()
    {
        
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Log_AlarmTypes", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_AlarmTypes", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_AlarmTypes", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_AlarmTypes", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_AlarmTypes", "Label 'log' not found");
            return;
        }

        // Initial download on tab open
        StartDownload();
    }

    public override void Stop()
    {
        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }
    }

    [ExportMethod]
    public void DownloadLogFileAsync()
    {
        // Trigger download via button using global IP
        StartDownload();
    }

   private void StartDownload()
    {
        // Safety check for IP address
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_AlarmTypes", "IP address not set, cannot start download");
            if (logLabel != null) logLabel.Text = "Failed: IP not set";
            return;
        }

        // If a task exists, dispose it before starting a new one
        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }

        downloadTask = new LongRunningTask(task => DownloadTaskMethod(task), LogicObject);
        downloadTask.Start();
    }

    private void DownloadTaskMethod(LongRunningTask task)
    {
        string remoteFilePath = "/LoggingResults/Alarm_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, deviceName);
        string localFilePath = Path.Combine(localFolder, "Alarm_Log.csv");

        try
        {
            
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_AlarmTypes-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            Directory.CreateDirectory(localFolder);

            logLabel.Text = "Downloading...";
            //Log.Info("Log_AlarmTypes", $"Starting HTTP download from {url}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                byte[] fileBytes = client.GetByteArrayAsync(url).Result;

                // Check for cancellation before proceeding
                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_AlarmTypes", "Download canceled");
                    logLabel.Text = "Canceled";
                    return;
                }

                File.WriteAllBytes(localFilePath, fileBytes);
                //Log.Info("Log_AlarmTypes", $"File downloaded to {localFilePath}");

                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_AlarmTypes", "Download canceled after file write");
                    logLabel.Text = "Canceled";
                    return;
                }

                if (File.Exists(localFilePath))
                {
                    logLabel.Text = "Processing...";
                    ProcessAlarmLog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_AlarmTypes", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_AlarmTypes", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
        }
    }

    private IUAVariable GetVariableValue(string variableName)
        {
            var variable = LogicObject.GetVariable(variableName);
            if (variable == null)
            {
                Log.Error($"{variableName} not found");
                return null;
            }
            return variable;
        }

    private class AlarmData
    {
        public int Alarm_Record_Identifier { get; set; }
        public string Alarm_Timestamp { get; set; }
        public int Alarm_Type_Code { get; set; }
        public string Alarm_Type_Description { get; set; }
        public int Alarm_Code { get; set; }
        public string Alarm_Code_Description { get; set; }
    }

    // Alarm Type Dictionary
   private readonly Dictionary<int, (string AlarmTypeDescription, Dictionary<int, string> AlarmCodes)> alarmDictionary = new()
    {
        {
            1, // AlarmTypeCode
            (
                "Metering Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "Virtual Wiring Correction" },
                    { 2, "Volts Loss V1" },
                    { 4, "Volts Loss V2" },
                    { 8, "Volts Loss V3" },
                    { 16, "Voltage Over Range Indication" },
                    { 32, "Ampere Over Range Indication" },
                    { 64, "Wiring Diagnostics Active" }
                }
            )
        },
        {
            2, // AlarmTypeCode
            (
                "Over Range Information", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "V1G Over Range" },
                    { 2, "V2G Over Range" },
                    { 4, "V3G Over Range" },
                    { 8, "VNG Over Range" },
                    { 16, "I1 Over Range" },
                    { 32, "I2 Over Range" },
                    { 64, "I3 Over Range" },
                    { 128, "I4 Over Range" }
                }
            )
        },
        {
            4, // AlarmTypeCode
            (
                "Power Quality Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "Sag Indication Detected" },
                    { 2, "Swell Indication Detected" },
                    { 4, "Transient Indication" },
                    { 8, "200mS Sag Swell Status Flag" },
                    { 16, "3s Sag Swell Status Flag" },
                    { 32, "10m Sag Swell Status Flag" },
                    { 64, "2h Sag Swell Status Flag" }
                }
            )
        },
        {
            8, // AlarmTypeCode
            (
                "Logs Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "Data Log Full Fill And Stop" },
                    { 2, "Event Log Full Fill And Stop" },
                    { 4, "Setpoint Log Full Fill And Stop" },
                    { 8, "PowerDuality Log Full Fill And Stop" },
                    { 16, "Energy Log Full Fill And Stop" },
                    { 32, "Waveform Full" },
                    { 64, "TriggerData Full Fill And Stop" }
                }
            )
        },
        {
            16, // AlarmTypeCode
            (
                "Output Pulse Overrun", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "KYZ Pulse Overrun" },
                    { 2, "Relay1 Pulse Overrun" },
                    { 4, "Relay2 Pulse Overrun" },
                    { 8, "Relay3 Pulse Overrun" }
                }
            )
        },
        {
            32, // AlarmTypeCode
            (
                "IEEE1159 Over/UnderVoltage Imbalance", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "IEEE1159 Over Voltage V1" },
                    { 2, "IEEE1159 Over Voltage V2" },
                    { 4, "IEEE1159 Over Voltage V3" },
                    { 8, "IEEE1159 Under Voltage V1" },
                    { 16, "IEEE1159 Under Voltage V2" },
                    { 32, "IEEE1159 Under Voltage V3" },
                    { 64, "IEEE1159 Imbalance Condition Volts" },
                    { 128, "IEEE1159 Imbalance Condition Current" }
                }
            )
        },
        {
            64, // AlarmTypeCode
            (
                "IEEE1159 DCOffset THD Frequency Condition", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "IEEE1159 DCOffset Condition V1" },
                    { 2, "IEEE1159 DCOffset Condition V2" },
                    { 4, "IEEE1159 DCOffset Condition V3" },
                    { 8, "IEEE1159 Voltage THD Condition V1" },
                    { 16, "IEEE1159 Voltage THD Condition V2" },
                    { 32, "IEEE1159 Voltage THD Condition V3" },
                    { 64, "IEEE1159 Current THD Condition I1" },
                    { 128, "IEEE1159 Current THD Condition I2" },
                    { 256, "IEEE1159 Current THD Condition I3" },
                    { 512, "IEEE1159 PowerFrequency Condition" },
                    { 1024, "IEEE1159 Current THD Condition I4" }
                }
            )
        },
        {
            65, // AlarmTypeCode
            (
                "IEEE1159 TID Condition", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "IEEE1159 Voltage TID Condition V1" },
                    { 2, "IEEE1159 Voltage TID Condition V2" },
                    { 4, "IEEE1159 Voltage TID Condition V3" },
                    { 8, "IEEE1159 Current TID Condition I1" },
                    { 16, "IEEE1159 Current TID Condition I2" },
                    { 32, "IEEE1159 Current TID Condition I3" },
                    { 64, "IEEE1159 Current TID Condition I4" }
                }
            )
        },
        {
            128, // AlarmTypeCode
            (
                "IEEE1159 Overall Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "ShortTerm TDD THD PASS FAIL" },
                    { 2, "LongTerm TDD THD PASS FAIL" },
                    { 4, "ShortTerm Individual Harmonic PASS FAIL" },
                    { 8, "LongTerm Individual Harmonic PASS FAIL" }
                }
            )
        },
        {
            256, // AlarmTypeCode
            (
                "ShortTerm 2nd To 17th Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "2nd Harmonic PASS FAIL" },
                    { 2, "3rd Harmonic PASS FAIL" },
                    { 4, "4th Harmonic PASS FAIL" },
                    { 8, "5th Harmonic PASS FAIL" },
                    { 16, "6th Harmonic PASS FAIL" },
                    { 32, "7th Harmonic PASS FAIL" },
                    { 64, "8th Harmonic PASS FAIL" },
                    { 128, "9th Harmonic PASS FAIL" },
                    { 256, "10th Harmonic PASS FAIL" },
                    { 512, "11th Harmonic PASS FAIL" },
                    { 1024, "12th Harmonic PASS FAIL" },
                    { 2048, "13th Harmonic PASS FAIL" },
                    { 4096, "14th Harmonic PASS FAIL" },
                    { 8192, "15th Harmonic PASS FAIL" },
                    { 16384, "16th Harmonic PASS FAIL" },
                    { 32768, "17th Harmonic PASS FAIL" }
                }
            )
        },
        {
            512, // AlarmTypeCode
            (
                "ShortTerm 18th To 33rd Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "18th Harmonic PASS FAIL" },
                    { 2, "19th Harmonic PASS FAIL" },
                    { 4, "20th Harmonic PASS FAIL" },
                    { 8, "21st Harmonic PASS FAIL" },
                    { 16, "22nd Harmonic PASS FAIL" },
                    { 32, "23rd Harmonic PASS FAIL" },
                    { 64, "24th Harmonic PASS FAIL" },
                    { 128, "25th Harmonic PASS FAIL" },
                    { 256, "26th Harmonic PASS FAIL" },
                    { 512, "27th Harmonic PASS FAIL" },
                    { 1024, "28th Harmonic PASS FAIL" },
                    { 2048, "29th Harmonic PASS FAIL" },
                    { 4096, "30th Harmonic PASS FAIL" },
                    { 8192, "31st Harmonic PASS FAIL" },
                    { 16384, "32nd Harmonic PASS FAIL" },
                    { 32768, "33rd Harmonic PASS FAIL" }
                }
            )
        },
        {
            1024, // AlarmTypeCode
            (
                "ShortTerm 34th To 40th Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "34th Harmonic PASS FAIL" },
                    { 2, "35th Harmonic PASS FAIL" },
                    { 4, "36th Harmonic PASS FAIL" },
                    { 8, "37th Harmonic PASS FAIL" },
                    { 16, "38th Harmonic PASS FAIL" },
                    { 32, "39th Harmonic PASS FAIL" },
                    { 64, "40th Harmonic PASS FAIL" }
                }
            )
        },
        {
            2048, // AlarmTypeCode
            (
                "LongTerm 2nd To 17th Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "2nd Harmonic PASS FAIL" },
                    { 2, "3rd Harmonic PASS FAIL" },
                    { 4, "4th Harmonic PASS FAIL" },
                    { 8, "5th Harmonic PASS FAIL" },
                    { 16, "6th Harmonic PASS FAIL" },
                    { 32, "7th Harmonic PASS FAIL" },
                    { 64, "8th Harmonic PASS FAIL" },
                    { 128, "9th Harmonic PASS FAIL" },
                    { 256, "10th Harmonic PASS FAIL" },
                    { 512, "11th Harmonic PASS FAIL" },
                    { 1024, "12th Harmonic PASS FAIL" },
                    { 2048, "13th Harmonic PASS FAIL" },
                    { 4096, "14th Harmonic PASS FAIL" },
                    { 8192, "15th Harmonic PASS FAIL" },
                    { 16384, "16th Harmonic PASS FAIL" },
                    { 32768, "17th Harmonic PASS FAIL" }
                }
            )
        },
        {
            4096, // AlarmTypeCode
            (
                "LongTerm 18th To 33rd Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "18th Harmonic PASS FAIL" },
                    { 2, "19th Harmonic PASS FAIL" },
                    { 4, "20th Harmonic PASS FAIL" },
                    { 8, "21st Harmonic PASS FAIL" },
                    { 16, "22nd Harmonic PASS FAIL" },
                    { 32, "23rd Harmonic PASS FAIL" },
                    { 64, "24th Harmonic PASS FAIL" },
                    { 128, "25th Harmonic PASS FAIL" },
                    { 256, "26th Harmonic PASS FAIL" },
                    { 512, "27th Harmonic PASS FAIL" },
                    { 1024, "28th Harmonic PASS FAIL" },
                    { 2048, "29th Harmonic PASS FAIL" },
                    { 4096, "30th Harmonic PASS FAIL" },
                    { 8192, "31st Harmonic PASS FAIL" },
                    { 16384, "32nd Harmonic PASS FAIL" },
                    { 32768, "33rd Harmonic PASS FAIL" }
                }
            )
        },
        {
            8192, // AlarmTypeCode
            (
                "LongTerm 34th To 40th Harmonic Status", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "34th Harmonic PASS FAIL" },
                    { 2, "35th Harmonic PASS FAIL" },
                    { 4, "36th Harmonic PASS FAIL" },
                    { 8, "37th Harmonic PASS FAIL" },
                    { 16, "38th Harmonic PASS FAIL" },
                    { 32, "39th Harmonic PASS FAIL" },
                    { 64, "40th Harmonic PASS FAIL" }
                }
            )
        },
        {
            16384, // AlarmTypeCode
            (
                "IEEE1159 Voltage Fluctuation Condition", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "IEEE1159 Voltage Fluctuation Condition V1" },
                    { 2, "IEEE1159 Voltage Fluctuation Condition V2" },
                    { 4, "IEEE1159 Voltage Fluctuation Condition V3" }
                }
            )
        },
        {
            32768, // AlarmTypeCode
            (
                "EN61000 4 30 Plains Signal Condition", // AlarmTypeDescription
                new Dictionary<int, string> // AlarmCodes
                {
                    { 1, "EN61000 4 30 Mains Signal Condition V1" },
                    { 2, "EN61000 4 30 Mains Signal Condition V2" },
                    { 4, "EN61000 4 30 Mains Signal Condition V3" },
                    { 8, "EN61000 4 30 Under Deviation V1" },
                    { 16, "EN61000 4 30 Under Deviation V2" },
                    { 32, "EN61000 4 30 Under Deviation V3" },
                    { 64, "EN61000 4 30 Over Deviation V1" },
                    { 128, "EN61000 4 30 Over Deviation V2" },
                    { 256, "EN61000 4 30 Over Deviation V3" }
                }
            )
        }
    };
    
    public void ProcessAlarmLog()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, deviceName, "Alarm_Log.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("Alarm Log file not found at: " + csvFilePath);
                return;
            }

            //Log.Info("Processing Alarm Log from: " + csvFilePath);

            var alarmDataList = ProcessCsvFile(csvFilePath);
            UpdateDashboard(baseDirectory, alarmDataList);
        }
        catch (Exception ex)
        {
            Log.Error($"Error in ProcessAlarmLog: {ex.Message}");
        }
    }

    private List<AlarmData> ProcessCsvFile(string csvFilePath)
{
    var lines = File.ReadAllLines(csvFilePath);
    var alarmDataList = new List<AlarmData>();

    foreach (var line in lines.Skip(1)) // Skip header
    {
        var row = line.Split(',');
        var alarmTypeCode = int.Parse(row[5]); // AlarmTypeCode from CSV
        var alarmCodeSum = int.Parse(row[6]);  // Summed AlarmCode from CSV
        var timestamp = FormatTimestamp(row[1], row[2], row[3], row[4]);
        var recordId = int.Parse(row[0]);

        // Get the alarm codes for this alarm type from the dictionary
        if (alarmDictionary.TryGetValue(alarmTypeCode, out var alarmTypeInfo))
        {
            // Decompose the summed alarm code into individual codes using bitwise operations
            foreach (var possibleCode in alarmTypeInfo.AlarmCodes.Keys)
            {
                // Check if this bit is set in the summed alarm code
                if ((alarmCodeSum & possibleCode) == possibleCode)
                {
                    // Create a new AlarmData entry for this individual alarm code
                    alarmDataList.Add(new AlarmData
                    {
                        Alarm_Record_Identifier = recordId,
                        Alarm_Timestamp = timestamp,
                        Alarm_Type_Code = alarmTypeCode,
                        Alarm_Type_Description = alarmTypeInfo.AlarmTypeDescription,
                        Alarm_Code = possibleCode,
                        Alarm_Code_Description = alarmTypeInfo.AlarmCodes[possibleCode]
                    });
                }
            }
        }
        else
        {
            // If the alarm type code isn't in the dictionary, log it as unknown
            alarmDataList.Add(new AlarmData
            {
                Alarm_Record_Identifier = recordId,
                Alarm_Timestamp = timestamp,
                Alarm_Type_Code = alarmTypeCode,
                Alarm_Type_Description = $"Unknown Alarm Type Code ({alarmTypeCode})",
                Alarm_Code = alarmCodeSum,
                Alarm_Code_Description = $"Unknown Alarm Code ({alarmCodeSum}) for Alarm Type Code {alarmTypeCode}"
            });
        }
    }

    return alarmDataList;
}

    private string GetAlarmTypeDescription(int alarmTypeCode)
    {
        if (alarmDictionary.TryGetValue(alarmTypeCode, out var alarmTypeInfo))
        {
            return alarmTypeInfo.AlarmTypeDescription;
        }
        return $"Unknown Alarm Type Code ({alarmTypeCode})";
    }

    private string GetAlarmCodeDescription(int alarmTypeCode, int alarmCode)
    {
        if (alarmDictionary.TryGetValue(alarmTypeCode, out var alarmTypeInfo) &&
            alarmTypeInfo.AlarmCodes.TryGetValue(alarmCode, out var description))
        {
            return description;
        }
        return $"Unknown Alarm Code ({alarmCode}) for Alarm Type Code {alarmTypeCode}";
    }

    private void UpdateDashboard(string baseDirectory, List<AlarmData> alarmDataList)
    {
        var chartData = new
        {
            alarms = alarmDataList.Select(a => new
            {
                id = a.Alarm_Record_Identifier,
                timestamp = a.Alarm_Timestamp,
                alarmTypeCode = a.Alarm_Type_Code,
                alarmTypeDescription = a.Alarm_Type_Description,
                alarmCode = a.Alarm_Code,
                alarmCodeDescription = a.Alarm_Code_Description
            }).ToArray()
        };

        string jsonData = JsonConvert.SerializeObject(chartData, new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.EscapeHtml
        });

        string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_AlarmDashboard.html");
        string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData); // Returns the path to the updated file

        var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        if (webBrowser != null)
        {
            string relativePath = $"res/Template_AlarmDashboard_{deviceName}.html";
            var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
            webBrowser.URL = templatePath;

            //Log.Info($"WebBrowser URL set to: {relativePath}");
        }
        else
        {
            Log.Error("WebBrowser control not found in the UI");
        }
    }

    private string UpdateHtmlTemplate(string templatePath, string jsonData)
    {
        // Define the output file path in the same res folder
        string resDirectory = Path.Combine(Path.GetDirectoryName(templatePath)); // Gets res folder
        string templateName = Path.GetFileNameWithoutExtension(templatePath); // e.g., "AlarmDashboard"
        string outputFileName = $"{templateName}_{deviceName}.html"; // e.g., "AlarmDashboard_Device1.html"
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error($"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            string pattern = @"var\s+alarmData\s*=\s*\{[^;]*\};";
            string replacement = $"var alarmData = {jsonData};";

            string updatedContent = System.Text.RegularExpressions.Regex.Replace(
                htmlContent,
                pattern,
                replacement
            );

            if (htmlContent.Equals(updatedContent))
            {
                Log.Warning("No changes made to HTML content. Check if pattern exists in template.");
            }

            File.WriteAllText(deviceHtmlPath, updatedContent);
            //Log.Info($"{outputFileName} updated successfully at: {deviceHtmlPath}");

            return deviceHtmlPath; // Return the path to the new file
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to update HTML template: {ex.Message}");
            return null;
        }
    }

    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            int monthDayInt = int.Parse(monthDay);
            string month = (monthDayInt / 100).ToString().PadLeft(2, '0');
            string day = (monthDayInt % 100).ToString().PadLeft(2, '0');

            int hourMinInt = int.Parse(hourMinute);
            string hour = (hourMinInt / 100).ToString().PadLeft(2, '0');
            string minute = (hourMinInt % 100).ToString().PadLeft(2, '0');

            int secMsInt = int.Parse(secMilliSec);
            string seconds = (secMsInt / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (secMsInt % 1000).ToString().PadLeft(3, '0');

            return $"{year}-{month}-{day} {hour}:{minute}:{seconds}.{milliseconds}";
        }
        catch (Exception ex)
        {
            Log.Error($"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
    }
}
