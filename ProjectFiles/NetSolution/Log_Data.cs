using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using FTOptix.WebUI;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Collections.Generic;
using DeviceLogin; 
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.SerialPort;

public class Log_Data : BaseNetLogic
{
    private string deviceName; 
    private string ipAddress; 
    private LongRunningTask fetchTask; 
    private Label logLabel; 
    private DataGrid dataGrid; 
    private IUANode dataLogFilesNode;

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("Log_Data", "Tag alias not found");
            return;
        }

        var ipVariable = tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_Data", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_Data", "IP address is null or empty");
            return;
        }

        deviceName = tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_Data", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("Status");
        if (logLabel == null)
        {
            Log.Error("Log_Data", "Label 'log' not found");
            return;
        }

        dataGrid = Owner.Get<DataGrid>("DataGrid1");
        if (dataGrid == null)
        {
            Log.Error("Log_Data", "DataGrid 'DataGrid1' not found");
            return;
        }

        dataLogFilesNode = tag.GetObject("DataLogFiles");
        if (dataLogFilesNode == null)
        {
            dataLogFilesNode = InformationModel.MakeObject("DataLogFiles");
            tag.Add(dataLogFilesNode);
        }

        dataGrid.Model = dataLogFilesNode.NodeId;

        logLabel.Text = "Please Select Datalog from the list to view it";
        StartFetch();
    }

    public override void Stop()
    {
        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }
    }

    [ExportMethod]
    public void FetchLogListAsync()
    {
        logLabel.Text = "Please Select Datalog from the list to view it";
        StartFetch();
    }

    [ExportMethod]
    public void OnRowClicked()
    {
        var row = InformationModel.Get(dataGrid.UISelectedItem);
        var fileNameVar = row.GetVariable("Filename");
        if (fileNameVar == null)
        {
            Log.Error("Log_Data", "RowClicked: Filename variable not found in row");
            logLabel.Text = "Error: Filename not found";
            return;
        }

        string fileName = fileNameVar.Value;
        if (string.IsNullOrEmpty(fileName))
        {
            Log.Error("Log_Data", "RowClicked: Filename is empty");
            logLabel.Text = "Error: Filename is empty";
            return;
        }

        FetchAndDisplayCsv(fileName);
    }

    private void StartFetch()
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_Data", "IP address not set, cannot start fetch");
            if (logLabel != null)
                logLabel.Text = "Error: IP not set";
            return;
        }

        if (fetchTask != null)
        {
            fetchTask.Dispose();
            fetchTask = null;
        }

        fetchTask = new LongRunningTask(task => FetchLogList(task), LogicObject);
        fetchTask.Start();
    }

    private void FetchLogList(LongRunningTask task)
    {
        string baseUrl = $"http://{ipAddress}/logresults/Data_Log.shtm";

        try
        {
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Error: Login to device failed";
                Log.Error($"Log_Data-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            logLabel.Text = "Fetching Data Log list...";

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                string htmlContent = client.GetStringAsync(baseUrl).Result;
                if (task.IsCancellationRequested)
                {
                    logLabel.Text = "Canceled";
                    return;
                }

                var matches = Regex.Matches(
                    htmlContent,
                    @"<tr[^>]*><td><a[^>]*Datalog_\d{8}_\d{6}_\d{2}\.csv[^>]*>[^<]*</a></td><td>(\d+)</td><td>(\d{2}/\d{2}/\d{4})</td><td>(\d{2}:\d{2}:\d{2})</td></tr>",
                    RegexOptions.IgnoreCase);

                if (matches.Count == 0)
                {
                    Log.Error("Log_Data", "No DataLog CSV files found in Data_Log.shtm");
                    logLabel.Text = "Error: No files found";
                    return;
                }

                foreach (var child in dataLogFilesNode.Children.OfType<IUAObject>().ToList())
                {
                    child.Delete();
                }

                var fileInfos = matches.Cast<Match>()
                    .Select(m => new
                    {
                        FileName = Regex.Match(m.Value, @"Datalog_\d{8}_\d{6}_\d{2}\.csv").Value,
                        Size = int.Parse(m.Groups[1].Value),
                        Date = DateTime.ParseExact(
                            m.Groups[2].Value + " " + m.Groups[3].Value,
                            "MM/dd/yyyy HH:mm:ss",
                            CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss")
                    })
                    .OrderByDescending(f => f.Date)
                    .ToList();

                for (int i = 0; i < fileInfos.Count; i++)
                {
                    var fileInfo = fileInfos[i];
                    var fileObject = InformationModel.MakeObject($"file{i + 1}");
                    fileObject.Add(InformationModel.MakeVariable("Filename", OpcUa.DataTypes.String));
                    fileObject.Add(InformationModel.MakeVariable("Filesize", OpcUa.DataTypes.Int32));
                    fileObject.Add(InformationModel.MakeVariable("Datetime", OpcUa.DataTypes.String));

                    fileObject.GetVariable("Filename").Value = fileInfo.FileName;
                    fileObject.GetVariable("Filesize").Value = fileInfo.Size;
                    fileObject.GetVariable("Datetime").Value = fileInfo.Date;

                    dataLogFilesNode.Add(fileObject);
                }

                logLabel.Text = "Please Select Datalog from the list to view it";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", $"Failed to fetch Data Log list: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void FetchAndDisplayCsv(string fileName)
    {
        try
        {
            logLabel.Text = "Downloading and Processing";

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                string csvContent = client.GetStringAsync($"http://{ipAddress}/LoggingResults/{fileName}").Result;

                string[] rows = csvContent.Trim().Split('\n');
                if (rows.Length == 0)
                {
                    throw new Exception("CSV file is empty");
                }

                // Save CSV to local folder for LoadCSVAndUpdateCharts
                string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
                string localFolder = Path.Combine(projectDir, deviceName);
                Directory.CreateDirectory(localFolder);
                string localFilePath = Path.Combine(localFolder, fileName);
                File.WriteAllText(localFilePath, csvContent);

                LoadCSVAndUpdateCharts(fileName);
                logLabel.Text = $"Data Log: {fileName}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", $"Failed to fetch or display CSV file {fileName}: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void LoadCSVAndUpdateCharts(string csvfilename)
    {
        try
        {
            ResourceUri filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, deviceName, csvfilename);

            if (!File.Exists(csvFilePath))
            {
                Log.Error("Log_Data", "CSV file not found at: " + csvFilePath);
                logLabel.Text = "Error: CSV file not found";
                return;
            }

            var lines = File.ReadAllLines(csvFilePath);
            var headers = lines[0].Split(',');
            var data = lines.Skip(1).Select(line => line.Split(',')).ToArray();

            var chartData = new
            {
                xAxis = data.Select(row => FormatTimestamp(row[1], row[2], row[3], row[4])).ToArray(),
                voltage = data.Select(row => float.Parse(row[5])).ToArray(),
                current = data.Select(row => float.Parse(row[7])).ToArray(),
                total_kW = data.Select(row => float.Parse(row[9])).ToArray(),
                total_kVAR = data.Select(row => float.Parse(row[10])).ToArray(),
                total_kVA = data.Select(row => float.Parse(row[11])).ToArray(),
                frequency = data.Select(row => float.Parse(row[8])).ToArray(),
                voltageUnbalance = data.Select(row => float.Parse(row[21])).ToArray(),
                currentUnbalance = data.Select(row => float.Parse(row[22])).ToArray()
            };

            string jsonData = JsonConvert.SerializeObject(chartData, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeHtml
            });

            string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_PowerDashboard.html");
            string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData);

            if (deviceHtmlPath != null)
            {
                string relativePath = $"res/Template_PowerDashboard_{deviceName}.html";
                var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
                var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
                if (webBrowser != null)
                {
                    webBrowser.URL = templatePath;
                    webBrowser.Refresh();
                }
                else
                {
                    Log.Error("Log_Data", "WebBrowser control not found in the UI");
                    logLabel.Text = "Error: WebBrowser control not found";
                }
            }
            else
            {
                logLabel.Text = "Error: Failed to update HTML template";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", "Error in LoadCSVAndUpdateCharts: " + ex.Message);
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private string UpdateHtmlTemplate(string templatePath, string jsonData)
    {
        string resDirectory = Path.Combine(Path.GetDirectoryName(templatePath));
        string templateName = Path.GetFileNameWithoutExtension(templatePath);
        string outputFileName = $"{templateName}_{deviceName}.html";
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error("Log_Data", $"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            string pattern = @"<script id=""jsonData"" type=""application/json""></script>";
            string replacement = $"<script id=\"jsonData\" type=\"application/json\">{jsonData}</script>";

            string updatedContent = Regex.Replace(htmlContent, pattern, replacement);

            if (htmlContent.Equals(updatedContent))
            {
                Log.Warning("Log_Data", "No changes made to HTML content. Check if pattern exists in template.");
            }

            File.WriteAllText(deviceHtmlPath, updatedContent);

            return deviceHtmlPath;
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", $"Failed to update HTML template: {ex.Message}");
            return null;
        }
    }

    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            int hourMinuteInt = int.Parse(hourMinute);
            string hour = (hourMinuteInt / 100).ToString().PadLeft(2, '0');
            string minute = (hourMinuteInt % 100).ToString().PadLeft(2, '0');
            string second = secMilliSec.PadLeft(2, '0');

            string month, day;
            if (monthDay.Length == 3)
            {
                month = monthDay.Substring(0, 1).PadLeft(2, '0');
                day = monthDay.Substring(1, 2);
            }
            else
            {
                month = monthDay.Substring(0, 2);
                day = monthDay.Substring(2, 2);
            }

            return $"{year}-{month}-{day} {hour}:{minute}:{second}";
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", $"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
    }
}
