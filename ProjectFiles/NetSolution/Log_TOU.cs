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
using System.Threading.Tasks;
using System.Xml;
using System.Net.Http;
using System.Text;
using FTOptix.EventLogger;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using FTOptix.WebUI;
using DeviceLogin;
#endregion

public class Log_TOU : BaseNetLogic
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
            Log.Error("Log_TOU", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_TOU", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_TOU", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_TOU", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_TOU", "Label 'log' not found");
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
            Log.Error("Log_TOU", "IP address not set, cannot start download");
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
        string remoteFilePath = "/LoggingResults/Time_of_Use_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, deviceName);
        string localFilePath = Path.Combine(localFolder, "Time_of_Use_Log.csv");

        try
        {
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_TOU-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            Directory.CreateDirectory(localFolder);

            logLabel.Text = "Downloading...";
            //Log.Info("Log_TOU", $"Starting HTTP download from {url}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                byte[] fileBytes = client.GetByteArrayAsync(url).Result;

                // Check for cancellation before proceeding
                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_TOU", "Download canceled");
                    logLabel.Text = "Canceled";
                    return;
                }

                File.WriteAllBytes(localFilePath, fileBytes);
                //Log.Info("Log_TOU", $"File downloaded to {localFilePath}");

                if (task.IsCancellationRequested)
                {
                    //Log.Info("Log_TOU", "Download canceled after file write");
                    logLabel.Text = "Canceled";
                    return;
                }

                if (File.Exists(localFilePath))
                {
                    logLabel.Text = "Processing...";
                    TOULog();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_TOU", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_TOU", $"Failed to download log file: {ex.Message}");
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



    public void TOULog()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, deviceName, "Time_of_Use_Log.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("TOU Log file not found at: " + csvFilePath);
                return;
            }

            //Log.Info("Processing TOU Log from: " + csvFilePath);

            var TOUDataList = ProcessCsvFile(csvFilePath);
            UpdateDashboard(baseDirectory, TOUDataList);
        }
        catch (Exception ex)
        {
            Log.Error($"Error in Time of use Log: {ex.Message}");
        }
    }

    private List<TOUData> ProcessCsvFile(string csvFilePath)
    {
        var lines = File.ReadAllLines(csvFilePath);
        return lines.Skip(1)
            .Select(line =>
            {
                var row = line.Split(',');
                DateOnly StartDate = DateOnly.ParseExact(row[1],"yyMMdd", null); // Data from CSV
                DateOnly EndDate = DateOnly.ParseExact(row[2],"yyMMdd", null); // Data from CSV
                
                var Off_Peak_GWhNet = float.Parse(row[3]); // Data from CSV
                var Off_Peak_kWhNet = float.Parse(row[4]); // Data from CSV
                var Off_Peak_kWDemand = float.Parse(row[5]); // Data from CSV

                var Mid_Peak_GWhNet = float.Parse(row[6]); // Data from CSV
                var Mid_Peak_kWhNet = float.Parse(row[7]); // Data from CSV
                var Mid_Peak_kWDemand = float.Parse(row[8]); // Data from CSV
                var On_Peak_GWhNet = float.Parse(row[9]); // Data from CSV
                var On_Peak_kWhNet = float.Parse(row[10]); // Data from CSV
                var On_Peak_kWDemand = float.Parse(row[11]); // Data from CSV
                var Off_Peak_GVARhNet = float.Parse(row[12]); // Data from CSV
                var Off_Peak_kVARhNet = float.Parse(row[13]); // Data from CSV
                var Off_Peak_kVARDemand = float.Parse(row[14]); // Data from CSV
                var Mid_Peak_GVARhNet = float.Parse(row[15]); // Data from CSV
                var Mid_Peak_kVARhNet = float.Parse(row[16]); // Data from CSV
                var Mid_Peak_kVARDemand = float.Parse(row[17]); // Data from CSV
                var On_Peak_GVARhNet = float.Parse(row[18]); // Data from CSV
                var On_Peak_kVARhNet = float.Parse(row[19]); // Data from CSV
                var On_Peak_kVARDemand = float.Parse(row[20]); // Data from CSV
                var Off_Peak_GVAhNet = float.Parse(row[21]); // Data from CSV
                var Off_Peak_kVAhNet = float.Parse(row[22]); // Data from CSV
                var Off_Peak_kVADemand = float.Parse(row[23]); // Data from CSV
                var Mid_Peak_GVAhNet = float.Parse(row[24]); // Data from CSV
                var Mid_Peak_kVAhNet = float.Parse(row[25]); // Data from CSV
                var Mid_Peak_kVADemand = float.Parse(row[26]); // Data from CSV
                var On_Peak_GVAhNet = float.Parse(row[27]); // Data from CSV
                var On_Peak_kVAhNet = float.Parse(row[28]); // Data from CSV
                var On_Peak_kVADemand = float.Parse(row[29]); // Data from CSV
                

                
            

                return new TOUData
                {
                    Record_Number = int.Parse(row[0]),
                    Start_Date = StartDate,
                    End_Date = EndDate,
                    Off_Peak_GWh_Net = Off_Peak_GWhNet,
                    Off_Peak_kWh_Net = Off_Peak_kWhNet,
                    Off_Peak_kW_Demand = Off_Peak_kWDemand,
                    
                    Mid_Peak_GWh_Net = Mid_Peak_GWhNet,
                    Mid_Peak_kWh_Net = Mid_Peak_kWhNet,
                    Mid_Peak_kW_Demand = Mid_Peak_kWDemand,
                    On_Peak_GWh_Net = On_Peak_GWhNet,
                    On_Peak_kWh_Net = On_Peak_kWhNet,
                    On_Peak_kW_Demand = On_Peak_kWDemand,
                    Off_Peak_GVARh_Net = Off_Peak_GVARhNet,
                    Off_Peak_kVARh_Net = Off_Peak_kVARhNet,
                    Off_Peak_kVAR_Demand = Off_Peak_kVARDemand,
                    Mid_Peak_GVARh_Net = Mid_Peak_GVARhNet,
                    Mid_Peak_kVARh_Net = Mid_Peak_kVARhNet,
                    Mid_Peak_kVAR_Demand = Mid_Peak_kVARDemand,
                    On_Peak_GVARh_Net = On_Peak_GVARhNet,
                    On_Peak_kVARh_Net = On_Peak_kVARhNet,
                    On_Peak_kVAR_Demand = On_Peak_kVARDemand,
                    Off_Peak_GVAh_Net = Off_Peak_GVAhNet,
                    Off_Peak_kVAh_Net = Off_Peak_kVAhNet,
                    Off_Peak_kVA_Demand = Off_Peak_kVADemand,
                    Mid_Peak_GVAh_Net = Mid_Peak_GVAhNet,
                    Mid_Peak_kVAh_Net = Mid_Peak_kVAhNet,
                    Mid_Peak_kVA_Demand = Mid_Peak_kVADemand,
                    On_Peak_GVAh_Net = On_Peak_GVAhNet,
                    On_Peak_kVAh_Net = On_Peak_kVAhNet,
                    On_Peak_kVA_Demand = On_Peak_kVADemand

                    
            
                };
            })
            .ToList();
    }

    
    private void UpdateDashboard(string baseDirectory, List<TOUData> TOUDataList)
    {
        // Prepare raw JSON data
        var AllData = TOUDataList.Select(d => new
        {
            Record_Number = d.Record_Number,
            Start_Date = d.Start_Date,
            End_Date = d.End_Date,
            Off_Peak_GWh_Net = d.Off_Peak_GWh_Net,
            Off_Peak_kWh_Net = d.Off_Peak_kWh_Net,
            Off_Peak_kW_Demand = d.Off_Peak_kW_Demand,
            Mid_Peak_GWh_Net = d.Mid_Peak_GWh_Net,
            Mid_Peak_kWh_Net = d.Mid_Peak_kWh_Net,
            Mid_Peak_kW_Demand = d.Mid_Peak_kW_Demand,
            On_Peak_GWh_Net = d.On_Peak_GWh_Net,
            On_Peak_kWh_Net = d.On_Peak_kWh_Net,
            On_Peak_kW_Demand = d.On_Peak_kW_Demand,
            Off_Peak_GVARh_Net = d.Off_Peak_GVARh_Net,
            Off_Peak_kVARh_Net = d.Off_Peak_kVARh_Net,
            Off_Peak_kVAR_Demand = d.Off_Peak_kVAR_Demand,
            Mid_Peak_GVARh_Net = d.Mid_Peak_GVARh_Net,
            Mid_Peak_kVARh_Net = d.Mid_Peak_kVARh_Net,
            Mid_Peak_kVAR_Demand = d.Mid_Peak_kVAR_Demand,
            On_Peak_GVARh_Net = d.On_Peak_GVARh_Net,
            On_Peak_kVARh_Net = d.On_Peak_kVARh_Net,
            On_Peak_kVAR_Demand = d.On_Peak_kVAR_Demand,
            Off_Peak_GVAh_Net = d.Off_Peak_GVAh_Net,
            Off_Peak_kVAh_Net = d.Off_Peak_kVAh_Net,
            Off_Peak_kVA_Demand = d.Off_Peak_kVA_Demand,
            Mid_Peak_GVAh_Net = d.Mid_Peak_GVAh_Net,
            Mid_Peak_kVAh_Net = d.Mid_Peak_kVAh_Net,
            Mid_Peak_kVA_Demand = d.Mid_Peak_kVA_Demand,
            On_Peak_GVAh_Net = d.On_Peak_GVAh_Net,
            On_Peak_kVAh_Net = d.On_Peak_kVAh_Net,
            On_Peak_kVA_Demand = d.On_Peak_kVA_Demand
        }).ToList();
        var rawData = AllData.OrderBy(d => d.Start_Date).ToList();
        // Serialize to JSON
        string jsonData = JsonConvert.SerializeObject(rawData, new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.EscapeHtml
        });

        // Update HTML template with raw JSON data
        string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_TOUDashboard.html");
        string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData); // Returns the path to the updated file

        // Refresh the WebBrowser control with the new file
        var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        if (webBrowser != null)
        {
            // string webBrowserUrl = $"file:///{deviceHtmlPath.Replace('\\', '/')}";
            // webBrowser.URL = webBrowserUrl;
            // webBrowser.Refresh();
            string relativePath = $"res/Template_TOUDashboard_{deviceName}.html";
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
        string templateName = Path.GetFileNameWithoutExtension(templatePath); // e.g., "TOUDashboard"
        string outputFileName = $"{templateName}_{deviceName}.html"; // e.g., "TOUDashboard_Device1.html"
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error($"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            string pattern = @"var\s+rawData\s*=\s*\[[^\]]*\];";
            string replacement = $"var rawData = {jsonData};";

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
}
public class TOUData
{
    public int Record_Number { get; set; }
    public DateOnly Start_Date { get; set; }
    public DateOnly End_Date { get; set; }
    public float Off_Peak_GWh_Net { get; set; }
    public float Off_Peak_kWh_Net { get; set; }
    public float Off_Peak_kW_Demand { get; set; }
    public float Mid_Peak_GWh_Net { get; set; }
    public float Mid_Peak_kWh_Net { get; set; }
    public float Mid_Peak_kW_Demand { get; set; }
    public float On_Peak_GWh_Net { get; set; }
    public float On_Peak_kWh_Net { get; set; }
    public float On_Peak_kW_Demand { get; set; }
    public float Off_Peak_GVARh_Net { get; set; }
    public float Off_Peak_kVARh_Net { get; set; }
    public float Off_Peak_kVAR_Demand { get; set; }
    public float Mid_Peak_GVARh_Net { get; set; }
    public float Mid_Peak_kVARh_Net { get; set; }
    public float Mid_Peak_kVAR_Demand { get; set; }
    public float On_Peak_GVARh_Net { get; set; }
    public float On_Peak_kVARh_Net { get; set; }
    public float On_Peak_kVAR_Demand { get; set; }
    public float Off_Peak_GVAh_Net { get; set; }
    public float Off_Peak_kVAh_Net { get; set; }
    public float Off_Peak_kVA_Demand { get; set; }
    public float Mid_Peak_GVAh_Net { get; set; }
    public float Mid_Peak_kVAh_Net { get; set; }
    public float Mid_Peak_kVA_Demand { get; set; }
    public float On_Peak_GVAh_Net { get; set; }
    public float On_Peak_kVAh_Net { get; set; }
    public float On_Peak_kVA_Demand { get; set; }
  
}
