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
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Threading.Tasks;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
#endregion

public class Fetch_dbPowerQuality : BaseNetLogic
{
    private string deviceName;
    private LongRunningTask fetchTask;
    private Label logLabel;
    private IUAVariable button_Animation;

    private IUAVariable New_event_triggered; 

    public override void Start()
    {
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Fetch_dbPowerQuality", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Fetch_dbPowerQuality", "Device name is null or empty");
            return;
        }

        button_Animation = GetVariableValue("Button_Animation");
        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Fetch_dbPowerQuality", "Label 'log' not found");
            return;
        }

        New_event_triggered = Tag.GetVariable("db_PQLogger/NewEventTrigger");

        // Subscribe to enable/disable changes
        New_event_triggered.VariableChange += tab_refresh_VariableChange;

        FetchLastWeek();
    }

    public override void Stop()
    {
        button_Animation.Value = 0;
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }
        New_event_triggered.VariableChange -= tab_refresh_VariableChange;
    }

    private void tab_refresh_VariableChange(object sender, VariableChangeEventArgs e)
    {
        FetchLastWeek();
    }

    private void ProcessPowerQualityLog_filter(string deviceName, DateTime? startDate = null, DateTime? endDate = null)
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }

        IUAVariable device_cat = Owner.GetAlias("Tag").GetVariable("DeviceStatus/CatalogNumber");
        string cat = device_cat.Value;

        if (cat == "1426-M5")
        {
            Log.Warning("Fetch_dbPowerQuality", $"Device status is {cat}, fetch aborted.");
            logLabel.Text = $"Power Quality Logs not supported for: {deviceName} catalog";
            return;
        }

        fetchTask = new LongRunningTask(task => FetchAndUpdateDashboard(task, deviceName, startDate, endDate), LogicObject);
        fetchTask.Start();
    }

    private void FetchAndUpdateDashboard(LongRunningTask task, string deviceName, DateTime? startDate, DateTime? endDate)
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            webBrowser.Opacity = 0;

            logLabel.Text = "Fetching data...";
            //Log.Info("Fetch_dbPowerQuality", "Starting data fetch process");

            var project = FTOptix.HMIProject.Project.Current;
            var pqStore = project.GetObject("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_DataStores").Get<Store>("raC_4_00_raC_Dvc_PM5000_PQEM_db_PowerQuality");
            string pqTableName = $"PowerQualityLog_{deviceName}";

            if (task.IsCancellationRequested) return;

            if (!TableExists(pqStore, pqTableName))
            {
                Log.Error($"Table '{pqTableName}' does not exist in the database.");
                logLabel.Text = $"Error: No data table found for device {deviceName}";
                UpdateDashboardWithEmptyData();
                return;
            }

            var powerQualityDataList = FetchFilteredDataFromDatabase(pqStore, pqTableName, startDate, endDate);
            if (powerQualityDataList.Count == 0)
            {
                //Log.Info("No data found in the database for the selected period.");
                logLabel.Text = $"No records found for {deviceName} between {startDate?.ToString("yyyy-MM-dd") ?? "All Dates"} and {endDate?.ToString("yyyy-MM-dd") ?? "All Dates"}";
                UpdateDashboardWithEmptyData();
                webBrowser.Opacity = 100;
                return;
            }

            if (task.IsCancellationRequested) return;

            //Log.Info($"{powerQualityDataList.Count} power quality records fetched from the database.");
            logLabel.Text = $"Found {powerQualityDataList.Count} records between {startDate?.ToString("yyyy-MM-dd") ?? "All Dates"} and {endDate?.ToString("yyyy-MM-dd") ?? "All Dates"}";

            var dashboardData = new { events = powerQualityDataList };
            string jsonData = JsonConvert.SerializeObject(dashboardData, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml
            });

            logLabel.Text = "Generating dashboard...";
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_PowerQualityDashboard.html");
            //Log.Info("Fetch_dbPowerQuality", $"File size before UpdateHtmlTemplate: {new FileInfo(htmlTemplatePath).Length} bytes");
            string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData);

            if (task.IsCancellationRequested) return;

            if (deviceHtmlPath != null)
            {
                if (webBrowser != null)
                {
                    string relativePath = $"res/Template_PowerQualityDashboard_{deviceName}.html";
                    var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                    webBrowser.URL = templatePath;
                    //Log.Info("Fetch_dbPowerQuality", $"WebBrowser URL set to: {templatePath.Uri}");
                    webBrowser.Opacity = 100;
                    logLabel.Text = "Done";
                    webBrowser.Refresh();
                }
                else
                {
                    Log.Warning("Fetch_dbPowerQuality", "WebBrowser control not found");
                    logLabel.Text = "Error: WebBrowser not found";
                }
            }
            else
            {
                Log.Error("Fetch_dbPowerQuality", "Failed to update HTML template; deviceHtmlPath is null");
                logLabel.Text = "Error: Failed to generate dashboard";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Fetch_dbPowerQuality", $"Error in FetchAndUpdateDashboard: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void UpdateDashboardWithEmptyData()
    {
        var emptyData = new { events = new object[0] };
        string jsonData = JsonConvert.SerializeObject(emptyData);

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_PowerQualityDashboard.html");

        string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData);

        if (deviceHtmlPath != null)
        {
            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser != null)
            {
                string relativePath = $"res/Template_PowerQualityDashboard_{deviceName}.html";
                var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                webBrowser.URL = templatePath;
                //Log.Info("Fetch_dbPowerQuality", $"WebBrowser URL set to: {templatePath.Uri} (empty data)");
                webBrowser.Refresh();
            }
            else
            {
                Log.Warning("Fetch_dbPowerQuality", "WebBrowser control not found");
            }
        }
        else
        {
            Log.Error("Fetch_dbPowerQuality", "Failed to update HTML template with empty data");
        }
    }

    private List<PowerQualityData2> FetchFilteredDataFromDatabase(Store store, string tableName, DateTime? startDate, DateTime? endDate)
    {
        var powerQualityDataList = new List<PowerQualityData2>();

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
                var record = new PowerQualityData2
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
                Log.Error("Fetch_dbPowerQuality", $"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            //Log.Info("Fetch_dbPowerQuality", $"Read {htmlContent.Length} chars from {templatePath}");

            string pattern = @"var\s+rawData\s*=\s*\{[\s\S]*?\};";
            string replacement = $"var rawData = {jsonData};";
            string modifiedContent = System.Text.RegularExpressions.Regex.Replace(htmlContent, pattern, replacement);

            if (htmlContent.Equals(modifiedContent))
            {
                Log.Warning("Fetch_dbPowerQuality", "No changes made to HTML content. Check if pattern exists in template.");
            }

            File.WriteAllText(deviceHtmlPath, modifiedContent);
            //Log.Info("Fetch_dbPowerQuality", $"{outputFileName} updated successfully at: {deviceHtmlPath}");
            return deviceHtmlPath;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_dbPowerQuality", $"Failed to update HTML template: {ex.Message}");
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
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info("Fetched data for the last 24 hours.");
        button_Animation.Value = 1;
    }

    [ExportMethod]
    public void FetchLastWeek()
    {
        DateTime startDate = DateTime.UtcNow.AddDays(-7);
        DateTime endDate = DateTime.UtcNow;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info("Fetched data for the last week.");
        button_Animation.Value = 2;
    }

    [ExportMethod]
    public void FetchLastMonth()
    {
        DateTime startDate = DateTime.UtcNow.AddMonths(-1);
        DateTime endDate = DateTime.UtcNow;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info("Fetched data for the last month.");
        button_Animation.Value = 3;
    }

    [ExportMethod]
    public void FetchLast6Months()
    {
        DateTime startDate = DateTime.UtcNow.AddMonths(-6);
        DateTime endDate = DateTime.UtcNow;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info("Fetched data for the last 6 months.");
        button_Animation.Value = 4;
    }

    [ExportMethod]
    public void FetchLastYear()
    {
        DateTime startDate = DateTime.UtcNow.AddYears(-1);
        DateTime endDate = DateTime.UtcNow;
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info("Fetched data for the last year.");
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
        ProcessPowerQualityLog_filter(deviceName, startDate, endDate);
        //Log.Info($"Fetched data for custom range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        button_Animation.Value = 6;
    }
}

public class PowerQualityData2
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
