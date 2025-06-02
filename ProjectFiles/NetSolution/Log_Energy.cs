using System;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using System.IO;
using System.Linq;
using FTOptix.WebUI;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using OpcUa = UAManagedCore.OpcUa;
using System.Collections.Generic;
using Newtonsoft.Json;
using FTOptix.OPCUAServer;
using DeviceLogin;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.SerialPort;

public class Log_Energy : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private LongRunningTask fetchTask;
    private Label logLabel;
    private DataGrid dataGrid;
    private IUANode energyLogFilesNode;

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("EnergyLog_List", "Tag alias not found");
            return;
        }

        var ipVariable = tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("EnergyLog_List", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("EnergyLog_List", "IP address is null or empty");
            return;
        }

        deviceName = tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("EnergyLog_List", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("EnergyLog_List", "Label 'log' not found");
            return;
        }

        dataGrid = Owner.Get<DataGrid>("DataGrid1");
        if (dataGrid == null)
        {
            Log.Error("EnergyLog_List", "DataGrid 'DataGrid1' not found");
            return;
        }

        energyLogFilesNode = tag.GetObject("EnergyLogFiles");
        if (energyLogFilesNode == null)
        {
            energyLogFilesNode = InformationModel.MakeObject("EnergyLogFiles");
            tag.Add(energyLogFilesNode);
        }

        dataGrid.Model = energyLogFilesNode.NodeId;

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
            Log.Error("EnergyLog_List", "RowClicked: Filename variable not found in row");
            logLabel.Text = "Error: Filename not found";
            return;
        }

        string fileName = fileNameVar.Value;
        if (string.IsNullOrEmpty(fileName))
        {
            Log.Error("EnergyLog_List", "RowClicked: Filename is empty");
            logLabel.Text = "Error: Filename is empty";
            return;
        }

        FetchAndDisplayCsv(fileName);
    }

    private void StartFetch()
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("EnergyLog_List", "IP address not set, cannot start fetch");
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
        string baseUrl = $"http://{ipAddress}/logresults/Energy_Log.shtm";

        try
        {
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Error: Login to device failed";
                Log.Error($"EnergyLog_List-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            logLabel.Text = "Fetching Energy Log list...";

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
                    @"<tr[^>]*><td><a[^>]*Energylog_\d{8}_\d{6}_\d{2}\.csv[^>]*>[^<]*</a></td><td>(\d+)</td><td>(\d{2}/\d{2}/\d{4})</td><td>(\d{2}:\d{2}:\d{2})</td></tr>",
                    RegexOptions.IgnoreCase);

                if (matches.Count == 0)
                {
                    Log.Error("EnergyLog_List", "No Energy Log CSV files found in Energy_Log.shtm");
                    logLabel.Text = "Error: No files found";
                    return;
                }

                foreach (var child in energyLogFilesNode.Children.OfType<IUAObject>().ToList())
                {
                    child.Delete();
                }

                var fileInfos = matches.Cast<Match>()
                    .Select(m => new
                    {
                        FileName = Regex.Match(m.Value, @"Energylog_\d{8}_\d{6}_\d{2}\.csv").Value,
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
                    fileObject.Add(InformationModel.MakeVariable("filesize", OpcUa.DataTypes.Int32));
                    fileObject.Add(InformationModel.MakeVariable("datetime", OpcUa.DataTypes.String));

                    fileObject.GetVariable("Filename").Value = fileInfo.FileName;
                    fileObject.GetVariable("filesize").Value = fileInfo.Size;
                    fileObject.GetVariable("datetime").Value = fileInfo.Date;

                    energyLogFilesNode.Add(fileObject);
                }

                logLabel.Text = "Please Select Datalog from the list to view it";
            }
        }
        catch (Exception ex)
        {
            Log.Error("EnergyLog_List", $"Failed to fetch Energy Log list: {ex.Message}");
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

                string[] headers = rows[0].Split(',').Select(h => h.Trim()).ToArray();
                List<string[]> data = rows.Skip(1).Select(row => row.Split(',').Select(cell => cell.Trim()).ToArray()).ToList();

                var jsonData = new
                {
                    deviceName = deviceName,
                    headers = headers,
                    rows = data,
                    fileName = fileName
                };

                string templatePath = Path.Combine(new ResourceUri("%PROJECTDIR%\\").Uri, "res", "Template_EnergyLog.html");
                if (!File.Exists(templatePath))
                {
                    throw new Exception("Template_EnergyLog.html not found in res directory");
                }

                string templateContent = File.ReadAllText(templatePath);

                string jsonDataString = JsonConvert.SerializeObject(jsonData);
                string modifiedTemplateContent = templateContent.Replace(
                    "<!-- DATA_PLACEHOLDER -->",
                    $"<script>const energyData = {jsonDataString};</script>");

                string outputHtmlPath = Path.Combine(new ResourceUri("%PROJECTDIR%\\").Uri, "res", $"{deviceName}_EnergyLog.html");
                File.WriteAllText(outputHtmlPath, modifiedTemplateContent);

                var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
                if (webBrowser != null)
                {
                    var htmlUri = ResourceUri.FromProjectRelativePath($"res/{deviceName}_EnergyLog.html");
                    webBrowser.URL = htmlUri;
                    logLabel.Text = $"Energy Log: {fileName}";
                    webBrowser.Refresh();
                }
                else
                {
                    Log.Error("EnergyLog_List", "WebBrowser control not found");
                    logLabel.Text = "Error: WebBrowser not found";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("EnergyLog_List", $"Failed to fetch or display CSV file {fileName}: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }
}
