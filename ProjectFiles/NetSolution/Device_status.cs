using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.MicroController;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using FTOptix.SerialPort;
using System.Collections.Generic;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;

public class Device_status : BaseNetLogic
{
    private PeriodicTask readinessCheckTask;
    private readonly HttpClient httpClient;
    private string deviceName;
    private string ipAddress;
    private int LiveDataUpdateRate;

    public Device_status()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public override void Start()
    {
        var Tag = Owner;
        if (Tag == null)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "Device name is null or empty");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "IP address is null or empty");
            return;
        }

        LiveDataUpdateRate = Math.Max((int)(Tag.GetVariable("Cfg_LiveDataUpdateRate")?.Value ?? 3000), 3000);

        InitializeDeviceStatusNode(Tag);
        InitializeDeviceAlarmsNode(Tag);
        InitializeRealTimeVIFNode(Tag);

        readinessCheckTask = new PeriodicTask(CheckDeviceReadiness, LiveDataUpdateRate, LogicObject);
        readinessCheckTask.Start();
    }

    public override void Stop()
    {
        if (readinessCheckTask != null)
        {
            readinessCheckTask.Dispose();
        }
        httpClient.Dispose();
        Log.Info($"DeviceReadinessChecker-{deviceName}", "DeviceStatus stopped.");
    }

    private async void CheckDeviceReadiness(PeriodicTask task)
    {
        await UpdateDeviceStatus();

        // Check device status
        IUAVariable deviceStatusVar = Owner.GetVariable("DeviceStatus/Status");
        if (deviceStatusVar == null)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "DeviceStatus/Status variable not found, VIF fetch aborted.");
            return;
        }

        string status = deviceStatusVar.Value.Value?.ToString();
        if (status != "Ready")
        {
            Log.Warning($"DeviceReadinessChecker-{deviceName}", $"Device status is {status ?? "null"}, VIF fetch aborted.");
            return;
        }

        await UpdateRealTimeVIF();
    }

    private async Task UpdateDeviceStatus()
    {
        try
        {
            var response = await httpClient.GetAsync($"http://{ipAddress}/overview.shtm");
            
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                var deviceInfo = ParseDeviceInfo(content);
                deviceInfo.Status = "Ready";
                deviceInfo.Timestamp = DateTime.Now;
                await FetchAndUpdateAlarms();

                SaveToJson(deviceInfo);
                UpdateTagVariables(deviceInfo);
            }
            else
            {
                SaveCommunicationFailure();
            }
        }
        catch (Exception)
        {
            SaveCommunicationFailure();
        }
    }

    private async Task FetchAndUpdateAlarms()
    {
        try
        {
            var alarmResponse = await httpClient.PostAsync($"http://{ipAddress}/Status/cgi-bin/Alarms", null);
            if (alarmResponse.IsSuccessStatusCode)
            {
                string alarmContent = await alarmResponse.Content.ReadAsStringAsync();
                var alarmValues = ParseAlarms(alarmContent);
                UpdateDeviceAlarmsNode(alarmValues);
            }
            else
            {
                Log.Warning($"DeviceReadinessChecker-{deviceName}", $"Failed to fetch alarms: HTTP Error {alarmResponse.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", $"Error fetching alarms: {ex.Message}");
        }
    }

    private async Task UpdateRealTimeVIF()
    {
        try
        {
            var response = await httpClient.GetAsync($"http://{ipAddress}/MeteringResults/cgi-bin/RealTime_VIF_Power");
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                var vifData = ParseRealTimeVIF(content);
                UpdateRealTimeVIFNode(vifData);
            }
            else
            {
                Log.Warning($"DeviceReadinessChecker-{deviceName}", $"Failed to fetch RealTime_VIF data: HTTP Error {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"DeviceReadinessChecker: {ipAddress}", $"Error fetching RealTime_VIF data: {ex.Message}");
        }
    }

    private Dictionary<string, object> ParseRealTimeVIF(string content)
    {
        var vifData = new Dictionary<string, object>();
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i += 2)
        {
            if (i + 1 >= lines.Length) break;

            string key = lines[i].Trim();
            string value = lines[i + 1].Trim();

            // Determine data type based on key and value
            if (key.Contains("Stamp") || key == "Metering_Iteration")
            {
                if (int.TryParse(value, out int intValue))
                {
                    vifData[key] = intValue;
                }
            }
            else if (key.Contains("Volts") || key.Contains("Amps") || key.Contains("kW") || 
                     key.Contains("kVAR") || key.Contains("kVA") || key.Contains("Frequency") || 
                     key.Contains("PF_%"))
            {
                if (float.TryParse(value, out float floatValue))
                {
                    vifData[key] = floatValue;
                }
            }
            else if (key == "Voltage_Rotation" || key.Contains("Indicator"))
            {
                vifData[key] = value; // Store as string
            }
        }

        return vifData;
    }

    private void InitializeRealTimeVIFNode(IUANode tag)
    {
        var realTimeVIFNode = tag.GetObject("RealTime_VIF");
        if (realTimeVIFNode == null)
        {
            realTimeVIFNode = InformationModel.MakeObject("RealTime_VIF");
            tag.Add(realTimeVIFNode);
        }

        var variables = new[]
        {
            ("Metering_Date_Stamp", OpcUa.DataTypes.Int32),
            ("Metering_Time_Stamp", OpcUa.DataTypes.Int32),
            ("Metering_Microsecond_Stamp", OpcUa.DataTypes.Int32),
            ("V1_N_Volts", OpcUa.DataTypes.Float),
            ("V2_N_Volts", OpcUa.DataTypes.Float),
            ("V3_N_Volts", OpcUa.DataTypes.Float),
            ("VN_G_Volts", OpcUa.DataTypes.Float),
            ("Avg_V_N_Volts", OpcUa.DataTypes.Float),
            ("V1_V2_Volts", OpcUa.DataTypes.Float),
            ("V2_V3_Volts", OpcUa.DataTypes.Float),
            ("V3_V1_Volts", OpcUa.DataTypes.Float),
            ("Avg_VL_VL_Volts", OpcUa.DataTypes.Float),
            ("I1_Amps", OpcUa.DataTypes.Float),
            ("I2_Amps", OpcUa.DataTypes.Float),
            ("I3_Amps", OpcUa.DataTypes.Float),
            ("I4_Amps", OpcUa.DataTypes.Float),
            ("Avg_Amps", OpcUa.DataTypes.Float),
            ("Frequency_Hz", OpcUa.DataTypes.Float),
            ("Avg_Frequency_Hz", OpcUa.DataTypes.Float),
            ("L1_kW", OpcUa.DataTypes.Float),
            ("L2_kW", OpcUa.DataTypes.Float),
            ("L3_kW", OpcUa.DataTypes.Float),
            ("Total_kW", OpcUa.DataTypes.Float),
            ("L1_kVAR", OpcUa.DataTypes.Float),
            ("L2_kVAR", OpcUa.DataTypes.Float),
            ("L3_kVAR", OpcUa.DataTypes.Float),
            ("Total_kVAR", OpcUa.DataTypes.Float),
            ("L1_kVA", OpcUa.DataTypes.Float),
            ("L2_kVA", OpcUa.DataTypes.Float),
            ("L3_kVA", OpcUa.DataTypes.Float),
            ("Total_kVA", OpcUa.DataTypes.Float),
            ("L1_True_PF_%", OpcUa.DataTypes.Float),
            ("L2_True_PF_%", OpcUa.DataTypes.Float),
            ("L3_True_PF_%", OpcUa.DataTypes.Float),
            ("Total_True_PF", OpcUa.DataTypes.Float),
            ("L1_Disp_PF", OpcUa.DataTypes.Float),
            ("L2_Disp_PF", OpcUa.DataTypes.Float),
            ("L3_Disp_PF", OpcUa.DataTypes.Float),
            ("Total_Disp_PF", OpcUa.DataTypes.Float),
            ("L1_PF_Lead_Lag_Indicator", OpcUa.DataTypes.String),
            ("L2_PF_Lead_Lag_Indicator", OpcUa.DataTypes.String),
            ("L3_PF_Lead_Lag_Indicator", OpcUa.DataTypes.String),
            ("Total_PF_Lead_Lag_Indicator", OpcUa.DataTypes.String),
            ("Voltage_Rotation", OpcUa.DataTypes.String),
            ("Metering_Iteration", OpcUa.DataTypes.Int32)
        };

        foreach (var (name, dataType) in variables)
        {
            if (realTimeVIFNode.GetVariable(name) == null)
            {
                var variable = InformationModel.MakeVariable(name, dataType);
                realTimeVIFNode.Add(variable);
            }
        }
    }

    private void UpdateRealTimeVIFNode(Dictionary<string, object> vifData)
    {
        var tag = Owner;
        var realTimeVIFNode = tag.GetObject("RealTime_VIF");
        if (realTimeVIFNode == null)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "RealTime_VIF object not found under Tag");
            return;
        }

        foreach (var item in vifData)
        {
            var variable = realTimeVIFNode.GetVariable(item.Key);
            if (variable != null)
            {
                if (item.Value is int intValue)
                    variable.Value = intValue;
                else if (item.Value is float floatValue)
                    variable.Value = floatValue;
                else if (item.Value is string stringValue)
                    variable.Value = stringValue;
            }
        }
    }

    private Dictionary<string, int> ParseAlarms(string alarmContent)
    {
        var alarmValues = new Dictionary<string, int>();
        var lines = alarmContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i += 2)
        {
            if (i + 1 >= lines.Length) break;

            string alarmName = lines[i].Trim();
            if (int.TryParse(lines[i + 1].Trim(), out int value))
            {
                alarmValues[alarmName] = value;
            }
        }

        return alarmValues;
    }

    private DeviceStatus ParseDeviceInfo(string htmlContent)
    {
        var deviceInfo = new DeviceStatus
        {
            Details = new DeviceDetails()
        };

        try
        {
            var patterns = new (string Key, string Pattern)[]
            {
                ("Device_Name", @"<div id\s*=\s*""Device_Name"">(.*?)<"),
                ("Device_Location", @"<div id\s*=\s*""Device_Location"">(.*?)<"),
                ("IP_Address", @"<div id\s*=\s*""IP_Address"">(.*?)</div>"),
                ("Ethernet_Address", @"<div id\s*=\s*""Ethernet_Address"">(.*?)<"),
                ("Hardware_Revision", @"<div id\s*=\s*""Hardware_Revision"">(.*?)</div>"),
                ("Firmware_Revision", @"<div id\s*=\s*""Firmware_Revision"">(.*?)<"),
                ("Catalog_Number", @"<div id\s*=\s*""Catalog_Number"">(.*?)</div>"),
                ("Installed_Options", @"<div id\s*=\s*""Installed_Options"">(.*?)<"),
                ("Original_Catalog_Number", @"<div id\s*=\s*""Original_Catalog_Number"">(.*?)</div>"),
                ("Series_Letter", @"<div id\s*=\s*""Series_Letter"">(.*?)<")
            };

            foreach (var (key, pattern) in patterns)
            {
                var match = Regex.Match(htmlContent, pattern);
                if (match.Success)
                {
                    string value = match.Groups[1].Value.Trim();
                    switch (key)
                    {
                        case "Device_Name": deviceInfo.Details.DeviceName = value; break;
                        case "Device_Location": deviceInfo.Details.DeviceLocation = value; break;
                        case "IP_Address": deviceInfo.Details.IPAddress = value; break;
                        case "Ethernet_Address": deviceInfo.Details.EthernetAddress = value; break;
                        case "Hardware_Revision": deviceInfo.Details.HardwareRevision = value; break;
                        case "Firmware_Revision": deviceInfo.Details.FirmwareRevision = value; break;
                        case "Catalog_Number": deviceInfo.Details.CatalogNumber = value; break;
                        case "Installed_Options": deviceInfo.Details.InstalledOptions = value; break;
                        case "Original_Catalog_Number": deviceInfo.Details.OriginalCatalogNumber = value; break;
                        case "Series_Letter": deviceInfo.Details.SeriesLetter = value; break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(deviceName))
            {
                deviceInfo.Details.DeviceName = deviceName;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", $"Failed to parse HTML: {ex.Message}");
        }

        return deviceInfo;
    }

    private void SaveCommunicationFailure()
    {
        var deviceInfo = new DeviceStatus
        {
            Status = "Not Ready",
            Timestamp = DateTime.Now,
            Details = new DeviceDetails
            {
                DeviceName = deviceName,
                IPAddress = ipAddress,
                Reason = "Communication Failure"
            }
        };
        SaveToJson(deviceInfo);
        UpdateTagVariables(deviceInfo);
    }

    private void SaveToJson(DeviceStatus status)
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res");
            string filePath = Path.Combine(directory, $"{deviceName}_DeviceStatus.json");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string jsonString = JsonSerializer.Serialize(status, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(filePath, jsonString);
        }
        catch (Exception ex)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", $"Failed to save JSON for {deviceName}: {ex.Message}");
        }
    }

    private void InitializeDeviceStatusNode(IUANode tag)
    {
        var deviceStatusNode = tag.GetObject("DeviceStatus");
        if (deviceStatusNode == null)
        {
            deviceStatusNode = InformationModel.MakeObject("DeviceStatus");
            tag.Add(deviceStatusNode);
        }

        var variables = new[]
        {
            ("Status", OpcUa.DataTypes.String),
            ("Timestamp", OpcUa.DataTypes.String),
            ("DeviceName", OpcUa.DataTypes.String),
            ("DeviceLocation", OpcUa.DataTypes.String),
            ("IPAddress", OpcUa.DataTypes.String),
            ("EthernetAddress", OpcUa.DataTypes.String),
            ("HardwareRevision", OpcUa.DataTypes.String),
            ("FirmwareRevision", OpcUa.DataTypes.String),
            ("CatalogNumber", OpcUa.DataTypes.String),
            ("InstalledOptions", OpcUa.DataTypes.String),
            ("OriginalCatalogNumber", OpcUa.DataTypes.String),
            ("SeriesLetter", OpcUa.DataTypes.String),
            ("Reason", OpcUa.DataTypes.String)
        };

        foreach (var (name, dataType) in variables)
        {
            if (deviceStatusNode.GetVariable(name) == null)
            {
                var variable = InformationModel.MakeVariable(name, dataType);
                deviceStatusNode.Add(variable);
            }
        }
    }

    private void InitializeDeviceAlarmsNode(IUANode tag)
    {
        var deviceAlarmsNode = tag.GetObject("DeviceAlarms");
        if (deviceAlarmsNode == null)
        {
            deviceAlarmsNode = InformationModel.MakeObject("DeviceAlarms");
            tag.Add(deviceAlarmsNode);
        }

        var alarmVariables = new[]
        {
            ("Setpoints_1_10_Active", OpcUa.DataTypes.Int32),
            ("Setpoints_11_20_Active", OpcUa.DataTypes.Int32),
            ("Logic_Level_1_Gates_Active", OpcUa.DataTypes.Int32),
            ("Metering_Status", OpcUa.DataTypes.Int32),
            ("Over_Range_Information", OpcUa.DataTypes.Int32),
            ("PowerQuality_Status", OpcUa.DataTypes.Int32),
            ("Logs_Status", OpcUa.DataTypes.Int32),
            ("Output_Pulse_Overrun", OpcUa.DataTypes.Int32),
            ("IEEE1159_Over_Voltage", OpcUa.DataTypes.Int32),
            ("IEEE1159_Under_Voltage", OpcUa.DataTypes.Int32),
            ("IEEE1159_Imbalance_Condition", OpcUa.DataTypes.Int32),
            ("IEEE1159_DCOffset_Condition", OpcUa.DataTypes.Int32),
            ("IEEE1159_Voltage_THD_Condition", OpcUa.DataTypes.Int32),
            ("IEEE1159_Current_THD_Condition", OpcUa.DataTypes.Int32),
            ("IEEE1159_PowerFrequency_Condition", OpcUa.DataTypes.Int32),
            ("IEEE519_Overall_Status", OpcUa.DataTypes.Int32),
            ("ShortTerm_2nd_To_17th_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("ShortTerm_18th_To_33rd_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("ShortTerm_34th_To_40th_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("LongTerm_2nd_To_17th_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("LongTerm_18th_To_33rd_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("LongTerm_34th_To_40th_Harmonic_Status", OpcUa.DataTypes.Int32),
            ("IEEE1159_Voltage_Fluctuation_Condition", OpcUa.DataTypes.Int32),
            ("EN61000_4_30_Mains_Signaling_Condition", OpcUa.DataTypes.Int32),
            ("EN61000_4_30_Under_Deviation_Condition", OpcUa.DataTypes.Int32),
            ("EN61000_4_30_Over_Deviation_Condition", OpcUa.DataTypes.Int32),
            ("OverallAlarmStatus", OpcUa.DataTypes.Boolean)
        };

        foreach (var (name, dataType) in alarmVariables)
        {
            if (deviceAlarmsNode.GetVariable(name) == null)
            {
                var variable = InformationModel.MakeVariable(name, dataType);
                deviceAlarmsNode.Add(variable);
            }
        }
    }

    private void UpdateDeviceAlarmsNode(Dictionary<string, int> alarmValues)
    {
        var tag = Owner;
        var deviceAlarmsNode = tag.GetObject("DeviceAlarms");
        if (deviceAlarmsNode == null)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "DeviceAlarms object not found under Tag");
            return;
        }

        // Update individual alarm values
        foreach (var alarm in alarmValues)
        {
            var variable = deviceAlarmsNode.GetVariable(alarm.Key);
            if (variable != null)
            {
                variable.Value = alarm.Value;
            }
        }

        // Calculate OverallAlarmStatus: true if any alarm value is non-zero, false otherwise
        bool overallAlarmStatus = false;
        foreach (var alarm in alarmValues)
        {
            if (alarm.Value != 0)
            {
                overallAlarmStatus = true;
                break;
            }
        }

        var overallAlarmVar = deviceAlarmsNode.GetVariable("OverallAlarmStatus");
        if (overallAlarmVar != null)
        {
            overallAlarmVar.Value = overallAlarmStatus;
        }
        else
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", "OverallAlarmStatus variable not found in DeviceAlarms node");
        }
    }

    private void UpdateTagVariables(DeviceStatus deviceInfo)
    {
        try
        {
            var Tag = Owner;
            if (Tag == null)
            {
                Log.Error($"DeviceReadinessChecker-{deviceName}", "Tag alias not found in UpdateTagVariables");
                return;
            }

            var deviceStatusNode = Tag.GetObject("DeviceStatus");
            if (deviceStatusNode == null)
            {
                Log.Error($"DeviceReadinessChecker-{deviceName}", "DeviceStatus object not found under Tag");
                return;
            }

            SetVariableValue(deviceStatusNode, "Status", deviceInfo.Status);
            SetVariableValue(deviceStatusNode, "Timestamp", deviceInfo.Timestamp.ToString("o"));

            var details = deviceInfo.Details;
            SetVariableValue(deviceStatusNode, "DeviceName", details.DeviceName);
            SetVariableValue(deviceStatusNode, "DeviceLocation", details.DeviceLocation);
            SetVariableValue(deviceStatusNode, "IPAddress", details.IPAddress);
            SetVariableValue(deviceStatusNode, "EthernetAddress", details.EthernetAddress);
            SetVariableValue(deviceStatusNode, "HardwareRevision", details.HardwareRevision);
            SetVariableValue(deviceStatusNode, "FirmwareRevision", details.FirmwareRevision);
            SetVariableValue(deviceStatusNode, "CatalogNumber", details.CatalogNumber);
            SetVariableValue(deviceStatusNode, "InstalledOptions", details.InstalledOptions);
            SetVariableValue(deviceStatusNode, "OriginalCatalogNumber", details.OriginalCatalogNumber);
            SetVariableValue(deviceStatusNode, "SeriesLetter", details.SeriesLetter);
            SetVariableValue(deviceStatusNode, "Reason", details.Reason);
        }
        catch (Exception ex)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", $"Failed to update DeviceStatus variables: {ex.Message}");
        }
    }

    private void SetVariableValue(IUANode node, string variableName, string value)
    {
        var variable = node.GetVariable(variableName);
        if (variable != null && value != null)
        {
            variable.Value = value;
        }
    }

    [ExportMethod]
    public void ExportDeviceStatus()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res");
            string filePath = Path.Combine(directory, $"{deviceName}_DeviceStatus.json");

            if (File.Exists(filePath))
            {
                string jsonContent = File.ReadAllText(filePath);
            }
            else
            {
                Log.Warning($"DeviceReadinessChecker-{deviceName}", $"No status file found to export for {deviceName}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DeviceReadinessChecker-{deviceName}", $"Export failed for {deviceName}: {ex.Message}");
        }
    }
}

public class DeviceStatus
{
    public string Status { get; set; }
    public DateTime Timestamp { get; set; }
    public DeviceDetails Details { get; set; }
}

public class DeviceDetails
{
    public string DeviceName { get; set; }
    public string DeviceLocation { get; set; }
    public string IPAddress { get; set; }
    public string EthernetAddress { get; set; }
    public string HardwareRevision { get; set; }
    public string FirmwareRevision { get; set; }
    public string CatalogNumber { get; set; }
    public string InstalledOptions { get; set; }
    public string OriginalCatalogNumber { get; set; }
    public string SeriesLetter { get; set; }
    public string Reason { get; set; }
}
