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
using FTOptix.SerialPort;

public class Fetch_Waveforms : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private LongRunningTask fetchTask;
    private LongRunningTask displayWaveformTask;
    private Label statusLabel; // Renamed from logLabel to statusLabel for clarity
    private DataGrid dataGrid;
    private IUANode waveformFilesNode;
    private Button cancelButton; // Added to control the cancel button visibility

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("WaveformViewer_List", "Tag alias not found");
            return;
        }

        var ipVariable = tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("WaveformViewer_List", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("WaveformViewer_List", "IP address is null or empty");
            return;
        }

        deviceName = tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("WaveformViewer_List", "Device name is null or empty");
            return;
        }

        statusLabel = Owner.Get<Label>("Rectangle1/HorizontalLayout1/Status"); // Use Status label
        if (statusLabel == null)
        {
            Log.Error("WaveformViewer_List", "Label 'Status' not found");
            return;
        }

        dataGrid = Owner.Get<DataGrid>("DataGrid1");
        if (dataGrid == null)
        {
            Log.Error("WaveformViewer_List", "DataGrid 'DataGrid1' not found");
            return;
        }

        waveformFilesNode = tag.GetObject("WaveformFiles");
        if (waveformFilesNode == null)
        {
            waveformFilesNode = InformationModel.MakeObject("WaveformFiles");
            tag.Add(waveformFilesNode);
            //Log.Info("WaveformViewer_List", "Created WaveformFiles object under Tag");
        }

        dataGrid.Model = waveformFilesNode.NodeId;

        // Set initial status message and get cancel button
        cancelButton = Owner.Get<Button>("Rectangle1/HorizontalLayout1/cancel"); // Assuming the button is named "cancel"
        if (cancelButton == null)
        {
            Log.Error("WaveformViewer_List", "Cancel button not found");
            return;
        }
        cancelButton.Visible = false; // Hide initially

        StartFetch();
        statusLabel.Text = "Please select the waveform from the list to view it.";
    }

    public override void Stop()
    {
        try
        {
            if (fetchTask != null)
            {
                fetchTask.Cancel();
                fetchTask.Dispose();
                fetchTask = null;
                //Log.Info("WaveformViewer_List", "Fetch task canceled or disposed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel fetchTask: {ex.Message}");
        }

        try
        {
            if (displayWaveformTask != null)
            {
                displayWaveformTask.Cancel();
                displayWaveformTask.Dispose();
                displayWaveformTask = null;
                //Log.Info("WaveformViewer_List", "Display waveform task canceled or disposed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel displayWaveformTask: {ex.Message}");
        }
    }

    [ExportMethod]
    public void FetchWaveformListAsync()
    {
        statusLabel.Text = "Downloading & Processing"; // Update status
        cancelButton.Visible = true; // Show cancel button
        StartFetch();
    }

    [ExportMethod]
    public void OnRowClicked()
    {
        var row = InformationModel.Get(dataGrid.UISelectedItem);
        var fileNameVar = row.GetVariable("Filename");
        if (fileNameVar == null)
        {
            Log.Error("WaveformViewer_List", "RowClicked: Filename variable not found in row");
            statusLabel.Text = "Error: Filename not found";
            cancelButton.Visible = false;
            return;
        }

        string fileName = fileNameVar.Value;
        if (string.IsNullOrEmpty(fileName))
        {
            Log.Error("WaveformViewer_List", "RowClicked: Filename is empty");
            statusLabel.Text = "Error: Filename is empty";
            cancelButton.Visible = false;
            return;
        }

        // Show status before starting the fetch
        statusLabel.Text = "Downloading & Processing";
        cancelButton.Visible = true; // Show cancel button

        // Ensure any previous task is fully stopped
        try
        {
            if (displayWaveformTask != null)
            {
                displayWaveformTask.Cancel();
                displayWaveformTask.Dispose();
                displayWaveformTask = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel displayWaveformTask: {ex.Message}");
        }

        var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        webBrowser.Visible = false;

        //Log.Info("WaveformViewer_List", $"Starting new waveform fetch for {fileName}");
        displayWaveformTask = new LongRunningTask(task => FetchAndDisplayWaveform(fileName, task), LogicObject);
        displayWaveformTask.Start();
    }

    private void StartFetch()
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("WaveformViewer_List", "IP address not set, cannot start fetch");
            if (statusLabel != null)
                statusLabel.Text = "Failed: IP not set";
            cancelButton.Visible = false;
            return;
        }

        // Show status during initial fetch
        statusLabel.Text = "Fetching waveform list";
        cancelButton.Visible = true; // Show cancel button

        try
        {
            if (fetchTask != null)
            {
                fetchTask.Cancel();
                fetchTask.Dispose();
                fetchTask = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel fetchTask: {ex.Message}");
        }

        fetchTask = new LongRunningTask(task => FetchWaveformList(task), LogicObject);
        fetchTask.Start();
    }

    private void FetchWaveformList(LongRunningTask task)
    {
        string baseUrl = $"http://{ipAddress}/logresults/Waveform_Log.shtm";

        try
        {
            statusLabel.Text = "Logging in...";

            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                statusLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"WaveformViewer_List-{deviceName}", "Cannot fetch waveform list due to login failure.");
                cancelButton.Visible = false; // Hide cancel button
                return;
            }

            statusLabel.Text = "Fetching Waveform list...";
            //Log.Info("WaveformViewer_List", $"Fetching waveform list from {baseUrl}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                string htmlContent = client.GetStringAsync(baseUrl).Result;
                if (task.IsCancellationRequested)
                {
                    //Log.Info("WaveformViewer_List", "Operation canceled");
                    statusLabel.Text = "Canceled";
                    cancelButton.Visible = false; // Hide cancel button
                    return;
                }

                //Log.Info("WaveformViewer_List", $"Raw HTML Content:\n{htmlContent}");

                var matches = Regex.Matches(
                    htmlContent,
                    @"<tr[^>]*><td[^>]*><a[^>]*href=""[^""]*Waveform_\d{3}_\d{8}_\d{6}_\d{6}_\d{2}\.wfm[^""]*"">Waveform_\d{3}_(\d{8}_\d{6}_\d{6})_\d{2}\.wfm</a></td>\s*<td[^>]*>(\d+(\.\d+)?)</td>\s*<td[^>]*>(\d+)</td>\s*<td[^>]*>(\d{1,2}/\d{1,2}/\d{4})</td>\s*<td[^>]*>(\d{1,2}:\d{2}:\d{2})</td>",
                    RegexOptions.IgnoreCase);

                if (matches.Count == 0)
                {
                    Log.Error("WaveformViewer_List", "No waveform files found in Waveform_Log.shtm table");
                    statusLabel.Text = "Failed: No files found";
                    cancelButton.Visible = false; // Hide cancel button
                    return;
                }

                //Log.Info("WaveformViewer_List", $"Found {matches.Count} waveform entries in the table");

                foreach (var child in waveformFilesNode.Children.OfType<IUAObject>().ToList())
                {
                    child.Delete();
                }

                var fileInfos = matches.Cast<Match>()
                    .Select(m => new
                    {
                        FileName = m.Value.Contains("Waveform_") ? Regex.Match(m.Value, @"Waveform_\d{3}_\d{8}_\d{6}_\d{6}_\d{2}\.wfm").Value : "",
                        Size = float.Parse(m.Groups[2].Value),
                        Cycles = int.Parse(m.Groups[4].Value),
                        DateTime = ParseWaveformDateTimeFromTable(m.Groups[5].Value, m.Groups[6].Value)
                    })
                    .Where(f => !string.IsNullOrEmpty(f.FileName) && f.DateTime != null)
                    .OrderByDescending(f => f.DateTime)
                    .Select(f => new
                    {
                        FileName = f.FileName,
                        Size = (int)(f.Size * 1024),
                        Cycles = f.Cycles,
                        Date = f.DateTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
                    })
                    .ToList();

                for (int i = 0; i < fileInfos.Count; i++)
                {
                    var fileInfo = fileInfos[i];
                    var fileObject = InformationModel.MakeObject($"file{i + 1}");
                    fileObject.Add(InformationModel.MakeVariable("Filename", OpcUa.DataTypes.String));
                    fileObject.Add(InformationModel.MakeVariable("filesize", OpcUa.DataTypes.Int32));
                    fileObject.Add(InformationModel.MakeVariable("cycles", OpcUa.DataTypes.Int32));
                    fileObject.Add(InformationModel.MakeVariable("datetime", OpcUa.DataTypes.String));
                    fileObject.Add(InformationModel.MakeVariable("Event Type", OpcUa.DataTypes.String)); // New column

                    fileObject.GetVariable("Filename").Value = fileInfo.FileName;
                    fileObject.GetVariable("filesize").Value = fileInfo.Size;
                    fileObject.GetVariable("cycles").Value = fileInfo.Cycles;
                    fileObject.GetVariable("datetime").Value = fileInfo.Date;

                    waveformFilesNode.Add(fileObject);
                }

                // Associate power quality events after loading waveforms
                AssociatePowerQualityEvents();

                statusLabel.Text = "Done";
                //Log.Info("WaveformViewer_List", $"Loaded {fileInfos.Count} waveform files into WaveformFiles model");
                statusLabel.Text = "Please select the waveform from the list to view it.";
                cancelButton.Visible = false; // Hide cancel button
            }
        }
        catch (Exception ex)
        {
            Log.Error("WaveformViewer_List", $"Failed to fetch waveform list: {ex.Message}");
            statusLabel.Text = $"Failed: {ex.Message}";
            cancelButton.Visible = false; // Hide cancel button
        }
    }

    private DateTime? ParseWaveformDateTimeFromTable(string dateStr, string timeStr)
    {
        try
        {
            string formatted = $"{dateStr} {timeStr}";
            return DateTime.ParseExact(formatted, "M/d/yyyy H:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            Log.Warning("WaveformViewer_List", $"Failed to parse date and time: {dateStr} {timeStr}");
            return null;
        }
    }

    private DateTime? ParseWaveformDateTime(string dateTimeStr)
    {
        try
        {
            string year = dateTimeStr.Substring(0, 4);
            string month = dateTimeStr.Substring(4, 2);
            string day = dateTimeStr.Substring(6, 2);
            string hour = dateTimeStr.Substring(9, 2);
            string minute = dateTimeStr.Substring(11, 2);
            string second = dateTimeStr.Substring(13, 2);
            string millisecond = dateTimeStr.Substring(15, 3);

            string formatted = $"{year}-{month}-{day} {hour}:{minute}:{second}.{millisecond}";
            return DateTime.ParseExact(formatted, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        catch
        {
            Log.Warning("WaveformViewer_List", $"Failed to parse waveform datetime: {dateTimeStr}");
            return null;
        }
    }

    private void FetchAndDisplayWaveform(string fileName, LongRunningTask task)
    {
        try
        {
            statusLabel.Text = $"Downloading & Processing: {fileName}";
            //Log.Info("WaveformViewer_List", $"Fetching waveform file: {fileName} from http://{ipAddress}/Waveform/cgi-bin/ConvertToCVS/{fileName.Replace(".wfm", ".csv")}?filename={fileName}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(600);

                string csvUrl = $"http://{ipAddress}/Waveform/cgi-bin/ConvertToCVS/{fileName.Replace(".wfm", ".csv")}?filename={fileName}";
                string csvContent = client.GetStringAsync(csvUrl).Result;

                if (task.IsCancellationRequested)
                {
                    //Log.Info("WaveformViewer_List", "Operation canceled");
                    statusLabel.Text = "Canceled";
                    cancelButton.Visible = false; // Hide cancel button
                    return;
                }

                string[] rows = csvContent.Trim().Split('\n');
                if (rows.Length < 2)
                {
                    throw new Exception("Waveform CSV file is empty or invalid");
                }

                string[] headers = rows[2].Split(',').Select(h => h.Trim()).ToArray();
                List<string[]> data = rows.Skip(3)
                    .Select(row => row.Split(',').Select(cell => cell.Trim()).ToArray())
                    .Where(row => row.Length >= 9)
                    .ToList();

                var jsonData = new
                {
                    deviceName = deviceName,
                    headers = headers,
                    rows = data,
                    fileName = fileName
                };

                string templatePath = Path.Combine(new ResourceUri("%PROJECTDIR%\\").Uri, "res", "Template_WaveformViewer.html");
                if (!File.Exists(templatePath))
                {
                    throw new Exception("Template_WaveformViewer.html not found in res directory");
                }

                string templateContent = File.ReadAllText(templatePath);

                string jsonDataString = JsonConvert.SerializeObject(jsonData);
                string modifiedTemplateContent = templateContent.Replace(
                    "<!-- DATA_PLACEHOLDER -->",
                    $"<script>const waveformData = {jsonDataString};</script>")
                    .Replace("{{deviceName}}", deviceName);

                string outputHtmlPath = Path.Combine(new ResourceUri("%PROJECTDIR%\\").Uri, "res", $"{deviceName}_WaveformViewer.html");
                File.WriteAllText(outputHtmlPath, modifiedTemplateContent);
                //Log.Info("WaveformViewer_List", $"Saved modified template to {outputHtmlPath}");

                var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
                if (webBrowser != null)
                {
                    var htmlUri = ResourceUri.FromProjectRelativePath($"res/{deviceName}_WaveformViewer.html");

                    webBrowser.Visible = true;
                    // Update WebBrowser URL
                    webBrowser.URL = htmlUri;
                    
                    //webBrowser.Refresh();

                    // Additional delay to ensure the WebBrowser updates (workaround for rendering issues)
                    System.Threading.Thread.Sleep(100);
                    webBrowser.Refresh();
                    
                    statusLabel.Text = $"Waveform loaded: {fileName}";
                    cancelButton.Visible = false; // Hide cancel button
                }
                else
                {
                    Log.Error("WaveformViewer_List", "WebBrowser control not found");
                    statusLabel.Text = "Error: WebBrowser not found";
                    cancelButton.Visible = false; // Hide cancel button
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("WaveformViewer_List", $"Failed to fetch or display waveform file {fileName}: {ex.Message}");
            statusLabel.Text = $"Failed: {ex.Message}";
            cancelButton.Visible = false; // Hide cancel button
        }
    }

    [ExportMethod]
    public void CancelDownload()
    {
        try
        {
            if (fetchTask != null)
            {
                fetchTask.Cancel();
                fetchTask.Dispose();
                fetchTask = null;
                //Log.Info("WaveformViewer_List", "Fetch task canceled or disposed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel fetchTask: {ex.Message}");
        }

        try
        {
            if (displayWaveformTask != null)
            {
                displayWaveformTask.Cancel();
                displayWaveformTask.Dispose();
                displayWaveformTask = null;
                //Log.Info("WaveformViewer_List", "Display waveform task canceled or disposed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("WaveformViewer_List", $"Failed to cancel displayWaveformTask: {ex.Message}");
        }
        statusLabel.Text = "Download canceled by user";
        cancelButton.Visible = false; // Hide cancel button after cancellation
        //Log.Info("WaveformViewer_List", "Download canceled by user");
    }

    private void AssociatePowerQualityEvents()
{
    string remoteFilePath = "/LoggingResults/Power_Quality_Log.csv";
    string url = $"http://{ipAddress}{remoteFilePath}";
    string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
    string localFolder = Path.Combine(projectDir, deviceName);
    string localFilePath = Path.Combine(localFolder, "Power_Quality_Log.csv");

    try
    {
        statusLabel.Text = "Downloading Power Quality events...";
        //Log.Info("WaveformViewer_List", $"Downloading PQ events from {url}");

        var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
        bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
        if (!isLoggedIn)
        {
            statusLabel.Text = "Cannot download PQ events: Login to device failed.";
            Log.Error($"WaveformViewer_List-{deviceName}", "Cannot download PQ events due to login failure.");
            return;
        }

        Directory.CreateDirectory(localFolder);
        using (var client = new HttpClient())
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            byte[] fileBytes = client.GetByteArrayAsync(url).Result;
            File.WriteAllBytes(localFilePath, fileBytes);
            statusLabel.Text = "Processing Power Quality events...";
            //Log.Info("WaveformViewer_List", "PQ events downloaded and processing started");

            var pqEvents = ParsePowerQualityEvents(localFilePath);
            if (pqEvents == null || pqEvents.Count == 0)
            {
                statusLabel.Text = "No PQ events found to process.";
                //Log.Info("WaveformViewer_List", "No PQ events found in the CSV");
                return;
            }

            foreach (var fileObject in waveformFilesNode.Children)
            {
                var fileNameVar = fileObject.GetVariable("Filename");
                if (fileNameVar == null || string.IsNullOrEmpty(fileNameVar.Value))
                    continue;

                string fileName = fileNameVar.Value;
                // Extract components from waveform filename
                var match = Regex.Match(fileName, @"_(\d{8})_(\d{6})_(\d{6})_(\d{2})\.wfm");
                if (!match.Success)
                {
                    Log.Warning("WaveformViewer_List", $"Invalid waveform filename format: {fileName}");
                    continue;
                }

                string yearMonthDay = match.Groups[1].Value; // YYYYMMDD (e.g., 19800130)
                string hourMinSec = match.Groups[2].Value;   // hhmmss (e.g., 101141)
                string msUs = match.Groups[3].Value;         // ssssss (e.g., 275597)

                string year = yearMonthDay.Substring(0, 4);      // YYYY (e.g., 1980)
                string monthDay = yearMonthDay.Substring(4, 4);  // MMDD (e.g., 0130)
                string hourMin = hourMinSec.Substring(0, 4);     // hhmm (e.g., 1011)
                string sec = hourMinSec.Substring(4, 2);         // ss (e.g., 41)
                string ms = msUs.Substring(0, 3);                // sss (milliseconds, e.g., 275)
                string us = msUs.Substring(3, 3);                // sss (microseconds, e.g., 597)

                var matchingEvents = pqEvents
                    .Where(e =>
                        e.Association_Timestamp_Year == year &&
                        e.Association_Timestamp_Mth_Day == monthDay &&
                        e.Association_Timestamp_Hr_Min == hourMin &&
                        e.Association_Timestamp_Sec_mS == $"{sec}{ms}" &&
                        e.Association_Timestamp_uS == us)
                    .ToList();

                if (matchingEvents.Any())
                {
                    // Collect unique Event_Type values and join with commas
                    var eventTypes = matchingEvents
                        .Select(e => e.Event_Type)
                        .Distinct() // Remove duplicates
                        .OrderBy(type => type) // Optional: sort for consistent output
                        .ToList();
                    string eventTypeString = string.Join(", ", eventTypes);
                    fileObject.GetVariable("Event Type").Value = eventTypeString;
                    //Log.Info("WaveformViewer_List", $"Matched event types {eventTypeString} with waveform {fileName}");
                }
                else
                {
                    fileObject.GetVariable("Event Type").Value = "N/A";
                    //Log.Info("WaveformViewer_List", $"No matching event found for waveform {fileName}");
                }
            }

            statusLabel.Text = "PQ events processed and associated with waveforms.";
            //Log.Info("WaveformViewer_List", "PQ events association completed");
        }
    }
    catch (HttpRequestException ex)
    {
        statusLabel.Text = $"Failed to download PQ events: Cannot connect to {deviceName}.";
        Log.Error($"WaveformViewer_List-{deviceName}", $"HTTP error downloading PQ events: {ex.Message}");
    }
    catch (Exception ex)
    {
        statusLabel.Text = $"Failed to process PQ events: {ex.Message}";
        Log.Error($"WaveformViewer_List-{deviceName}", $"Unexpected error processing PQ events: {ex.Message}");
    }
}

    private List<PQEvent> ParsePowerQualityEvents(string csvFilePath)
    {
        try
        {
            var lines = File.ReadAllLines(csvFilePath);
            return lines.Skip(1) // Skip header
                .Select(line =>
                {
                    var row = line.Split(',');
                    // Normalize the timestamp fields to match waveform format
                    string monthDay = row[14].PadLeft(4, '0'); // Ensure MMDD (e.g., "130" -> "0130")
                    string hourMin = row[15].PadLeft(4, '0');  // Ensure hhmm (e.g., "1011" -> "1011")
                    string secMs = row[16].PadLeft(5, '0');    // Ensure sssss (e.g., "41275" -> "41275")
                    string us = row[17].PadLeft(3, '0');       // Ensure sss (e.g., "597" -> "597")

                    return new PQEvent
                    {
                        Record_Identifier = int.Parse(row[0]),
                        Event_Type = row[1],
                        Association_Timestamp_Year = row[13],      // YYYY (e.g., "1980")
                        Association_Timestamp_Mth_Day = monthDay,  // MMDD (e.g., "0130")
                        Association_Timestamp_Hr_Min = hourMin,    // hhmm (e.g., "1011")
                        Association_Timestamp_Sec_mS = secMs,      // sssss (e.g., "41275")
                        Association_Timestamp_uS = us,             // sss (e.g., "597")
                        UTC_Timestamp_Hr_Min = row[5],             // hh mm (e.g., "130")
                        Local_Timestamp = FormatTimestamp(row[3], row[4], row[5], row[6])
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("WaveformViewer_List", $"Error parsing PQ events CSV file {csvFilePath}: {ex.Message}");
            return new List<PQEvent>();
        }
    }

    private string FormatTimestamp(string date, string time, string utcTime, string secMs, string us = "")
    {
        return $"{date} {time}.{secMs}{us}".Trim();
    }
}

public class PQEvent
{
    public int Record_Identifier { get; set; }
    public string Event_Type { get; set; }
    public string Association_Timestamp_Year { get; set; }
    public string Association_Timestamp_Mth_Day { get; set; }
    public string Association_Timestamp_Hr_Min { get; set; }
    public string Association_Timestamp_Sec_mS { get; set; }
    public string Association_Timestamp_uS { get; set; }
    public string UTC_Timestamp_Hr_Min { get; set; }
    public string Local_Timestamp { get; set; }
}
