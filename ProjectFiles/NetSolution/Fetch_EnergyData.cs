#region Using directives
using System;
using UAManagedCore;
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
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
#endregion

public class Fetch_EnergyData : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private const string JsonFolder = "res"; // Folder under project directory
    private const string HtmlTemplateFile = "Template_EnergyData.html"; // HTML template file name
    private PeriodicTask fetchTask; // Task for periodic execution
    private IUAVariable periodVariable; // Variable for period (in ms)
    private bool isFirstFetch = true; // Flag for initial delay
    private Dictionary<string, float> lastSuccessfulData; // Store last successful fetch
    private string lastRefreshTimestamp; // Timestamp of last successful fetch

   

    public override void Start()
    {
        try
        {
            base.Start();

            lastSuccessfulData = null;
            lastRefreshTimestamp = DateTime.UtcNow.ToString("o"); // ISO 8601 format

            var Tag = Owner.GetAlias("Tag");
            if (Tag == null)
            {
                Log.Error("Energy_DataFetcher", "Tag alias not found");
                return;
            }

            deviceName = Tag.BrowseName;
            if (string.IsNullOrEmpty(deviceName))
            {
                Log.Error("Energy_DataFetcher", "Device name is null or empty");
                return;
            }

            var ipVariable = Tag.GetVariable("Val_IPAddress");
            if (ipVariable == null)
            {
                Log.Error("Energy_DataFetcher", "Val_IPAddress variable not found in Tag");
                return;
            }

            ipAddress = ipVariable.Value;
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log.Error("Energy_DataFetcher", "IP address is null or empty");
                return;
            }

            periodVariable = Tag.GetVariable("Cfg_LiveDataUpdateRate");
            if (periodVariable == null)
            {
                Log.Error("Energy_DataFetcher", "Unable to find Cfg_LiveDataUpdateRate variable in LogicObject");
                return;
            }

            SetupDeviceSpecificHtml();

            // Initial fetch in background
            Task.Run(async () =>
            {
                try
                {
                    await UpdateEnergyValuesAsync();
                }
                catch (Exception e)
                {
                    Log.Error("Energy_DataFetcher", $"Initial fetch failed: {e.Message}");
                }
            });

            fetchTask = new PeriodicTask(FetchAndUpdateAsyncWrapper, periodVariable.Value, LogicObject);
            //Log.Info("Energy_DataFetcher", $"Periodic task created and starting with {periodVariable.Value}ms interval for device {deviceName}");
            fetchTask.Start();
        }
        catch (Exception e)
        {
            Log.Error("Energy_DataFetcher", $"Start failed: {e.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            fetchTask?.Dispose();
            fetchTask = null;
            Log.Info("Energy_DataFetcher", $"Stopped periodic task for device {deviceName}");
            base.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Energy_DataFetcher", $"Stop failed: {e.Message}");
        }
    }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("EnergyDataBrowser");
            if (webBrowser == null)
            {
                Log.Error("Energy_DataFetcher", "WebBrowser control 'EnergyDataBrowser' not found");
                return;
            }

            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, JsonFolder);

            if (!Directory.Exists(resDirectory))
            {
                Directory.CreateDirectory(resDirectory);
                //Log.Info("Energy_DataFetcher", $"Created directory: {resDirectory}");
            }

            string htmlTemplatePath = Path.Combine(resDirectory, HtmlTemplateFile);
            string deviceHtmlPath = Path.Combine(resDirectory, $"EnergyData_{deviceName}.html");

            if (File.Exists(htmlTemplatePath))
            {
                string htmlContent = File.ReadAllText(htmlTemplatePath);
                htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
                File.WriteAllText(deviceHtmlPath, htmlContent);

                string tempHtmlPath = Path.Combine(baseDirectory, "res", $"EnergyData_{deviceName}.html");
                var deviceHtmlUri = ResourceUri.FromProjectRelativePath(tempHtmlPath);
                webBrowser.URL = deviceHtmlUri;

                //Log.Info("Energy_DataFetcher", $"Created and loaded HTML for device {deviceName} at {deviceHtmlPath}");
            }
            else
            {
                Log.Error("Energy_DataFetcher", $"HTML template not found at {htmlTemplatePath}");
            }
        }
        catch (IOException e)
        {
            Log.Error("Energy_DataFetcher", $"IO error creating HTML file: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Energy_DataFetcher", $"Permission denied when creating HTML file: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error("Energy_DataFetcher", $"Unexpected error setting up HTML: {e.Message}");
        }
    }

    private void FetchAndUpdateAsyncWrapper()
    {
        //Log.Info("Energy_DataFetcher", "Periodic task triggered");
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
                    //Log.Info("Energy_DataFetcher", "Delaying first fetch by 2 seconds");
                    await Task.Delay(2000);
                    isFirstFetch = false;
                }

                //Log.Info("Energy_DataFetcher", "Starting background fetch");
                await UpdateEnergyValuesAsync();
                //Log.Info("Energy_DataFetcher", "Background fetch completed");
            }
            catch (Exception e)
            {
                Log.Error("Energy_DataFetcher", $"Periodic fetch failed: {e.Message}");
            }
        });
    }

    public async Task<Dictionary<string, float>> FetchEnergyDataAsync()
    {
        var energyData = new Dictionary<string, float>();
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ipAddress}/MeteringResults/cgi-bin/Energy_Demand")
                {
                    Content = new StringContent("")
                };

                HttpResponseMessage response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string data = await response.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(data))
                    {
                        Log.Warning("Energy_DataFetcher", "No data received from server");
                        return energyData;
                    }

                    string[] lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length - 1; i += 2)
                    {
                        string key = lines[i].Trim();
                        float value = float.TryParse(lines[i + 1].Trim(), out float parsedValue) ? parsedValue : 0;
                        energyData[key] = value;
                    }

                    lastRefreshTimestamp = DateTime.UtcNow.ToString("o"); // Update timestamp on successful fetch
                    //Log.Info("Energy_DataFetcher", $"Fetched for {deviceName}: kWh_Fwd={energyData.GetValueOrDefault("kWh_Fwd")}, kWh_Rev={energyData.GetValueOrDefault("kWh_Rev")}");
                }
                else
                {
                    Log.Warning("Energy_DataFetcher", $"Request failed with status: {response.StatusCode}");
                }
            }
        }
        catch (HttpRequestException e)
        {
            Log.Warning("Energy_DataFetcher", $"Network error for {deviceName}: {e.Message}");
        }
        catch (TaskCanceledException)
        {
            Log.Warning("Energy_DataFetcher", $"Request timed out for {deviceName} - server unreachable");
        }
        catch (Exception e)
        {
            Log.Warning("Energy_DataFetcher", $"Unexpected error fetching data for {deviceName}: {e.Message}");
        }
        return energyData;
    }

    [ExportMethod]
    public async Task UpdateEnergyValuesAsync()
    {
        try
        {
            var energyData = await FetchEnergyDataAsync();
            if (energyData.Count > 0) // Only update lastSuccessfulData if fetch succeeds
            {
                lastSuccessfulData = energyData;
            }
            // Export with lastRefreshTimestamp, using last successful data if current fetch fails
            await ExportToJsonAsync(lastSuccessfulData ?? energyData);
        }
        catch (Exception e)
        {
            Log.Error("Energy_DataFetcher", $"UpdateEnergyValuesAsync failed: {e.Message}");
        }
    }

    private async Task ExportToJsonAsync(Dictionary<string, float> energyData)
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, JsonFolder);

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                //Log.Info("Energy_DataFetcher", $"Created directory: {directory}");
            }

            string filePath = Path.Combine(directory, $"{deviceName}_EnergyData.json");

            // Create an anonymous object to include lastRefreshTimestamp
            var jsonData = new
            {
                EnergyData = energyData,
                LastRefreshTimestamp = lastRefreshTimestamp
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(jsonData, jsonOptions);

            await File.WriteAllTextAsync(filePath, jsonString);
            //Log.Info("Energy_DataFetcher", $"Data exported to {filePath} for device {deviceName}");
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Error("Energy_DataFetcher", $"Permission denied when writing JSON file: {e.Message}");
        }
        catch (IOException e)
        {
            Log.Error("Energy_DataFetcher", $"IO error exporting to JSON: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error("Energy_DataFetcher", $"Unexpected error exporting to JSON: {e.Message}");
        }
    }
}
