#region Using directives
using System;
using UAManagedCore;
using System.IO;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.Store;
using FTOptix.RAEtherNetIP;
using FTOptix.MicroController;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using FTOptix.SerialPort;
using FTOptix.OPCUAServer;
#endregion

public class Fetch_HarmonicsData : BaseNetLogic
{
    private PeriodicTask harmonicsFetchTask;
    private string deviceName;
    private string ipAddress;
    private string currentTableId = "500"; // Default table ID
    private string currentMetric = "No Selection"; // Default metric name
    private int startIndex = 0; // Start of the harmonic range
    private int endIndex = 0; // End of the harmonic range
    private LongRunningTask initializeTask;
    private const int maxHttpRetries = 3; // Number of retries for HTTP requests
    private const int retryDelayMs = 2000; // Delay between retries (2 seconds)
    private Dictionary<string, string> lastSuccessfulHarmonicsData; // Store last successful data
    private string lastSuccessfulMetric; // Store last successful metric
    private string lastRefreshTimestamp; // Store last refresh timestamp
    private bool isFirstFetch = true; // Flag for initial delay
    private IUAVariable periodVariable; // Variable for period (in ms)

    private ComboBox Main,Sub;

    // Mapping of table IDs to metric names
    private readonly Dictionary<string, string> tableIdToMetric = new Dictionary<string, string>
    {
        { "500", "No Selection" },
        { "14", "Total_kW_H1_RMS_DC_to_31" },
        { "15", "Total_kW_H2_RMS_32_to_63" },
        { "16", "Total_kW_H3_RMS_64_to_95" },
        { "17", "Total_kW_H4_RMS_96_to_127" },
        { "18", "Total_kVAR_H1_RMS_DC_to_31" },
        { "19", "Total_kVAR_H2_RMS_32_to_63" },
        { "20", "Total_kVAR_H3_RMS_64_to_95" },
        { "21", "Total_kVAR_H4_RMS_96_to_127" },
        { "22", "Total_kVA_H1_RMS_DC_to_31" },
        { "23", "Total_kVA_H2_RMS_32_to_63" },
        { "24", "Total_kVA_H3_RMS_64_to_95" },
        { "25", "Total_kVA_H4_RMS_96_to_127" },
        { "26", "V1_N_Volts_H1_RMS_DC_to_31" },
        { "27", "V1_N_Volts_H2_RMS_32_to_63" },
        { "28", "V1_N_Volts_H3_RMS_64_to_95" },
        { "29", "V1_N_Volts_H4_RMS_96_to_127" },
        { "30", "V2_N_Volts_H1_RMS_DC_to_31" },
        { "31", "V2_N_Volts_H2_RMS_32_to_63" },
        { "32", "V2_N_Volts_H3_RMS_64_to_95" },
        { "33", "V2_N_Volts_H4_RMS_96_to_127" },
        { "34", "V3_N_Volts_H1_RMS_DC_to_31" },
        { "35", "V3_N_Volts_H2_RMS_32_to_63" },
        { "36", "V3_N_Volts_H3_RMS_64_to_95" },
        { "37", "V3_N_Volts_H4_RMS_96_to_127" },
        { "38", "VN_G_Volts_H1_RMS_DC_to_31" },
        { "39", "VN_G_Volts_H2_RMS_32_to_63" },
        { "40", "VN_G_Volts_H3_RMS_64_to_95" },
        { "41", "VN_G_Volts_H4_RMS_96_to_127" },
        { "42", "V1_V2_Volts_H1_RMS_DC_to_31" },
        { "43", "V1_V2_Volts_H2_RMS_32_to_63" },
        { "44", "V1_V2_Volts_H3_RMS_64_to_95" },
        { "45", "V1_V2_Volts_H4_RMS_96_to_127" },
        { "46", "V2_V3_Volts_H1_RMS_DC_to_31" },
        { "47", "V2_V3_Volts_H2_RMS_32_to_63" },
        { "48", "V2_V3_Volts_H3_RMS_64_to_95" },
        { "49", "V2_V3_Volts_H4_RMS_96_to_127" },
        { "50", "V3_V1_Volts_H1_RMS_DC_to_31" },
        { "51", "V3_V1_Volts_H2_RMS_32_to_63" },
        { "52", "V3_V1_Volts_H3_RMS_64_to_95" },
        { "53", "V3_V1_Volts_H4_RMS_96_to_127" },
        { "54", "I1_Amps_H1_RMS_DC_to_31" },
        { "55", "I1_Amps_H2_RMS_32_to_63" },
        { "56", "I1_Amps_H3_RMS_64_to_95" },
        { "57", "I1_Amps_H4_RMS_96_to_127" },
        { "58", "I2_Amps_H1_RMS_DC_to_31" },
        { "59", "I2_Amps_H2_RMS_32_to_63" },
        { "60", "I2_Amps_H3_RMS_64_to_95" },
        { "61", "I2_Amps_H4_RMS_96_to_127" },
        { "62", "I3_Amps_H1_RMS_DC_to_31" },
        { "63", "I3_Amps_H2_RMS_32_to_63" },
        { "64", "I3_Amps_H3_RMS_64_to_95" },
        { "65", "I3_Amps_H4_RMS_96_to_127" },
        { "66", "I4_Amps_H1_RMS_DC_to_31" },
        { "67", "I4_Amps_H2_RMS_32_to_63" },
        { "68", "I4_Amps_H3_RMS_64_to_95" },
        { "69", "I4_Amps_H4_RMS_96_to_127" },
        { "70", "L1_kW_H1_RMS_DC_to_31" },
        { "71", "L1_kW_H2_RMS_32_to_63" },
        { "72", "L1_kW_H3_RMS_64_to_95" },
        { "73", "L1_kW_H4_RMS_96_to_127" },
        { "74", "L2_kW_H1_RMS_DC_to_31" },
        { "75", "L2_kW_H2_RMS_32_to_63" },
        { "76", "L2_kW_H3_RMS_64_to_95" },
        { "77", "L2_kW_H4_RMS_96_to_127" },
        { "78", "L3_kW_H1_RMS_DC_to_31" },
        { "79", "L3_kW_H2_RMS_32_to_63" },
        { "80", "L3_kW_H3_RMS_64_to_95" },
        { "81", "L3_kW_H4_RMS_96_to_127" },
        { "82", "L1_kVAR_H1_RMS_DC_to_31" },
        { "83", "L1_kVAR_H2_RMS_32_to_63" },
        { "84", "L1_kVAR_H3_RMS_64_to_95" },
        { "85", "L1_kVAR_H4_RMS_96_to_127" },
        { "86", "L2_kVAR_H1_RMS_DC_to_31" },
        { "87", "L2_kVAR_H2_RMS_32_to_63" },
        { "88", "L2_kVAR_H3_RMS_64_to_95" },
        { "89", "L2_kVAR_H4_RMS_96_to_127" },
        { "90", "L3_kVAR_H1_RMS_DC_to_31" },
        { "91", "L3_kVAR_H2_RMS_32_to_63" },
        { "92", "L3_kVAR_H3_RMS_64_to_95" },
        { "93", "L3_kVAR_H4_RMS_96_to_127" },
        { "94", "L1_kVA_H1_RMS_DC_to_31" },
        { "95", "L1_kVA_H2_RMS_32_to_63" },
        { "96", "L1_kVA_H3_RMS_64_to_95" },
        { "97", "L1_kVA_H4_RMS_96_to_127" },
        { "98", "L2_kVA_H1_RMS_DC_to_31" },
        { "99", "L2_kVA_H2_RMS_32_to_63" },
        { "100", "L2_kVA_H3_RMS_64_to_95" },
        { "101", "L2_kVA_H4_RMS_96_to_127" },
        { "102", "L3_kVA_H1_RMS_DC_to_31" },
        { "103", "L3_kVA_H2_RMS_32_to_63" },
        { "104", "L3_kVA_H3_RMS_64_to_95" },
        { "105", "L3_kVA_H4_RMS_96_to_127" },
        { "106", "V1_N_Volts_H1_Ang_DC_to_31" },
        { "107", "V1_N_Volts_H2_Ang_32_to_63" },
        { "108", "V1_N_Volts_H3_Ang_64_to_95" },
        { "109", "V1_N_Volts_H4_Ang_96_to_127" },
        { "110", "V2_N_Volts_H1_Ang_DC_to_31" },
        { "111", "V2_N_Volts_H2_Ang_32_to_63" },
        { "112", "V2_N_Volts_H3_Ang_64_to_95" },
        { "113", "V2_N_Volts_H4_Ang_96_to_127" },
        { "114", "V3_N_Volts_H1_Ang_DC_to_31" },
        { "115", "V3_N_Volts_H2_Ang_32_to_63" },
        { "116", "V3_N_Volts_H3_Ang_64_to_95" },
        { "117", "V3_N_Volts_H4_Ang_96_to_127" },
        { "118", "VN_G_Volts_H1_Ang_DC_to_31" },
        { "119", "VN_G_Volts_H2_Ang_32_to_63" },
        { "120", "VN_G_Volts_H3_Ang_64_to_95" },
        { "121", "VN_G_Volts_H4_Ang_96_to_127" },
        { "122", "V1_V2_Volts_H1_Ang_DC_to_31" },
        { "123", "V1_V2_Volts_H2_Ang_32_to_63" },
        { "124", "V1_V2_Volts_H3_Ang_64_to_95" },
        { "125", "V1_V2_Volts_H4_Ang_96_to_127" },
        { "126", "V2_V3_Volts_H1_Ang_DC_to_31" },
        { "127", "V2_V3_Volts_H2_Ang_32_to_63" },
        { "128", "V2_V3_Volts_H3_Ang_64_to_95" },
        { "129", "V2_V3_Volts_H4_Ang_96_to_127" },
        { "130", "V3_V1_Volts_H1_Ang_DC_to_31" },
        { "131", "V3_V1_Volts_H2_Ang_32_to_63" },
        { "132", "V3_V1_Volts_H3_Ang_64_to_95" },
        { "133", "V3_V1_Volts_H4_Ang_96_to_127" },
        { "134", "I1_Amps_H1_Ang_DC_to_31" },
        { "135", "I1_Amps_H2_Ang_32_to_63" },
        { "136", "I1_Amps_H3_Ang_64_to_95" },
        { "137", "I1_Amps_H4_Ang_96_to_127" },
        { "138", "I2_Amps_H1_Ang_DC_to_31" },
        { "139", "I2_Amps_H2_Ang_32_to_63" },
        { "140", "I2_Amps_H3_Ang_64_to_95" },
        { "141", "I2_Amps_H4_Ang_96_to_127" },
        { "142", "I3_Amps_H1_Ang_DC_to_31" },
        { "143", "I3_Amps_H2_Ang_32_to_63" },
        { "144", "I3_Amps_H3_Ang_64_to_95" },
        { "145", "I3_Amps_H4_Ang_96_to_127" },
        { "146", "I4_Amps_H1_Ang_DC_to_31" },
        { "147", "I4_Amps_H2_Ang_32_to_63" },
        { "148", "I4_Amps_H3_Ang_64_to_95" },
        { "149", "I4_Amps_H4_Ang_96_to_127" }
    };

    public override void Start()
    {
        try
        {
            base.Start();

            // Initialize last successful data and timestamp
            lastSuccessfulHarmonicsData = new Dictionary<string, string>();
            lastSuccessfulMetric = "No Selection";
            lastRefreshTimestamp = DateTime.UtcNow.ToString("o"); // ISO 8601 format

            // Validate the Tag alias and its variables
            var Tag = Owner.GetAlias("Tag");
            if (Tag == null)
            {
                Log.Error("Harmonics_DataFetcher", "Tag alias not found");
                return;
            }

            var ipVariable = Tag.GetVariable("Val_IPAddress");
            if (ipVariable == null)
            {
                Log.Error("Harmonics_DataFetcher", "Val_IPAddress variable not found in Tag");
                return;
            }

            ipAddress = ipVariable.Value;
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log.Error("Harmonics_DataFetcher", "IP address is null or empty");
                return;
            }

            deviceName = Tag.BrowseName;
            if (string.IsNullOrEmpty(deviceName))
            {
                Log.Error("Harmonics_DataFetcher", "Device name is null or empty");
                return;
            }
            
            periodVariable = Tag.GetVariable("Cfg_LiveDataUpdateRate");
            if (periodVariable == null)
            {
                Log.Error("Harmonics_DataFetcher", "Unable to find Cfg_LiveDataUpdateRate variable in LogicObject");
                return;
            }
            
            IUANode HormonicsMainModel = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Harmonics/raC_4_00_raC_Dvc_PM5000_PQEM_Harmonics_Main");
            Main = Owner.Get<ComboBox>("HorizontalLayout1/ComboBox_Main");
            Main.Model = HormonicsMainModel.NodeId;

            IUANode HormonicsSubModel = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Harmonics/raC_4_00_raC_Dvc_PM5000_PQEM_Harmonics_Main_Sub");
            Sub = Owner.Get<ComboBox>("HorizontalLayout1/ComboBox_Sub");
            Sub.Model = HormonicsSubModel.NodeId;


            // Set the initial tableId
            var tableId = LogicObject.GetVariable("tableId");
            tableId.Value = 14;

            // Start the initialization in a LongRunningTask
            initializeTask = new LongRunningTask(InitializeAsync, LogicObject);
            initializeTask.Start();

            // Start the periodic task with wrapper
            harmonicsFetchTask = new PeriodicTask(FetchHarmonicsRefreshDataWrapper, periodVariable.Value, LogicObject);
            //Log.Info("Harmonics_DataFetcher", $"Periodic task created and starting with {periodVariable.Value}ms interval for device {deviceName}");
            harmonicsFetchTask.Start();
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Start failed: {e.Message}");
        }
    }

    private async void InitializeAsync()
    {
        try
        {
            // Create a default JSON file immediately
            await CreateDefaultJsonFile();

            // Setup the device-specific HTML
            await SetupDeviceSpecificHtml();

            // Fetch initial harmonics data
            await FetchInitialHarmonicsData();
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Initialization failed: {e.Message}");
        }
    }

    private async Task CreateDefaultJsonFile()
    {
        var harmonicsData = new Dictionary<string, string>();
        var data = new
        {
            metric = "No Selection",
            harmonics = harmonicsData,
            lastRefreshTimestamp = lastRefreshTimestamp
        };

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res");
        string filePath = Path.Combine(directory, $"{deviceName}_HarmonicsData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            //Log.Info("Harmonics_DataFetcher", $"Created directory: {directory}");
        }

        string jsonData = JsonConvert.SerializeObject(data, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, jsonData);
        //Log.Info("Harmonics_DataFetcher", $"Created default JSON file at {filePath}");
    }

    private async Task SetupDeviceSpecificHtml()
    {
        var webBrowser = Owner.Get<WebBrowser>("HarmonicsBrowser");
        if (webBrowser == null)
        {
            Log.Error("Harmonics_DataFetcher", "WebBrowser control 'HarmonicsBrowser' not found");
            return;
        }

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string resDirectory = Path.Combine(baseDirectory, "res");

        if (!Directory.Exists(resDirectory))
        {
            Directory.CreateDirectory(resDirectory);
            //Log.Info("Harmonics_DataFetcher", $"Created directory: {resDirectory}");
        }

        string htmlTemplatePath = Path.Combine(resDirectory, "Template_Harmonics.html");
        string deviceHtmlPath = Path.Combine(resDirectory, $"HarmonicsDashboard_{deviceName}.html");

        if (File.Exists(htmlTemplatePath))
        {
            try
            {
                string htmlContent = await File.ReadAllTextAsync(htmlTemplatePath);
                htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
                await File.WriteAllTextAsync(deviceHtmlPath, htmlContent);

                string tempHtmlPath = Path.Combine(baseDirectory, "res", $"HarmonicsDashboard_{deviceName}.html");
                var deviceHtmlUri = ResourceUri.FromProjectRelativePath(tempHtmlPath);
                webBrowser.URL = deviceHtmlUri;

                //Log.Info("Harmonics_DataFetcher", $"Created and loaded HTML for device {deviceName} at {deviceHtmlPath}");
            }
            catch (Exception e)
            {
                Log.Error("Harmonics_DataFetcher", $"Error setting up HTML: {e.Message}");
            }
        }
        else
        {
            Log.Error("Harmonics_DataFetcher", $"HTML template not found at {htmlTemplatePath}");
        }
    }

    [ExportMethod]
    public void OnTableIdChanged(string tableId)
    {
        if (tableId == currentTableId)
        {
            Log.Info("Harmonics_DataFetcher", $"Table ID {tableId} is already selected, no action needed");
            return;
        }

        //Log.Info("Harmonics_DataFetcher", $"Table ID changed to {tableId}");
        currentTableId = tableId;
        currentMetric = tableIdToMetric.ContainsKey(tableId) ? tableIdToMetric[tableId] : "No Selection";

        // Set the harmonic range based on the selected metric
        if (currentMetric.Contains("H1"))
        {
            startIndex = 0;
            endIndex = 31;
        }
        else if (currentMetric.Contains("H2"))
        {
            startIndex = 32;
            endIndex = 63;
        }
        else if (currentMetric.Contains("H3"))
        {
            startIndex = 64;
            endIndex = 95;
        }
        else if (currentMetric.Contains("H4"))
        {
            startIndex = 96;
            endIndex = 127;
        }
        else
        {
            startIndex = 0;
            endIndex = 0;
        }

        // Run the initial fetch in a LongRunningTask
        var fetchTask = new LongRunningTask(() => 
        {
            FetchInitialHarmonicsData().GetAwaiter().GetResult();
        }, LogicObject);
        fetchTask.Start();
    }

    private void FetchHarmonicsRefreshDataWrapper()
    {
        //Log.Info("Harmonics_DataFetcher", "Periodic task triggered");
        Task.Run(async () =>
        {
            try
            {
                // Check device status
                IUAVariable device_status = Owner.GetAlias("Tag").GetVariable("DeviceStatus/Status");
                string status = device_status.Value;

                if (status != "Ready")
                {
                    Log.Warning("Energy_DataFetcher", $"Device status is {status}, fetch aborted.");
                    return;
                }

                if (isFirstFetch)
                {
                    //Log.Info("Harmonics_DataFetcher", "Delaying first fetch by 2 seconds");
                    await Task.Delay(2000); // Initial delay to prevent IO conflicts
                    isFirstFetch = false;
                }

                //Log.Info("Harmonics_DataFetcher", "Starting background fetch");
                await FetchHarmonicsRefreshData();
                //Log.Info("Harmonics_DataFetcher", "Background fetch completed");
            }
            catch (Exception e)
            {
                Log.Error("Harmonics_DataFetcher", $"Periodic fetch failed: {e.Message}");
            }
        });
    }

    private async Task FetchInitialHarmonicsData()
    {
        var harmonicsData = new Dictionary<string, string>();
        if (currentTableId == "500")
        {
            var data = new
            {
                metric = currentMetric,
                harmonics = harmonicsData,
                lastRefreshTimestamp = lastRefreshTimestamp
            };

            await WriteJsonFile(data, "Wrote empty harmonics data for 'No Selection'");
            lastSuccessfulHarmonicsData = harmonicsData;
            lastSuccessfulMetric = currentMetric;
            return;
        }

        using (var client = new HttpClient())
        {
            try
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/PowerQuality/cgi-bin/HarmonicsResults%7C{currentTableId}")
                {
                    Content = new StringContent("")
                };

                HttpResponseMessage response = null;
                for (int attempt = 1; attempt <= maxHttpRetries; attempt++)
                {
                    try
                    {
                        response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            break;
                        }
                        else
                        {
                            Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Initial harmonics request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Error fetching initial harmonics data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Error("Harmonics_DataFetcher", "Failed to fetch initial harmonics data after multiple attempts.");
                    var data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Initial fetch failed, using last successful data");
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();
                //Log.Info("Harmonics_DataFetcher", $"Raw response from HarmonicsResults%7C{currentTableId}:\n{responseData}");

                if (responseData.Trim() != "ok")
                {
                    Log.Error("Harmonics_DataFetcher", $"Table ID {currentTableId} selection failed: Server did not return 'ok'. Response: {responseData}");
                    var data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Initial fetch failed, using last successful data");
                    return;
                }

                //Log.Info("Harmonics_DataFetcher", $"Table ID {currentTableId} selection successful");
                await FetchHarmonicsRefreshData();
            }
            catch (Exception e)
            {
                Log.Error("Harmonics_DataFetcher", $"Error fetching initial harmonics data after {maxHttpRetries} attempts: {e.Message}");
                var data = new
                {
                    metric = lastSuccessfulMetric,
                    harmonics = lastSuccessfulHarmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };
                await WriteJsonFile(data, "Initial fetch failed, using last successful data");
            }
        }
    }

    private async Task FetchHarmonicsRefreshData()
    {
        if (currentTableId == "500")
        {
            //Log.Info("Harmonics_DataFetcher", "No table ID selected (500), skipping refresh");
            return;
        }

        var harmonicsData = new Dictionary<string, string>();
        using (var client = new HttpClient())
        {
            try
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/PowerQuality/cgi-bin/HarmonicsResults_Refresh")
                {
                    Content = new StringContent("")
                };

                object data = null;

                HttpResponseMessage response = null;
                for (int attempt = 1; attempt <= maxHttpRetries; attempt++)
                {
                    try
                    {
                        response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            break;
                        }
                        else
                        {
                            Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Harmonics refresh request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Harmonics_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Error fetching harmonics refresh data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Warning("Harmonics_DataFetcher", "Harmonics refresh failed after multiple attempts, keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(responseData))
                {
                    Log.Warning("Harmonics_DataFetcher", "No harmonics data received from server (refresh), keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                //Log.Info("Harmonics_DataFetcher", $"Raw response from HarmonicsResults_Refresh:\n{responseData}");

                string[] lines = responseData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string prefix = currentMetric;
                if (currentMetric.Contains("H1"))
                {
                    prefix = currentMetric.Replace("_H1_RMS_DC_to_31", "");
                }
                else if (currentMetric.Contains("H2"))
                {
                    prefix = currentMetric.Replace("_H2_RMS_32_to_63", "");
                }
                else if (currentMetric.Contains("H3"))
                {
                    prefix = currentMetric.Replace("_H3_RMS_64_to_95", "");
                }
                else if (currentMetric.Contains("H4"))
                {
                    prefix = currentMetric.Replace("_H4_RMS_96_to_127", "");
                }

                for (int i = 0; i < lines.Length - 1; i += 2)
                {
                    string key = lines[i].Trim();
                    string value = lines[i + 1].Trim();

                    if (key.StartsWith("Metering_"))
                    {
                        continue;
                    }

                    if (key.EndsWith("_H_RMS"))
                    {
                        string adjustedKey;
                        if (key.Contains("_DC_"))
                        {
                            adjustedKey = key.Replace("_H_RMS", "");
                        }
                        else
                        {
                            // Preserve "1st" for the 1st harmonic, adjust others to "th"
                            if (key.Contains("1st_H_RMS"))
                            {
                                adjustedKey = key.Replace("_H_RMS", ""); // Keep as "1st"
                            }
                            else
                            {
                                adjustedKey = key.Replace("st_H_RMS", "th").Replace("nd_H_RMS", "th").Replace("_H_RMS", "");
                            }
                        }

                        int harmonicOrder = 0;
                        if (adjustedKey.EndsWith("_DC"))
                        {
                            harmonicOrder = 0;
                        }
                        else
                        {
                            string orderStr = adjustedKey.Substring(adjustedKey.LastIndexOf('_') + 1).Replace("th", "").Replace("st", "");
                            if (int.TryParse(orderStr, out int order))
                            {
                                harmonicOrder = order;
                            }
                        }

                        if (harmonicOrder >= startIndex && harmonicOrder <= endIndex)
                        {
                            harmonicsData[adjustedKey] = value;
                        }
                    }
                }

                bool hasHarmonicsData = false;
                foreach (var key in harmonicsData.Keys)
                {
                    if (key.Contains("th") || key.EndsWith("_DC"))
                    {
                        hasHarmonicsData = true;
                        break;
                    }
                }

                if (!hasHarmonicsData)
                {
                    Log.Warning("Harmonics_DataFetcher", $"Refresh response does not contain harmonics data for the expected range ({startIndex} to {endIndex}), keeping last successful data");
                    data = new
                    {
                        metric = lastSuccessfulMetric,
                        harmonics = lastSuccessfulHarmonicsData,
                        lastRefreshTimestamp = lastRefreshTimestamp
                    };
                    await WriteJsonFile(data, "Refresh failed, using last successful data");
                    return;
                }

                lastSuccessfulHarmonicsData = new Dictionary<string, string>(harmonicsData);
                lastSuccessfulMetric = currentMetric;
                lastRefreshTimestamp = DateTime.UtcNow.ToString("o");

                data = new
                {
                    metric = currentMetric,
                    harmonics = harmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };

                await WriteJsonFile(data, "Refreshed harmonics data exported");
            }
            catch (Exception e)
            {
                Log.Warning("Harmonics_DataFetcher", $"Error fetching harmonics refresh data after {maxHttpRetries} attempts: {e.Message}, keeping last successful data");
                var data = new
                {
                    metric = lastSuccessfulMetric,
                    harmonics = lastSuccessfulHarmonicsData,
                    lastRefreshTimestamp = lastRefreshTimestamp
                };
                await WriteJsonFile(data, "Refresh failed, using last successful data");
            }
        }
    }

    private async Task WriteJsonFile(object data, string successMessage)
    {
        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res");
        string filePath = Path.Combine(directory, $"{deviceName}_HarmonicsData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            string jsonString = JsonConvert.SerializeObject(data, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, jsonString);
            //Log.Info("Harmonics_DataFetcher", $"{successMessage} to {filePath}");
        }
        catch (IOException e)
        {
            Log.Error("Harmonics_DataFetcher", $"IO error writing JSON file: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Harmonics_DataFetcher", $"Permission denied writing JSON file: {e.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            harmonicsFetchTask?.Dispose();
            initializeTask?.Dispose();
            Log.Info("Harmonics_DataFetcher", "Stopped fetching harmonics data");
            base.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Harmonics_DataFetcher", $"Stop failed: {e.Message}");
        }
    }
}
