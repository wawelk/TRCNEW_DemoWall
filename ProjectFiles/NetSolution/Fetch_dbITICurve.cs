#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.Core;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using FTOptix.WebUI;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class Fetch_dbITICurve : BaseNetLogic
{
    private string deviceName;
    private LongRunningTask fetchTask;
    private Label logLabel;
    private IUAVariable button_Animation;

    public override void Start()
    {
        
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Fetch_dbITICurve", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Fetch_dbITICurve", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Fetch_dbITICurve", "Label 'log' not found");
            return;
        }

        button_Animation = GetVariableValue("Button_Animation");

        //ProcessITICurveData(deviceName);
        FetchLastWeek();
    }

    public override void Stop()
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }
        button_Animation.Value = 0;
    }

    private void ProcessITICurveData(string deviceName, DateTime? startDate = null, DateTime? endDate = null)
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }

        // Check device status
        IUAVariable device_cat = Owner.GetAlias("Tag").GetVariable("DeviceStatus/CatalogNumber");
        string cat = device_cat.Value;

        if (cat == "1426-M5")
        {
            Log.Warning("Fetch_dbPowerQuality", $"Device status is {cat}, fetch aborted.");
            return;
        }

        fetchTask = new LongRunningTask(task => FetchAndUpdateITICurve(task, deviceName, startDate, endDate), LogicObject);
        fetchTask.Start();
    }

    private void FetchAndUpdateITICurve(LongRunningTask task, string deviceName, DateTime? startDate, DateTime? endDate)
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            webBrowser.Opacity = 0;

            logLabel.Text = "Fetching ITI curve data...";
            //Log.Info("Fetch_dbITICurve", "Starting ITI curve data fetch process");

            var project = FTOptix.HMIProject.Project.Current;
            var pqStore = project.GetObject("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_00_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            string pqTableName = $"PowerQualityLog_{deviceName}";

            if (task.IsCancellationRequested) return;

            if (!TableExists(pqStore, pqTableName))
            {
                Log.Error($"Table '{pqTableName}' does not exist in the database.");
                logLabel.Text = $"Error: No data table found for device {deviceName}";
                UpdateITICurveWithEmptyData();
                return;
            }

            var powerQualityDataList = FetchFilteredDataFromDatabase(pqStore, pqTableName, startDate, endDate);
            if (powerQualityDataList.Count == 0)
            {
                //Log.Info("No data found for ITI curve calculation.");
                logLabel.Text = $"No records found for {deviceName} between {startDate?.ToString("yyyy-MM-dd") ?? "All Dates"} and {endDate?.ToString("yyyy-MM-dd") ?? "All Dates"}";
                UpdateITICurveWithEmptyData();
                webBrowser.Opacity = 100;
                return;
            }

            if (task.IsCancellationRequested) return;

            //Log.Info($"{powerQualityDataList.Count} records fetched for ITI curve processing.");
            logLabel.Text = $"Found {powerQualityDataList.Count} records, calculating ITI curve data...";

            var itiCurveData = ProcessPowerQualityLogs(powerQualityDataList);
            var processedDataAsLists = itiCurveData.ConvertAll(item => new List<double> { item.Cycles, item.Percentage });
            logLabel.Text = $"Processed {processedDataAsLists.Count} ITI curve points between {startDate?.ToString("yyyy-MM-dd") ?? "All Dates"} and {endDate?.ToString("yyyy-MM-dd") ?? "All Dates"}";

            if (task.IsCancellationRequested) return;

            string jsonData = JsonConvert.SerializeObject(processedDataAsLists, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml
            });

            logLabel.Text = "Generating ITI curve dashboard...";
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_ITIDashboard.html");
            string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData);

            if (task.IsCancellationRequested) return;

            if (deviceHtmlPath != null)
            {
                if (webBrowser != null)
                {
                    string relativePath = $"res/Template_ITIDashboard_{deviceName}.html";
                    var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                    webBrowser.URL = templatePath;
                    //Log.Info("Fetch_dbITICurve", $"WebBrowser URL set to: {templatePath.Uri}");
                    webBrowser.Opacity = 100;
                    logLabel.Text = "Done";
                    webBrowser.Refresh();
                }
                else
                {
                    Log.Warning("Fetch_dbITICurve", "WebBrowser control not found");
                    logLabel.Text = "Error: WebBrowser not found";
                }
            }
            else
            {
                Log.Error("Fetch_dbITICurve", "Failed to update HTML template; deviceHtmlPath is null");
                logLabel.Text = "Error: Failed to generate ITI curve dashboard";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Fetch_dbITICurve", $"Error in FetchAndUpdateITICurve: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void UpdateITICurveWithEmptyData()
    {
        var emptyData = new List<List<double>>();
        string jsonData = JsonConvert.SerializeObject(emptyData);

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_ITIDashboard.html");

        string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData);

        if (deviceHtmlPath != null)
        {
            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser != null)
            {
                string relativePath = $"res/Template_ITIDashboard_{deviceName}.html";
                var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                webBrowser.URL = templatePath;
                //Log.Info("Fetch_dbITICurve", $"WebBrowser URL set to: {templatePath.Uri} (empty data)");
                webBrowser.Opacity = 100;
                webBrowser.Refresh();
            }
            else
            {
                Log.Warning("Fetch_dbITICurve", "WebBrowser control not found");
            }
        }
        else
        {
            Log.Error("Fetch_dbITICurve", "Failed to update HTML template with empty data");
        }
    }

    private List<(double Cycles, double Percentage)> ProcessPowerQualityLogs(List<PowerQualityData3> logs)
    {
        var processedData = new List<(double Cycles, double Percentage)>();

        foreach (var log in logs)
        {
            try
            {
                // Convert Event_Duration_mS to seconds
                double seconds = log.Event_Duration_mS / 1000.0;

                // Calculate Cycles (assuming nominal frequency of 60 Hz)
                double cycles = seconds * 60;

                // Parse Min_or_Max (now a string) to float, removing any '%' signs
                string minOrMaxStr = log.Min_or_Max?.Replace("%", "").Trim() ?? "0";
                if (!float.TryParse(minOrMaxStr, out float minOrMax))
                {
                    Log.Warning("Fetch_dbITICurve", $"Failed to parse Min_or_Max '{log.Min_or_Max}' for Record_Identifier {log.Record_Identifier}, defaulting to 0");
                    minOrMax = 0;
                }

                // Calculate Percentage Deviation
                double nominalVoltage = 480; // Nominal line-to-line voltage
                double absoluteDifference = Math.Abs(minOrMax - nominalVoltage);
                double percentage = (absoluteDifference / nominalVoltage) * 100;

                processedData.Add((cycles, percentage));
            }
            catch (Exception ex)
            {
                Log.Error("Fetch_dbITICurve", $"Error processing log Record_Identifier {log.Record_Identifier}: {ex.Message}");
            }
        }

        return processedData;
    }

    private List<PowerQualityData3> FetchFilteredDataFromDatabase(Store store, string tableName, DateTime? startDate, DateTime? endDate)
    {
        var powerQualityDataList = new List<PowerQualityData3>();

        try
        {
            string query = "SELECT * FROM " + tableName;
            if (startDate.HasValue && endDate.HasValue)
            {
                query += $" WHERE Local_Timestamp BETWEEN '{startDate.Value.ToString("yyyy-MM-dd HH:mm:ss")}' AND '{endDate.Value.ToString("yyyy-MM-dd HH:mm:ss")}'";
            }
            query += " ORDER BY Local_Timestamp DESC";

            object[,] resultSet;
            string[] header;
            store.Query(query, out header, out resultSet);

            if (resultSet == null || resultSet.GetLength(0) == 0)
            {
                //Log.Info("No records found in the database.");
                return powerQualityDataList;
            }

            string[] expectedColumns = { "Record_Identifier", "Event_Type", "Event_Code", "Sub_Event_Code", "Sub_Event", 
                                        "Local_Timestamp", "Event_Duration_mS", "Trip_Point", "Min_or_Max", "Association_Timestamp" };
            foreach (var column in expectedColumns)
            {
                if (Array.IndexOf(header, column) == -1)
                {
                    Log.Error($"Column '{column}' not found in query result.");
                    return powerQualityDataList;
                }
            }

            for (int i = 0; i < resultSet.GetLength(0); i++)
            {
                var record = new PowerQualityData3
                {
                    Record_Identifier = resultSet[i, Array.IndexOf(header, "Record_Identifier")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Record_Identifier")]) : 0,
                    Event_Type = resultSet[i, Array.IndexOf(header, "Event_Type")]?.ToString() ?? "",
                    Event_Code = resultSet[i, Array.IndexOf(header, "Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Event_Code")]) : 0,
                    Sub_Event_Code = resultSet[i, Array.IndexOf(header, "Sub_Event_Code")] != null ? Convert.ToInt32(resultSet[i, Array.IndexOf(header, "Sub_Event_Code")]) : 0,
                    Sub_Event = resultSet[i, Array.IndexOf(header, "Sub_Event")]?.ToString() ?? "",
                    Local_Timestamp = resultSet[i, Array.IndexOf(header, "Local_Timestamp")]?.ToString() ?? "",
                    Event_Duration_mS = resultSet[i, Array.IndexOf(header, "Event_Duration_mS")] != null ? Convert.ToSingle(resultSet[i, Array.IndexOf(header, "Event_Duration_mS")]) : 0f,
                    Trip_Point = resultSet[i, Array.IndexOf(header, "Trip_Point")]?.ToString() ?? "",
                    Min_or_Max = resultSet[i, Array.IndexOf(header, "Min_or_Max")]?.ToString() ?? "",
                    Association_Timestamp = resultSet[i, Array.IndexOf(header, "Association_Timestamp")]?.ToString() ?? ""
                };
                powerQualityDataList.Add(record);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error fetching filtered data from database: {ex.Message}");
        }

        return powerQualityDataList;
    }

    private bool TableExists(Store store, string tableName)
    {
        object[,] resultSet;
        string[] header;

        try
        {
            store.Query($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", out header, out resultSet);

            if (resultSet != null && resultSet.GetLength(0) > 0)
            {
                int tableExists = Convert.ToInt32(resultSet[0, 0]);
                return tableExists > 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error checking if table '{tableName}' exists: {ex.Message}");
        }

        return false;
    }

    private string UpdateHtmlTemplate(string templatePath, string jsonData)
    {
        string resDirectory = Path.GetDirectoryName(templatePath);
        string templateName = Path.GetFileNameWithoutExtension(templatePath);
        string outputFileName = $"{templateName}_{deviceName}.html";
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error("Fetch_dbITICurve", $"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            //Log.Info("Fetch_dbITICurve", $"Read {htmlContent.Length} chars from {templatePath}");

            string pattern = @"var\s+scatterData\s*=\s*\[[^\]]*\];";
            string replacement = $"var scatterData = {jsonData};";
            string modifiedContent = System.Text.RegularExpressions.Regex.Replace(htmlContent, pattern, replacement);

            if (htmlContent.Equals(modifiedContent))
            {
                Log.Warning("Fetch_dbITICurve", "No changes made to HTML content. Check if pattern exists in template.");
            }

            File.WriteAllText(deviceHtmlPath, modifiedContent);
            //Log.Info("Fetch_dbITICurve", $"{outputFileName} updated successfully at: {deviceHtmlPath}");
            return deviceHtmlPath;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbITICurve", $"Failed to update HTML template: {ex.Message}");
            return null;
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

    [ExportMethod]
    public void FetchLast24Hours()
    {
        DateTime startDate = DateTime.UtcNow.AddHours(-24);
        DateTime endDate = DateTime.UtcNow;
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info("Fetched ITI curve data for the last 24 hours.");
        button_Animation.Value = 1;
    }

    [ExportMethod]
    public void FetchLastWeek()
    {
        DateTime startDate = DateTime.UtcNow.AddDays(-7);
        DateTime endDate = DateTime.UtcNow;
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info("Fetched ITI curve data for the last week.");
        button_Animation.Value = 2;
    }

    [ExportMethod]
    public void FetchLastMonth()
    {
        DateTime startDate = DateTime.UtcNow.AddMonths(-1);
        DateTime endDate = DateTime.UtcNow;
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info("Fetched ITI curve data for the last month.");
        button_Animation.Value = 3;
    }

    [ExportMethod]
    public void FetchLast6Months()
    {
        DateTime startDate = DateTime.UtcNow.AddMonths(-6);
        DateTime endDate = DateTime.UtcNow;
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info("Fetched ITI curve data for the last 6 months.");
        button_Animation.Value = 4;
    }

    [ExportMethod]
    public void FetchLastYear()
    {
        DateTime startDate = DateTime.UtcNow.AddYears(-1);
        DateTime endDate = DateTime.UtcNow;
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info("Fetched ITI curve data for the last year.");
        button_Animation.Value = 5;
    }

    [ExportMethod]
    public void FetchCustomRange(DateTime startDate, DateTime endDate)
    {
        if (startDate > endDate)
        {
            Log.Error("FetchCustomRange", "Start date must be earlier than end date.");
            logLabel.Text = "Error: Start date must be earlier than end date";
            return;
        }
        ProcessITICurveData(deviceName, startDate, endDate);
        //Log.Info($"Fetched ITI curve data for custom range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        button_Animation.Value = 6;
    }

    [ExportMethod]
    public void testtt()
    {
        
    }
}

public class PowerQualityData3
{
    public int Record_Identifier { get; set; }
    public string Event_Type { get; set; }
    public int Event_Code { get; set; }
    public int Sub_Event_Code { get; set; }
    public string Sub_Event { get; set; }
    public string Local_Timestamp { get; set; }
    public float Event_Duration_mS { get; set; }
    public string Trip_Point { get; set; }
    public string Min_or_Max { get; set; }
    public string Association_Timestamp { get; set; }
}
