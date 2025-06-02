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
using System.Net;
using System.Net.Http;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using DeviceLogin;
#endregion

public class Log_MinMax : BaseNetLogic
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
            Log.Error("Log_MinMax", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_MinMax", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_MinMax", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_MinMax", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_MinMax", "Label 'log' not found");
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
            Log.Error("Log_MinMax", "IP address not set, cannot start download");
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
        string remoteFilePath = "/LoggingResults/Min_Max_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, deviceName);
        string localFilePath = Path.Combine(localFolder, "Min_Max_Log.csv");

        try
        {
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_EventTypes-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            Directory.CreateDirectory(localFolder);

            logLabel.Text = "Downloading...";
            //Log.Info("Log_MinMax", $"Starting HTTP download from {url}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                byte[] fileBytes = client.GetByteArrayAsync(url).Result;

                // Check for cancellation before proceeding
                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_MinMax", "Download canceled");
                    logLabel.Text = "Canceled";
                    return;
                }

                File.WriteAllBytes(localFilePath, fileBytes);
                //Log.Info("Log_MinMax", $"File downloaded to {localFilePath}");

                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_MinMax", "Download canceled after file write");
                    logLabel.Text = "Canceled";
                    return;
                }

                if (File.Exists(localFilePath))
                {
                    logLabel.Text = "Processing...";
                    ProcessMinMaxLog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_MinMax", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_MinMax", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
        }
    }
    
    private class ParameterData
    {
        public int ParameterNumber { get; set; }
        public string ParameterName { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public string MinTimestamp { get; set; }
        public string MaxTimestamp { get; set; }
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

    public void ProcessMinMaxLog()
    {
        try
        {
            FTOptix.Core.ResourceUri filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, deviceName, "Min_Max_Log.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("Min/Max Log file not found at: " + csvFilePath);
                return;
            }

            //Log.Info("Min/Max Log file found at: " + csvFilePath);

            // Read and process CSV file
            var lines = File.ReadAllLines(csvFilePath);
            var headers = lines[0].Split(',');
            var data = lines.Skip(1).Select(line => line.Split(',')).ToArray();

            var parameterDataList = data.Select(row => new ParameterData
            {
                ParameterNumber = int.Parse(row[0]),
                ParameterName = GetParameterName(int.Parse(row[0])), // Use the mapping function
                MinValue = double.Parse(row[2]),
                MaxValue = double.Parse(row[3]),
                MinTimestamp = FormatTimestamp(row[4], row[5], row[6], row[7]),
                MaxTimestamp = FormatTimestamp(row[8], row[9], row[10], row[11])
            }).ToList();

            var chartData = new
            {
                parameters = parameterDataList.Select(p => new
                {
                    number = p.ParameterNumber,
                    name = p.ParameterName,
                    minValue = p.MinValue,
                    maxValue = p.MaxValue,
                    minTimestamp = p.MinTimestamp,
                    maxTimestamp = p.MaxTimestamp
                }).ToArray()
            };

            string jsonData = JsonConvert.SerializeObject(chartData, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml
            });

            // Update the HTML file
            string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_MinMaxDashboard.html");
            string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData); // New method to handle HTML update

            // Refresh the WebBrowser control with the new file
            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser != null)
            {
                string relativePath = $"res/Template_MinMaxDashboard_{deviceName}.html";
                var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                webBrowser.URL = templatePath;

                //Log.Info($"WebBrowser URL set to: {relativePath}");
            }
            else
            {
                Log.Error("WebBrowser control not found in the UI");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error in ProcessMinMaxLog: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log.Error($"Inner Exception: {ex.InnerException.Message}");
            }
        }
    }

    private string UpdateHtmlTemplate(string templatePath, string jsonData)
    {
        // Define the output file path in the same res folder
        string resDirectory = Path.Combine(Path.GetDirectoryName(templatePath)); // Gets res folder
        string templateName = Path.GetFileNameWithoutExtension(templatePath); // e.g., "MinMaxDashboard"
        string outputFileName = $"{templateName}_{deviceName}.html"; // e.g., "MinMaxDashboard_Device1.html"
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error($"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            string pattern = @"var\s+dashboardData\s*=\s*\{[^;]*\};";
            string replacement = $"var dashboardData = {jsonData};";

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

    private Dictionary<int, string> GetParameterMappings()
    {
        return new Dictionary<int, string>
        {
            { 1, "V1_N_Volts" },
            { 2, "V2_N_Volts" },
            { 3, "V3_N_Volts" },
            { 4, "VN_G_Volts" },
            { 5, "Avg_V_N_Volts" },
            { 6, "V12_Volts" },
            { 7, "V23_Volts" },
            { 8, "V31_Volts" },
            { 9, "Avg_V_Volts" },
            { 10, "I1_Amps" },
            { 11, "I2_Amps" },
            { 12, "I3_Amps" },
            { 13, "IN_Amps" },
            { 14, "Avg_I_Amps" },
            { 15, "Total_kW" },
            { 16, "Total_kVAR" },
            { 17, "Total_kVA" },
            { 18, "L1_kW" },
            { 19, "L2_kW" },
            { 20, "L3_kW" },
            { 21, "L1_kVAR" },
            { 22, "L2_kVAR" },
            { 23, "L3_kVAR" },
            { 24, "L1_kVA" },
            { 25, "L2_kVA" },
            { 26, "L3_kVA" }
        };
    }

    private string GetParameterName(int parameterNumber)
    {
        var mappings = GetParameterMappings();
        return mappings.ContainsKey(parameterNumber) 
            ? mappings[parameterNumber] 
            : $"Parameter {parameterNumber}";
    }

    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            // Convert monthDay (614 means 06/14)
            int monthDayInt = int.Parse(monthDay);
            string month = (monthDayInt / 100).ToString().PadLeft(2, '0');
            string day = (monthDayInt % 100).ToString().PadLeft(2, '0');

            // Convert hourMinute (2150 means 21:50)
            int hourMinInt = int.Parse(hourMinute);
            string hour = (hourMinInt / 100).ToString().PadLeft(2, '0');
            string minute = (hourMinInt % 100).ToString().PadLeft(2, '0');

            // Convert secMilliSec (10464 means 10.464 seconds)
            int secMsInt = int.Parse(secMilliSec);
            string seconds = (secMsInt / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (secMsInt % 1000).ToString().PadLeft(3, '0');

            string formattedTimestamp = $"{year}-{month}-{day} {hour}:{minute}:{seconds}.{milliseconds}";
            
            ////Log.Info($"Formatted timestamp: {formattedTimestamp}");
            return formattedTimestamp;
        }
        catch (Exception ex)
        {
            Log.Error($"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
    }
}
