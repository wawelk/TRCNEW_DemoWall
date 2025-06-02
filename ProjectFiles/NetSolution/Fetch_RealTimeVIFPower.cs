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
using Newtonsoft.Json;
using System.Collections.Generic;
using FTOptix.SerialPort;
using FTOptix.OPCUAServer;
#endregion

public class Fetch_RealTimeVIFPower : BaseNetLogic
{
    private PeriodicTask dataFetchTask;
    private string deviceName;
    private string ipAddress;
    private LongRunningTask initializeTask;
    private const int maxHttpRetries = 3;
    private const int retryDelayMs = 2000;
    private object lastSuccessfulData;
    private string lastRefreshTimestamp;
    private bool isFirstFetch = true; // Flag for initial delay
    private IUAVariable periodVariable;

    public override void Start()
    {
        base.Start();

        lastSuccessfulData = null;
        lastRefreshTimestamp = DateTime.UtcNow.ToString("o");

        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "Device name is null or empty");
            return;
        }

        periodVariable = Tag.GetVariable("Cfg_LiveDataUpdateRate");
        if (periodVariable == null)
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "Unable to find Cfg_LiveDataUpdateRate variable in LogicObject");
            return;
        }

        initializeTask = new LongRunningTask(InitializeAsync, LogicObject);
        initializeTask.Start();

        // Create and start PeriodicTask immediately
        dataFetchTask = new PeriodicTask(FetchDataAsyncWrapper, periodVariable.Value, LogicObject);
        Log.Info("RealTimeVIFPower_DataFetcher", "Periodic task created and starting");
        dataFetchTask.Start();
    }

    private async void InitializeAsync()
    {
        try
        {
            //Log.Info("RealTimeVIFPower_DataFetcher", "Starting initialization");
            await CreateDefaultJsonFile();
            await SetupDeviceSpecificHtml();
            await FetchData();
            //Log.Info("RealTimeVIFPower_DataFetcher", "Initialization complete");
        }
        catch (Exception e)
        {
            Log.Error("RealTimeVIFPower_DataFetcher", $"Initialization failed: {e.Message}");
        }
    }

    private async Task CreateDefaultJsonFile()
    {
        var defaultData = new
        {
            Metering_Date_Stamp = "0",
            Metering_Time_Stamp = "0",
            Metering_Microsecond_Stamp = "0",
            V1_N_Volts = 0.0,
            V2_N_Volts = 0.0,
            V3_N_Volts = 0.0,
            VN_G_Volts = 0.0,
            Avg_V_N_Volts = 0.0,
            V1_V2_Volts = 0.0,
            V2_V3_Volts = 0.0,
            V3_V1_Volts = 0.0,
            Avg_VL_VL_Volts = 0.0,
            I1_Amps = 0.0,
            I2_Amps = 0.0,
            I3_Amps = 0.0,
            I4_Amps = 0.0,
            Avg_Amps = 0.0,
            Frequency_Hz = 0.0,
            Avg_Frequency_Hz = 0.0,
            L1_kW = 0.0,
            L2_kW = 0.0,
            L3_kW = 0.0,
            Total_kW = 0.0,
            L1_kVAR = 0.0,
            L2_kVAR = 0.0,
            L3_kVAR = 0.0,
            Total_kVAR = 0.0,
            L1_kVA = 0.0,
            L2_kVA = 0.0,
            L3_kVA = 0.0,
            Total_kVA = 0.0,
            L1_True_PF = 0.0,
            L2_True_PF = 0.0,
            L3_True_PF = 0.0,
            Total_True_PF = 0.0,
            L1_Disp_PF = 0.0,
            L2_Disp_PF = 0.0,
            L3_Disp_PF = 0.0,
            Total_Disp_PF = 0.0,
            L1_PF_Lead_Lag_Indicator = 0,
            L2_PF_Lead_Lag_Indicator = 0,
            L3_PF_Lead_Lag_Indicator = 0,
            Total_PF_Lead_Lag_Indicator = 0,
            Voltage_Rotation = "0",
            Metering_Iteration = "0",
            lastRefreshTimestamp = lastRefreshTimestamp
        };

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res");
        string filePath = Path.Combine(directory, $"{deviceName}_RealTimeVIFPowerData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            //Log.Info("RealTimeVIFPower_DataFetcher", $"Created directory: {directory}");
        }

        string jsonData = JsonConvert.SerializeObject(defaultData, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, jsonData);
        //Log.Info("RealTimeVIFPower_DataFetcher", $"Created default JSON file at {filePath}");
    }

    private async Task SetupDeviceSpecificHtml()
    {
        var webBrowser = Owner.Get<WebBrowser>("RealTimeVIFPowerBrowser");
        if (webBrowser == null)
        {
            Log.Error("RealTimeVIFPower_DataFetcher", "WebBrowser control 'RealTimeVIFPowerBrowser' not found");
            return;
        }

        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string resDirectory = Path.Combine(baseDirectory, "res");

        if (!Directory.Exists(resDirectory))
        {
            Directory.CreateDirectory(resDirectory);
            //Log.Info("RealTimeVIFPower_DataFetcher", $"Created directory: {resDirectory}");
        }

        string htmlTemplatePath = Path.Combine(resDirectory, "Template_RealTimeVIFPower.html");
        string deviceHtmlPath = Path.Combine(resDirectory, $"RealTimeVIFPowerDashboard_{deviceName}.html");

        if (File.Exists(htmlTemplatePath))
        {
            try
            {
                string htmlContent = await File.ReadAllTextAsync(htmlTemplatePath);
                htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
                await File.WriteAllTextAsync(deviceHtmlPath, htmlContent);

                string tempHtmlPath = Path.Combine(baseDirectory, "res", $"RealTimeVIFPowerDashboard_{deviceName}.html");
                var deviceHtmlUri = ResourceUri.FromProjectRelativePath(tempHtmlPath);
                webBrowser.URL = deviceHtmlUri;

                //Log.Info("RealTimeVIFPower_DataFetcher", $"Created and loaded HTML for device {deviceName} at {deviceHtmlPath}");
            }
            catch (Exception e)
            {
                Log.Error("RealTimeVIFPower_DataFetcher", $"Error setting up HTML: {e.Message}");
            }
        }
        else
        {
            Log.Error("RealTimeVIFPower_DataFetcher", $"HTML template not found at {htmlTemplatePath}");
        }
    }

    private void FetchDataAsyncWrapper()
    {
        //Log.Info("RealTimeVIFPower_DataFetcher", "Periodic task triggered");
        Task.Run(async () =>
        {
            try
            {
                // Check device status
                IUAVariable device_status = Owner.GetAlias("Tag").GetVariable("DeviceStatus/Status");
                string status = device_status.Value;

                if (status != "Ready")
                {
                    Log.Warning("RealTimeVIFPower_DataFetcher", $"Device status is {status}, fetch aborted.");
                    return;
                }
                if (isFirstFetch)
                {
                    //Log.Info("RealTimeVIFPower_DataFetcher", "Delaying first fetch by 2 seconds");
                    await Task.Delay(2000); // Delay first fetch to avoid overlap
                    isFirstFetch = false;
                }

                //Log.Info("RealTimeVIFPower_DataFetcher", "Starting background fetch");
                await FetchData();
                //Log.Info("RealTimeVIFPower_DataFetcher", "Background fetch completed");
            }
            catch (Exception e)
            {
                Log.Error("RealTimeVIFPower_DataFetcher", $"Periodic fetch failed: {e.Message}");
            }
        });
    }

    private async Task FetchData()
    {
        using (var client = new HttpClient())
        {
            try
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/MeteringResults/cgi-bin/RealTime_VIF_Power")
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
                            Log.Warning("RealTimeVIFPower_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("RealTimeVIFPower_DataFetcher", $"Attempt {attempt}/{maxHttpRetries}: Error fetching data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Warning("RealTimeVIFPower_DataFetcher", "Failed to fetch data after multiple attempts, keeping last successful data");
                    if (lastSuccessfulData != null)
                    {
                        await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
                    }
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(responseData))
                {
                    Log.Warning("RealTimeVIFPower_DataFetcher", "No data received from server, keeping last successful data");
                    if (lastSuccessfulData != null)
                    {
                        await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
                    }
                    return;
                }

                //Log.Info("RealTimeVIFPower_DataFetcher", $"Raw response:\n{responseData}");

                string[] lines = responseData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var parsedData = new Dictionary<string, string>();
                for (int i = 0; i < lines.Length - 1; i += 2)
                {
                    string key = lines[i].Trim();
                    string value = lines[i + 1].Trim();
                    parsedData[key] = value;
                }

                lastRefreshTimestamp = DateTime.UtcNow.ToString("o");
                data = new
                {
                    Metering_Date_Stamp = parsedData.GetValueOrDefault("Metering_Date_Stamp", "0"),
                    Metering_Time_Stamp = parsedData.GetValueOrDefault("Metering_Time_Stamp", "0"),
                    Metering_Microsecond_Stamp = parsedData.GetValueOrDefault("Metering_Microsecond_Stamp", "0"),
                    V1_N_Volts = double.Parse(parsedData.GetValueOrDefault("V1_N_Volts", "0")),
                    V2_N_Volts = double.Parse(parsedData.GetValueOrDefault("V2_N_Volts", "0")),
                    V3_N_Volts = double.Parse(parsedData.GetValueOrDefault("V3_N_Volts", "0")),
                    VN_G_Volts = double.Parse(parsedData.GetValueOrDefault("VN_G_Volts", "0")),
                    Avg_V_N_Volts = double.Parse(parsedData.GetValueOrDefault("Avg_V_N_Volts", "0")),
                    V1_V2_Volts = double.Parse(parsedData.GetValueOrDefault("V1_V2_Volts", "0")),
                    V2_V3_Volts = double.Parse(parsedData.GetValueOrDefault("V2_V3_Volts", "0")),
                    V3_V1_Volts = double.Parse(parsedData.GetValueOrDefault("V3_V1_Volts", "0")),
                    Avg_VL_VL_Volts = double.Parse(parsedData.GetValueOrDefault("Avg_VL_VL_Volts", "0")),
                    I1_Amps = double.Parse(parsedData.GetValueOrDefault("I1_Amps", "0")),
                    I2_Amps = double.Parse(parsedData.GetValueOrDefault("I2_Amps", "0")),
                    I3_Amps = double.Parse(parsedData.GetValueOrDefault("I3_Amps", "0")),
                    I4_Amps = double.Parse(parsedData.GetValueOrDefault("I4_Amps", "0")),
                    Avg_Amps = double.Parse(parsedData.GetValueOrDefault("Avg_Amps", "0")),
                    Frequency_Hz = double.Parse(parsedData.GetValueOrDefault("Frequency_Hz", "0")),
                    Avg_Frequency_Hz = double.Parse(parsedData.GetValueOrDefault("Avg_Frequency_Hz", "0")),
                    L1_kW = double.Parse(parsedData.GetValueOrDefault("L1_kW", "0")),
                    L2_kW = double.Parse(parsedData.GetValueOrDefault("L2_kW", "0")),
                    L3_kW = double.Parse(parsedData.GetValueOrDefault("L3_kW", "0")),
                    Total_kW = double.Parse(parsedData.GetValueOrDefault("Total_kW", "0")),
                    L1_kVAR = double.Parse(parsedData.GetValueOrDefault("L1_kVAR", "0")),
                    L2_kVAR = double.Parse(parsedData.GetValueOrDefault("L2_kVAR", "0")),
                    L3_kVAR = double.Parse(parsedData.GetValueOrDefault("L3_kVAR", "0")),
                    Total_kVAR = double.Parse(parsedData.GetValueOrDefault("Total_kVAR", "0")),
                    L1_kVA = double.Parse(parsedData.GetValueOrDefault("L1_kVA", "0")),
                    L2_kVA = double.Parse(parsedData.GetValueOrDefault("L2_kVA", "0")),
                    L3_kVA = double.Parse(parsedData.GetValueOrDefault("L3_kVA", "0")),
                    Total_kVA = double.Parse(parsedData.GetValueOrDefault("Total_kVA", "0")),
                    L1_True_PF = double.Parse(parsedData.GetValueOrDefault("L1_True_PF_%", "0")),
                    L2_True_PF = double.Parse(parsedData.GetValueOrDefault("L2_True_PF_%", "0")),
                    L3_True_PF = double.Parse(parsedData.GetValueOrDefault("L3_True_PF_%", "0")),
                    Total_True_PF = double.Parse(parsedData.GetValueOrDefault("Total_True_PF", "0")),
                    L1_Disp_PF = double.Parse(parsedData.GetValueOrDefault("L1_Disp_PF", "0")),
                    L2_Disp_PF = double.Parse(parsedData.GetValueOrDefault("L2_Disp_PF", "0")),
                    L3_Disp_PF = double.Parse(parsedData.GetValueOrDefault("L3_Disp_PF", "0")),
                    Total_Disp_PF = double.Parse(parsedData.GetValueOrDefault("Total_Disp_PF", "0")),
                    L1_PF_Lead_Lag_Indicator = int.Parse(parsedData.GetValueOrDefault("L1_PF_Lead_Lag_Indicator", "0")),
                    L2_PF_Lead_Lag_Indicator = int.Parse(parsedData.GetValueOrDefault("L2_PF_Lead_Lag_Indicator", "0")),
                    L3_PF_Lead_Lag_Indicator = int.Parse(parsedData.GetValueOrDefault("L3_PF_Lead_Lag_Indicator", "0")),
                    Total_PF_Lead_Lag_Indicator = int.Parse(parsedData.GetValueOrDefault("Total_PF_Lead_Lag_Indicator", "0")),
                    Voltage_Rotation = parsedData.GetValueOrDefault("Voltage_Rotation", "0"),
                    Metering_Iteration = parsedData.GetValueOrDefault("Metering_Iteration", "0"),
                    lastRefreshTimestamp = lastRefreshTimestamp
                };

                lastSuccessfulData = data;
                await WriteJsonFile(data, "Data fetched and exported");
            }
            catch (Exception e)
            {
                Log.Warning("RealTimeVIFPower_DataFetcher", $"Error fetching data after {maxHttpRetries} attempts: {e.Message}, keeping last successful data");
                if (lastSuccessfulData != null)
                {
                    await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
                }
            }
        }
    }

    private async Task WriteJsonFile(object data, string successMessage)
    {
        var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
        string baseDirectory = filePathValue.Uri;
        string directory = Path.Combine(baseDirectory, "res");
        string filePath = Path.Combine(directory, $"{deviceName}_RealTimeVIFPowerData.json");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string jsonString = JsonConvert.SerializeObject(data, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, jsonString);
        //Log.Info("RealTimeVIFPower_DataFetcher", $"{successMessage} to {filePath}");
    }

    public override void Stop()
    {
        dataFetchTask?.Dispose();
        initializeTask?.Dispose();
        Log.Info("RealTimeVIFPower_DataFetcher", "Stopped fetching data");
        base.Stop();
    }
}
