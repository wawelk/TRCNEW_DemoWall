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

public class Fetch_WiringDiagnostics : BaseNetLogic
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
        try
        {
            base.Start();

            lastSuccessfulData = null;
            lastRefreshTimestamp = DateTime.UtcNow.ToString("o");

            var Tag = Owner.GetAlias("Tag");
            if (Tag == null)
            {
                Log.Error("Fetch_WiringDiagnostics", "Tag alias not found");
                return;
            }

            var ipVariable = Tag.GetVariable("Val_IPAddress");
            if (ipVariable == null)
            {
                Log.Error("Fetch_WiringDiagnostics", "Val_IPAddress variable not found in Tag");
                return;
            }

            ipAddress = ipVariable.Value;
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log.Error("Fetch_WiringDiagnostics", "IP address is null or empty");
                return;
            }

            deviceName = Tag.BrowseName;
            if (string.IsNullOrEmpty(deviceName))
            {
                Log.Error("Fetch_WiringDiagnostics", "Device name is null or empty");
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

            dataFetchTask = new PeriodicTask(FetchDataAsyncWrapper, periodVariable.Value, LogicObject);
            //Log.Info(Fetch_WiringDiagnostics", "Periodic task created and starting");
            dataFetchTask.Start();
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"Start failed: {e.Message}");
        }
    }

    private async void InitializeAsync()
    {
        try
        {
            //Log.Info(Fetch_WiringDiagnostics", "Starting initialization");
            await CreateDefaultJsonFile();
            await SetupDeviceSpecificHtml();
            await FetchData();
            //Log.Info(Fetch_WiringDiagnostics", "Initialization complete");
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"Initialization failed: {e.Message}");
        }
    }

    private async Task CreateDefaultJsonFile()
    {
        try
        {
            var defaultData = new
            {
                Command_Status = 0,
                Voltage_Input_Missing = 0,
                Current_Input_Missing = 0,
                Range1_L97_C89_Status = 0,
                Range1_Voltage_Input_Inverted = 0,
                Range1_Current_Input_Inverted = 0,
                Range1_Voltage_Rotation = 0,
                Range1_Current_Rotation = 0,
                Range2_L85_C98_Status = 0,
                Range2_Voltage_Input_Inverted = 0,
                Range2_Current_Input_Inverted = 0,
                Range2_Voltage_Rotation = 0,
                Range2_Current_Rotation = 0,
                Range3_L52_L95_Status = 0,
                Range3_Voltage_Input_Inverted = 0,
                Range3_Current_Input_Inverted = 0,
                Range3_Voltage_Rotation = 0,
                Range3_Current_Rotation = 0,
                Voltage_Phase_1_Angle = 0.0,
                Voltage_Phase_1_Magnitude = 0.0,
                Voltage_Phase_2_Angle = 0.0,
                Voltage_Phase_2_Magnitude = 0.0,
                Voltage_Phase_3_Angle = 0.0,
                Voltage_Phase_3_Magnitude = 0.0,
                Current_Phase_1_Angle = 0.0,
                Current_Phase_1_Magnitude = 0.0,
                Current_Phase_2_Angle = 0.0,
                Current_Phase_2_Magnitude = 0.0,
                Current_Phase_3_Angle = 0.0,
                Current_Phase_3_Magnitude = 0.0,
                lastRefreshTimestamp = lastRefreshTimestamp
            };

            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res");
            string filePath = Path.Combine(directory, $"{deviceName}_WiringDiagnosticsData.json");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                //Log.Info(Fetch_WiringDiagnostics", $"Created directory: {directory}");
            }

            string jsonData = JsonConvert.SerializeObject(defaultData, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, jsonData);
            //Log.Info(Fetch_WiringDiagnostics", $"Created default JSON file at {filePath}");
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"CreateDefaultJsonFile failed: {e.Message}");
            throw; // Re-throw to be caught by InitializeAsync
        }
    }

    private async Task SetupDeviceSpecificHtml()
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("WiringDiagnosticsBrowser");
            if (webBrowser == null)
            {
                Log.Error("Fetch_WiringDiagnostics", "WebBrowser control 'WiringDiagnosticsBrowser' not found");
                return;
            }

            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res");

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
                //Log.Info(Fetch_WiringDiagnostics", $"Created directory: {resDirectory}");
            }

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_WiringDiagnostics.html");
            string deviceHtmlPath = Path.Combine(resDirectory, $"WiringDiagnosticsDashboard_{deviceName}.html");

            if (File.Exists(htmlTemplatePath))
            {
                string htmlContent = await File.ReadAllTextAsync(htmlTemplatePath);
                htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
                await File.WriteAllTextAsync(deviceHtmlPath, htmlContent);

                string tempHtmlPath = Path.Combine(baseDirectory, "res", $"WiringDiagnosticsDashboard_{deviceName}.html");
                var deviceHtmlUri = ResourceUri.FromProjectRelativePath(tempHtmlPath);
                webBrowser.URL = deviceHtmlUri;

                //Log.Info(Fetch_WiringDiagnostics", $"Created and loaded HTML for device {deviceName} at {deviceHtmlPath}");
            }
            else
            {
                Log.Error("Fetch_WiringDiagnostics", $"HTML template not found at {htmlTemplatePath}");
            }
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"SetupDeviceSpecificHtml failed: {e.Message}");
        }
    }

    private void FetchDataAsyncWrapper()
    {
        //Log.Info(Fetch_WiringDiagnostics", "Periodic task triggered");
        Task.Run(async () =>
        {
            try
            {
                // Check device status
                IUAVariable device_status = Owner.GetAlias("Tag").GetVariable("DeviceStatus/Status");
                string status = device_status.Value;

                if (status != "Ready")
                {
                    Log.Warning("Fetch_WiringDiagnostics", $"Device status is {status}, fetch aborted.");
                    return;
                }
                if (isFirstFetch)
                {
                    //Log.Info(Fetch_WiringDiagnostics", "Delaying first fetch by 2 seconds");
                    await Task.Delay(2000);
                    isFirstFetch = false;
                }

                //Log.Info(Fetch_WiringDiagnostics", "Starting background fetch");
                await FetchData();
                //Log.Info(Fetch_WiringDiagnostics", "Background fetch completed");
            }
            catch (Exception e)
            {
                Log.Error("Fetch_WiringDiagnostics", $"Periodic fetch failed: {e.Message}");
            }
        });
    }

    private async Task FetchData()
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/Status/cgi-bin/WiringDiagnostics")
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
                            Log.Warning("Fetch_WiringDiagnostics", $"Attempt {attempt}/{maxHttpRetries}: Request failed with status: {response.StatusCode}");
                            if (attempt < maxHttpRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Fetch_WiringDiagnostics", $"Attempt {attempt}/{maxHttpRetries}: Error fetching data: {e.Message}");
                        if (attempt < maxHttpRetries)
                        {
                            await Task.Delay(retryDelayMs);
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Log.Warning("Fetch_WiringDiagnostics", "Failed to fetch data after multiple attempts, keeping last successful data");
                    if (lastSuccessfulData != null)
                    {
                        await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
                    }
                    return;
                }

                string responseData = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(responseData))
                {
                    Log.Warning("Fetch_WiringDiagnostics", "No data received from server, keeping last successful data");
                    if (lastSuccessfulData != null)
                    {
                        await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
                    }
                    return;
                }

                //Log.Info(Fetch_WiringDiagnostics", $"Raw response:\n{responseData}");

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
                    Command_Status = int.Parse(parsedData.GetValueOrDefault("Command_Status", "0")),
                    Voltage_Input_Missing = int.Parse(parsedData.GetValueOrDefault("Voltage_Input_Missing", "0")),
                    Current_Input_Missing = int.Parse(parsedData.GetValueOrDefault("Current_Input_Missing", "0")),
                    Range1_L97_C89_Status = int.Parse(parsedData.GetValueOrDefault("Range1_L97_C89_Status", "0")),
                    Range1_Voltage_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range1_Voltage_Input_Inverted", "0")),
                    Range1_Current_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range1_Current_Input_Inverted", "0")),
                    Range1_Voltage_Rotation = int.Parse(parsedData.GetValueOrDefault("Range1_Voltage_Rotation", "0")),
                    Range1_Current_Rotation = int.Parse(parsedData.GetValueOrDefault("Range1_Current_Rotation", "0")),
                    Range2_L85_C98_Status = int.Parse(parsedData.GetValueOrDefault("Range2_L85_C98_Status", "0")),
                    Range2_Voltage_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range2_Voltage_Input_Inverted", "0")),
                    Range2_Current_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range2_Current_Input_Inverted", "0")),
                    Range2_Voltage_Rotation = int.Parse(parsedData.GetValueOrDefault("Range2_Voltage_Rotation", "0")),
                    Range2_Current_Rotation = int.Parse(parsedData.GetValueOrDefault("Range2_Current_Rotation", "0")),
                    Range3_L52_L95_Status = int.Parse(parsedData.GetValueOrDefault("Range3_L52_L95_Status", "0")),
                    Range3_Voltage_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range3_Voltage_Input_Inverted", "0")),
                    Range3_Current_Input_Inverted = int.Parse(parsedData.GetValueOrDefault("Range3_Current_Input_Inverted", "0")),
                    Range3_Voltage_Rotation = int.Parse(parsedData.GetValueOrDefault("Range3_Voltage_Rotation", "0")),
                    Range3_Current_Rotation = int.Parse(parsedData.GetValueOrDefault("Range3_Current_Rotation", "0")),
                    Voltage_Phase_1_Angle = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_1_Angle", "0.0")),
                    Voltage_Phase_1_Magnitude = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_1_Magnitude", "0.0")),
                    Voltage_Phase_2_Angle = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_2_Angle", "0.0")),
                    Voltage_Phase_2_Magnitude = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_2_Magnitude", "0.0")),
                    Voltage_Phase_3_Angle = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_3_Angle", "0.0")),
                    Voltage_Phase_3_Magnitude = double.Parse(parsedData.GetValueOrDefault("Voltage_Phase_3_Magnitude", "0.0")),
                    Current_Phase_1_Angle = double.Parse(parsedData.GetValueOrDefault("Current_Phase_1_Angle", "0.0")),
                    Current_Phase_1_Magnitude = double.Parse(parsedData.GetValueOrDefault("Current_Phase_1_Magnitude", "0.0")),
                    Current_Phase_2_Angle = double.Parse(parsedData.GetValueOrDefault("Current_Phase_2_Angle", "0.0")),
                    Current_Phase_2_Magnitude = double.Parse(parsedData.GetValueOrDefault("Current_Phase_2_Magnitude", "0.0")),
                    Current_Phase_3_Angle = double.Parse(parsedData.GetValueOrDefault("Current_Phase_3_Angle", "0.0")),
                    Current_Phase_3_Magnitude = double.Parse(parsedData.GetValueOrDefault("Current_Phase_3_Magnitude", "0.0")),
                    lastRefreshTimestamp = lastRefreshTimestamp
                };

                lastSuccessfulData = data;
                await WriteJsonFile(data, "Data fetched and exported");
            }
        }
        catch (Exception e)
        {
            Log.Warning("Fetch_WiringDiagnostics", $"Error fetching data after {maxHttpRetries} attempts: {e.Message}, keeping last successful data");
            if (lastSuccessfulData != null)
            {
                await WriteJsonFile(lastSuccessfulData, "Fetch failed, using last successful data");
            }
        }
    }

    private async Task WriteJsonFile(object data, string successMessage)
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res");
            string filePath = Path.Combine(directory, $"{deviceName}_WiringDiagnosticsData.json");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string jsonString = JsonConvert.SerializeObject(data, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, jsonString);
            //Log.Info(Fetch_WiringDiagnostics", $"{successMessage} to {filePath}");
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"WriteJsonFile failed: {e.Message}");
            throw; // Re-throw to be caught by caller
        }
    }

    public override void Stop()
    {
        try
        {
            dataFetchTask?.Dispose();
            initializeTask?.Dispose();
            Log.Info("Fetch_WiringDiagnostics", "Stopped fetching data");
            base.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Fetch_WiringDiagnostics", $"Stop failed: {e.Message}");
        }
    }
}
