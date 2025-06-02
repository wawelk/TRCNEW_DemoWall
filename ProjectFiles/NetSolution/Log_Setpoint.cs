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
using Newtonsoft.Json; // Add Newtonsoft.Json for JSON serialization
using System.IO; // Add System.IO for file operations
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using FTOptix.WebUI;
using System.Threading;
using FTOptix.EventLogger;
using System.Net.Http;
using FTOptix.OPCUAServer;
using FTOptix.SerialPort;
using DeviceLogin;
#endregion

public class Log_Setpoint : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private LongRunningTask downloadTask;
    private Label logLabel;

    public override void Start()
    {
        
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Log_Setpoint", "Tag alias not found");
            return;
        }

        var ipVariable = Tag.GetVariable("Val_IPAddress");
        if (ipVariable == null)
        {
            Log.Error("Log_Setpoint", "Val_IPAddress variable not found in Tag");
            return;
        }

        ipAddress = ipVariable.Value;
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_Setpoint", "IP address is null or empty");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Log_Setpoint", "Device name is null or empty");
            return;
        }

        logLabel = Owner.Get<Label>("log");
        if (logLabel == null)
        {
            Log.Error("Log_Setpoint", "Label 'log' not found");
            return;
        }

        // Initial download on tab open
        StartDownload();
    }

    public override void Stop()
    {
        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }
    }

    [ExportMethod]
    public void DownloadLogFileAsync()
    {
        // Trigger download via button using global IP
        StartDownload();
    }

   private void StartDownload()
    {
        // Safety check for IP address
        if (string.IsNullOrEmpty(ipAddress))
        {
            Log.Error("Log_Setpoint", "IP address not set, cannot start download");
            if (logLabel != null) logLabel.Text = "Failed: IP not set";
            return;
        }

        // If a task exists, dispose it before starting a new one
        if (downloadTask != null)
        {
            downloadTask.Dispose();
            downloadTask = null;
        }

        downloadTask = new LongRunningTask(task => DownloadTaskMethod(task), LogicObject);
        downloadTask.Start();
    }

    private void DownloadTaskMethod(LongRunningTask task)
    {
        string remoteFilePath = "/LoggingResults/Setpoint_Log.csv";
        string url = $"http://{ipAddress}{remoteFilePath}";
        string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
        string localFolder = Path.Combine(projectDir, deviceName);
        string localFilePath = Path.Combine(localFolder, "Setpoint_Log.csv");

        try
        {
            logLabel.Text = "Logging in...";

            // Check and attempt login
            var loginManager = new DeviceLoginManager(ipAddress, Owner.GetAlias("Tag"), deviceName);
            bool isLoggedIn = loginManager.EnsureLoggedIn().Result;
            if (!isLoggedIn)
            {
                logLabel.Text = "Cannot refresh: Login to device failed.";
                Log.Error($"Log_Setpoint-{deviceName}", "Cannot download Data Log due to login failure.");
                return;
            }

            Directory.CreateDirectory(localFolder);

            logLabel.Text = "Downloading...";
            Log.Info("Log_Setpoint", $"Starting HTTP download from {url}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                byte[] fileBytes = client.GetByteArrayAsync(url).Result;

                // Check for cancellation before proceeding
                if (task.IsCancellationRequested)
                {
                    Log.Info("Log_Setpoint", "Download canceled");
                    logLabel.Text = "Canceled";
                    return;
                }

                File.WriteAllBytes(localFilePath, fileBytes);
                Log.Info("Log_Setpoint", $"File downloaded to {localFilePath}");

                if (task.IsCancellationRequested)
                {
                    Log.Info("Log_Setpoint", "Download canceled after file write");
                    logLabel.Text = "Canceled";
                    return;
                }

                if (File.Exists(localFilePath))
                {
                    logLabel.Text = "Processing...";
                    ProcessCsvData();
                    logLabel.Text = "Done";
                }
                else
                {
                    logLabel.Text = "Failed: File not found";
                    Log.Error("Log_Setpoint", $"Downloaded file not found at: {localFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_Setpoint", $"Failed to download log file: {ex.Message}");
            logLabel.Text = $"Failed: {ex.Message}";
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
    

    private static readonly Dictionary<int, Dictionary<string, string>> setpoint_parameter_selection_list = new Dictionary<int, Dictionary<string, string>>
    {
        {1, new Dictionary<string, string> {{"Parameter Tag Name", "V1_N_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {2, new Dictionary<string, string> {{"Parameter Tag Name", "V2_N_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {3, new Dictionary<string, string> {{"Parameter Tag Name", "V3_N_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {4, new Dictionary<string, string> {{"Parameter Tag Name", "VGN_N_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {5, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_V_N_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {6, new Dictionary<string, string> {{"Parameter Tag Name", "V1_V2_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {7, new Dictionary<string, string> {{"Parameter Tag Name", "V2_V3_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {8, new Dictionary<string, string> {{"Parameter Tag Name", "V3_V1_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {9, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_VL_VL_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {10, new Dictionary<string, string> {{"Parameter Tag Name", "I1_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {11, new Dictionary<string, string> {{"Parameter Tag Name", "I2_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {12, new Dictionary<string, string> {{"Parameter Tag Name", "I3_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {13, new Dictionary<string, string> {{"Parameter Tag Name", "I4_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {14, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {15, new Dictionary<string, string> {{"Parameter Tag Name", "Frequency_Hz"}, {"Units", "Hz"}, {"Range", "40.00…70.00"}}},
        {16, new Dictionary<string, string> {{"Parameter Tag Name", "L1_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {17, new Dictionary<string, string> {{"Parameter Tag Name", "L2_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {18, new Dictionary<string, string> {{"Parameter Tag Name", "L3_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {19, new Dictionary<string, string> {{"Parameter Tag Name", "Total_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {20, new Dictionary<string, string> {{"Parameter Tag Name", "L1_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {21, new Dictionary<string, string> {{"Parameter Tag Name", "L2_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {22, new Dictionary<string, string> {{"Parameter Tag Name", "L3_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {23, new Dictionary<string, string> {{"Parameter Tag Name", "Total_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {24, new Dictionary<string, string> {{"Parameter Tag Name", "L1_kVA"}, {"Units", "kVA"}, {"Range", "0…9.999E15"}}},
        {25, new Dictionary<string, string> {{"Parameter Tag Name", "L2_kVA"}, {"Units", "kVA"}, {"Range", "0…9.999E15"}}},
        {26, new Dictionary<string, string> {{"Parameter Tag Name", "L3_kVA"}, {"Units", "kVA"}, {"Range", "0…9.999E15"}}},
        {27, new Dictionary<string, string> {{"Parameter Tag Name", "Total_kVA"}, {"Units", "kVA"}, {"Range", "0…9.999E15"}}},
        {28, new Dictionary<string, string> {{"Parameter Tag Name", "L1_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {29, new Dictionary<string, string> {{"Parameter Tag Name", "L2_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {30, new Dictionary<string, string> {{"Parameter Tag Name", "L3_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {31, new Dictionary<string, string> {{"Parameter Tag Name", "Total_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {32, new Dictionary<string, string> {{"Parameter Tag Name", "L1_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {33, new Dictionary<string, string> {{"Parameter Tag Name", "L2_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {34, new Dictionary<string, string> {{"Parameter Tag Name", "L3_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {35, new Dictionary<string, string> {{"Parameter Tag Name", "Total_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {36, new Dictionary<string, string> {{"Parameter Tag Name", "L1_PF_Lead_Lag_Indicator"}, {"Units", "-"}, {"Range", "-1 or 1"}}},
        {37, new Dictionary<string, string> {{"Parameter Tag Name", "L2_PF_Lead_Lag_Indicator"}, {"Units", "-"}, {"Range", "-1 or 1"}}},
        {38, new Dictionary<string, string> {{"Parameter Tag Name", "L3_PF_Lead_Lag_Indicator"}, {"Units", "-"}, {"Range", "-1 or 1"}}},
        {39, new Dictionary<string, string> {{"Parameter Tag Name", "Total_PF_Lead_Lag_Indicator"}, {"Units", "-"}, {"Range", "-1 or 1"}}},
        {40, new Dictionary<string, string> {{"Parameter Tag Name", "V1_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {41, new Dictionary<string, string> {{"Parameter Tag Name", "V2_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {42, new Dictionary<string, string> {{"Parameter Tag Name", "V3_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {43, new Dictionary<string, string> {{"Parameter Tag Name", "V1_V2_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {44, new Dictionary<string, string> {{"Parameter Tag Name", "V2_V3_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {45, new Dictionary<string, string> {{"Parameter Tag Name", "V3_V1_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {46, new Dictionary<string, string> {{"Parameter Tag Name", "I1_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {47, new Dictionary<string, string> {{"Parameter Tag Name", "I2_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {48, new Dictionary<string, string> {{"Parameter Tag Name", "I3_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {49, new Dictionary<string, string> {{"Parameter Tag Name", "I4_Crest_Factor"}, {"Units", "-"}, {"Range", "0…9.999E15"}}},
        {50, new Dictionary<string, string> {{"Parameter Tag Name", "V1_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {51, new Dictionary<string, string> {{"Parameter Tag Name", "V2_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {52, new Dictionary<string, string> {{"Parameter Tag Name", "V3_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {53, new Dictionary<string, string> {{"Parameter Tag Name", "VN_G_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {54, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEEE_THD_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {55, new Dictionary<string, string> {{"Parameter Tag Name", "V1_V2_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {56, new Dictionary<string, string> {{"Parameter Tag Name", "V2_V3_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {57, new Dictionary<string, string> {{"Parameter Tag Name", "V3_V1_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {58, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEEE_THD_V_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {59, new Dictionary<string, string> {{"Parameter Tag Name", "I1_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {60, new Dictionary<string, string> {{"Parameter Tag Name", "I2_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {61, new Dictionary<string, string> {{"Parameter Tag Name", "I3_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {62, new Dictionary<string, string> {{"Parameter Tag Name", "I4_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {63, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEEE_THD_I_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {64, new Dictionary<string, string> {{"Parameter Tag Name", "V1_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {65, new Dictionary<string, string> {{"Parameter Tag Name", "V2_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {66, new Dictionary<string, string> {{"Parameter Tag Name", "V3_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {67, new Dictionary<string, string> {{"Parameter Tag Name", "VN_G_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {68, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEC_THD_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {69, new Dictionary<string, string> {{"Parameter Tag Name", "V1_V2_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {70, new Dictionary<string, string> {{"Parameter Tag Name", "V2_V3_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {71, new Dictionary<string, string> {{"Parameter Tag Name", "V3_V1_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {72, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEC_THD_V_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {73, new Dictionary<string, string> {{"Parameter Tag Name", "I1_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {74, new Dictionary<string, string> {{"Parameter Tag Name", "I2_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {75, new Dictionary<string, string> {{"Parameter Tag Name", "I3_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {76, new Dictionary<string, string> {{"Parameter Tag Name", "I4_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {77, new Dictionary<string, string> {{"Parameter Tag Name", "Avg_IEC_THD_I_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {78, new Dictionary<string, string> {{"Parameter Tag Name", "I1_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {79, new Dictionary<string, string> {{"Parameter Tag Name", "I2_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {80, new Dictionary<string, string> {{"Parameter Tag Name", "I3_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {81, new Dictionary<string, string> {{"Parameter Tag Name", "Pos_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {82, new Dictionary<string, string> {{"Parameter Tag Name", "Neg_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {83, new Dictionary<string, string> {{"Parameter Tag Name", "Zero_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {84, new Dictionary<string, string> {{"Parameter Tag Name", "Pos_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {85, new Dictionary<string, string> {{"Parameter Tag Name", "Neg_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {86, new Dictionary<string, string> {{"Parameter Tag Name", "Zero_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {87, new Dictionary<string, string> {{"Parameter Tag Name", "Voltage_Unbalance_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {88, new Dictionary<string, string> {{"Parameter Tag Name", "Current_Unbalance_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {89, new Dictionary<string, string> {{"Parameter Tag Name", "kW Demand"}, {"Units", "kW"}, {"Range", "±0.000…9,999,999"}}},
        {90, new Dictionary<string, string> {{"Parameter Tag Name", "kVAR Demand"}, {"Units", "kVAR"}, {"Range", "±0.000…9,999,999"}}},
        {91, new Dictionary<string, string> {{"Parameter Tag Name", "kVA Demand"}, {"Units", "kVA"}, {"Range", "0.000…9,999,999"}}},
        {92, new Dictionary<string, string> {{"Parameter Tag Name", "Demand PF"}, {"Units", "%"}, {"Range", "-100.0…+100.0"}}},
        {93, new Dictionary<string, string> {{"Parameter Tag Name", "Demand Amps"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {94, new Dictionary<string, string> {{"Parameter Tag Name", "Projected_kW_Demand"}, {"Units", "kW"}, {"Range", "-9,999,999…9,999,999"}}},
        {95, new Dictionary<string, string> {{"Parameter Tag Name", "Projected_kVAR_Demand"}, {"Units", "kVAR"}, {"Range", "-9,999,999…9,999,999"}}},
        {96, new Dictionary<string, string> {{"Parameter Tag Name", "Projected_kVA_Demand"}, {"Units", "kVA"}, {"Range", "0.000…9,999,999"}}},
        {97, new Dictionary<string, string> {{"Parameter Tag Name", "Projected_Ampere_Demand"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {98, new Dictionary<string, string> {{"Parameter Tag Name", "Status_Input_1_Actuated"}, {"Units", ""}, {"Range", "0 or 1"}}},
        {99, new Dictionary<string, string> {{"Parameter Tag Name", "Status_Input_2_Actuated"}, {"Units", ""}, {"Range", "0 or 1"}}},
        {100, new Dictionary<string, string> {{"Parameter Tag Name", "Status_Input_3_Actuated"}, {"Units", ""}, {"Range", "0 or 1"}}},
            {101, new Dictionary<string, string> {{"Parameter Tag Name", "Status_Input_4_Actuated"}, {"Units", ""}, {"Range", "0 or 1"}}},
        {102, new Dictionary<string, string> {{"Parameter Tag Name", "Log_Status"}, {"Units", ""}, {"Range", "See Status.Alarms table"}}},
        {103, new Dictionary<string, string> {{"Parameter Tag Name", "PowerQuality_Status"}, {"Units", ""}, {"Range", "See Status.Alarms table"}}},
        {104, new Dictionary<string, string> {{"Parameter Tag Name", "Over_Range_Information"}, {"Units", ""}, {"Range", "See Status.Alarms table"}}},
        {105, new Dictionary<string, string> {{"Parameter Tag Name", "Metering_Status"}, {"Units", ""}, {"Range", "See Status.Alarms table"}}},
        {106, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_N_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {107, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_N_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {108, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_N_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {109, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_G_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {110, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_Ave_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {111, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_V2_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {112, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_V3_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {113, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_V1_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {114, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VV_Ave_Magnitude"}, {"Units", "V"}, {"Range", "0.000…9,999,999"}}},
        {115, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I1_Amps_Magnitude"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {116, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I2_Amps_Magnitude"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {117, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I3_Amps_Magnitude"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {118, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I4_Amps_Magnitude"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {119, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Amps_Ave_Magnitude"}, {"Units", "A"}, {"Range", "0.000…9,999,999"}}},
        {120, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L1_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {121, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L2_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {122, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L3_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {123, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Total_kW"}, {"Units", "kW"}, {"Range", "-9.999E15…9.999E15"}}},
        {124, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L1_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {125, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L2_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {126, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L3_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {127, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Total_kVAR"}, {"Units", "kVAR"}, {"Range", "-9.999E15…9.999E15"}}},
        {128, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L1_kVA"}, {"Units", "kVA"}, {"Range", "0.000…9.999E15"}}},
        {129, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L2_kVA"}, {"Units", "kVA"}, {"Range", "0.000…9.999E15"}}},
        {130, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L3_kVA"}, {"Units", "kVA"}, {"Range", "0.000…9.999E15"}}},
        {131, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Total_kVA"}, {"Units", "kVA"}, {"Range", "0.000…9.999E15"}}},
        {132, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L1_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {133, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L2_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {134, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L3_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {135, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Total_True_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {136, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L1_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {137, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L2_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {138, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_L3_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {139, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Total_Disp_PF"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {140, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_N_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {141, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_N_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {142, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_N_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {143, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_G_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {144, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEEE_THD_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {145, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_V2_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {146, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_V3_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {147, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_V1_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {148, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEEE_THD_V_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {149, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I1_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {150, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I2_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
            {151, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I3_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {152, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I4_IEEE_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {153, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEEE_THD_I_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {154, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_N_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {155, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_N_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {156, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_N_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {157, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_G_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {158, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEC_THD_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {159, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_V2_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {160, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_V3_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {161, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_V1_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {162, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEC_THD_V_V_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {163, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I1_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {164, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I2_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {165, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I3_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {166, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I4_IEC_THD_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {167, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Avg_IEC_THD_I_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {168, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_N_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {169, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_N_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {170, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_N_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {171, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_G_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {172, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_AVE_VN_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {173, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_V2_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {174, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_V3_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {175, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_V1_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {176, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_AVE_LL_THDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {177, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_N_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {178, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_N_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {179, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_N_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {180, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_VN_G_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {181, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_AVE_VN_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {182, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V1_V2_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {183, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V2_V3_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {184, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_V3_V1_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {185, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_AVE_LL_TIHDS"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {186, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I1_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {187, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I2_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {188, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_I3_K_Factor"}, {"Units", "-"}, {"Range", "1.00…25000.00"}}},
        {189, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Pos_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {190, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Neg_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {191, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Zero_Seq_Volts"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {192, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Pos_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {193, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Neg_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {194, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Zero_Seq_Amps"}, {"Units", "A"}, {"Range", "0…9.999E15"}}},
        {195, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Voltage_Unbalance_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {196, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_Current_Unbalance_%"}, {"Units", "%"}, {"Range", "0.00…100.00"}}},
        {197, new Dictionary<string, string> {{"Parameter Tag Name", "10s_Power_Frequency"}, {"Units", "Hz"}, {"Range", "40.00…70.00"}}},
        {198, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V1_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {199, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V1_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {200, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V1_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {201, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V2_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {202, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V2_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {203, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V2_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {204, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V3_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {205, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V3_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {206, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V3_N_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {207, new Dictionary<string, string> {{"Parameter Tag Name", "3s_VN_G_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {208, new Dictionary<string, string> {{"Parameter Tag Name", "10m_VN_G_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {209, new Dictionary<string, string> {{"Parameter Tag Name", "2h_VN_G_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {210, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V1_V2_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {211, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V1_V2_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {212, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V1_V2_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {213, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V2_V3_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {214, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V2_V3_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {215, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V2_V3_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {216, new Dictionary<string, string> {{"Parameter Tag Name", "3s_V3_V1_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {217, new Dictionary<string, string> {{"Parameter Tag Name", "10m_V3_V1_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {218, new Dictionary<string, string> {{"Parameter Tag Name", "2h_V3_V1_Magnitude"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {219, new Dictionary<string, string> {{"Parameter Tag Name", "CH1_Short_Term_Flicker_Pst"}, {"Units", "Pst"}, {"Range", "0.0…100.00"}}},
        {220, new Dictionary<string, string> {{"Parameter Tag Name", "CH1_Long_Term_Flicker_Plt"}, {"Units", "Plt"}, {"Range", "0.0…100.00"}}},
        {221, new Dictionary<string, string> {{"Parameter Tag Name", "CH2_Short_Term_Flicker_Pst"}, {"Units", "Pst"}, {"Range", "0.0…100.00"}}},
        {222, new Dictionary<string, string> {{"Parameter Tag Name", "CH2_Long_Term_Flicker_Plt"}, {"Units", "Plt"}, {"Range", "0.0…100.00"}}},
        {223, new Dictionary<string, string> {{"Parameter Tag Name", "CH3_Short_Term_Flicker_Pst"}, {"Units", "Pst"}, {"Range", "0.0…100.00"}}},
        {224, new Dictionary<string, string> {{"Parameter Tag Name", "CH3_Long_Term_Flicker_Plt"}, {"Units", "Plt"}, {"Range", "0.0…100.00"}}},
        {225, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_CH1_Mains_Signaling_Voltage"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {226, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_CH2_Mains_Signaling_Voltage"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {227, new Dictionary<string, string> {{"Parameter Tag Name", "200mS_CH3_Mains_Signaling_Voltage"}, {"Units", "V"}, {"Range", "0…9.999E15"}}},
        {228, new Dictionary<string, string> {{"Parameter Tag Name", "3s_Voltage_Unbalance"}, {"Units", "%"}, {"Range", "0.0…100.00"}}},
        {229, new Dictionary<string, string> {{"Parameter Tag Name", "10m_Voltage_Unbalance"}, {"Units", "%"}, {"Range", "0.0…100.00"}}},
        {230, new Dictionary<string, string> {{"Parameter Tag Name", "2h_Voltage_Unbalance"}, {"Units", "%"}, {"Range", "0.0…100.00"}}}
    };

    private Dictionary<int, string> CreateTestConditionLookup()
    {
        return new Dictionary<int, string>
        {
            {0, "Disabled"},
            {1, "Less Than"},
            {2, "Greater Than"},
            {3, "Equals"}
        };
    }

    private Dictionary<int, string> CreateEvaluationTypeLookup()
    {
        return new Dictionary<int, string>
        {
            {0, "Magnitude"},
            {1, "State"},
            {2, "Percent of Reference"},
            {3, "Percent of Sliding Reference"}
        };
    }

    private Dictionary<int, string> CreateOutputActionLookup()
    {
        return new Dictionary<int, string>
        {
            {0, "None"},
            {1, "Energize Relay 1"},
            {2, "Energize Relay 2"},
            {3, "Energize Relay 3"},
            {4, "Energize KYZ"},
            {5, "Clear kWh result"},
            {6, "Clear kVARh result"},
            {7, "Clear kVAh result"},
            {8, "Clear Ah result"},
            {9, "Clear all energy results"},
            {10, "Clear setpoint #1 time accumulator and transition count"},
            {11, "Clear setpoint #2 time accumulator and transition count"},
            {12, "Clear setpoint #3 time accumulator and transition count"},
            {13, "Clear setpoint #4 time accumulator and transition count"},
            {14, "Clear setpoint #5 time accumulator and transition count"},
            {15, "Clear setpoint #6 time accumulator and transition count"},
            {16, "Clear setpoint #7 time accumulator and transition count"},
            {17, "Clear setpoint #8 time accumulator and transition count"},
            {18, "Clear setpoint #9 time accumulator and transition count"},
            {19, "Clear setpoint #10 time accumulator and transition count"},
            {20, "Clear setpoint #11 time accumulator and transition count"},
            {21, "Clear setpoint #12 time accumulator and transition count"},
            {22, "Clear setpoint #13 time accumulator and transition count"},
            {23, "Clear setpoint #14 time accumulator and transition count"},
            {24, "Clear setpoint #15 time accumulator and transition count"},
            {25, "Clear setpoint #16 time accumulator and transition count"},
            {26, "Clear setpoint #17 time accumulator and transition count"},
            {27, "Clear setpoint #18 time accumulator and transition count"},
            {28, "Clear setpoint #19 time accumulator and transition count"},
            {29, "Clear setpoint #20 time accumulator and transition count"},
            {30, "Start Trigger Data logging"},
            {31, "Trigger Waveform Capture"}
        };
    }
    public void ProcessCsvData()
    {
        try
        {
            var filePathValue = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string csvFilePath = Path.Combine(baseDirectory, deviceName, "Setpoint_Log.csv");

            if (!File.Exists(csvFilePath))
            {
                Log.Error("CSV file not found at: " + csvFilePath);
                return;
            }

            Log.Info("Processing CSV from: " + csvFilePath);

            var dataList = ProcessCsvFile(csvFilePath);
            UpdateDashboard(baseDirectory, dataList);
        }
        catch (Exception ex)
        {
            Log.Error($"Error in ProcessCsvData: {ex.Message}");
        }
    }

    private List<Dictionary<string, object>> ProcessCsvFile(string csvFilePath)
    {
        var lines = File.ReadAllLines(csvFilePath);
        var headers = lines[0].Split(',');
        
        // Create lookup dictionaries
        var parameterLookup = CreateParameterLookup();
        var testConditionLookup = CreateTestConditionLookup();
        var evaluationTypeLookup = CreateEvaluationTypeLookup();
        var outputActionLookup = CreateOutputActionLookup();
        
        return lines.Skip(1)
            .Select(line =>
            {
                var values = line.Split(',');
                var row = new Dictionary<string, object>();
                
                // Find timestamp column indices
                int yearIndex = Array.IndexOf(headers, "Timestamp_Year");
                int monthDayIndex = Array.IndexOf(headers, "Timestamp_Mth_Day");
                int hourMinIndex = Array.IndexOf(headers, "Timestamp_Hr_Min");
                int secMsIndex = Array.IndexOf(headers, "Timestamp_Sec_ms");
                
                // Find lookup column indices
                int inputParamIndex = Array.IndexOf(headers, "Input_Parameter");
                int testCondIndex = Array.IndexOf(headers, "Test_Condition");
                int evalTypeIndex = Array.IndexOf(headers, "Evaluation_Type");
                int outputActionIndex = Array.IndexOf(headers, "Output_Action");
                int setpointStatusIndex = Array.IndexOf(headers, "Setpoint_Status");

                // Add formatted timestamp if all required columns exist
                if (yearIndex >= 0 && monthDayIndex >= 0 && 
                    hourMinIndex >= 0 && secMsIndex >= 0)
                {
                    string formattedTimestamp = FormatTimestamp(
                        values[yearIndex],
                        values[monthDayIndex],
                        values[hourMinIndex],
                        values[secMsIndex]
                    );
                    row["Timestamp"] = formattedTimestamp;
                }

                // Process all columns
                for (int i = 0; i < headers.Length; i++)
                {
                    // Skip timestamp component columns
                    if (i == yearIndex || i == monthDayIndex || 
                        i == hourMinIndex || i == secMsIndex)
                        continue;

                    if (i < values.Length)
                    {
                        string value = values[i];
                        string header = headers[i];
                        
                        // Apply lookups based on column
                        if (i == inputParamIndex && int.TryParse(value, out int paramId))
                        {
                            var paramInfo = parameterLookup.GetValueOrDefault(paramId, new { Parameter_Tag_Name = "Unknown", Units = "" });
                            row[header] = $"{paramInfo.Parameter_Tag_Name} ({paramInfo.Units})";
                        }
                        else if (i == testCondIndex && int.TryParse(value, out int testCondId))
                        {
                            row[header] = testConditionLookup.GetValueOrDefault(testCondId, "Unknown");
                        }
                        else if (i == evalTypeIndex && int.TryParse(value, out int evalTypeId))
                        {
                            row[header] = evaluationTypeLookup.GetValueOrDefault(evalTypeId, "Unknown");
                        }
                        else if (i == outputActionIndex && int.TryParse(value, out int actionId))
                        {
                            row[header] = outputActionLookup.GetValueOrDefault(actionId, "Unknown");
                        }
                        else if (i == setpointStatusIndex && int.TryParse(value, out int statusValue))
                        {
                            // Add Setpoint Status lookup
                            row[header] = statusValue == 1 ? "Active" : "Not Active";
                        }
                        else if (int.TryParse(value, out int intValue))
                        {
                            row[header] = intValue;
                        }
                        else
                        {
                            row[header] = value;
                        }
                    }
                }
                
                return row;
            })
            .ToList();
    }

    private Dictionary<int, dynamic> CreateParameterLookup()
    {
        // Convert the Python-style dictionary to C# dictionary
        var lookup = new Dictionary<int, dynamic>();
        foreach (var entry in setpoint_parameter_selection_list)
        {
            lookup[entry.Key] = new
            {
                Parameter_Tag_Name = entry.Value["Parameter Tag Name"],
                Units = entry.Value["Units"]
            };
        }
        return lookup;
    }



    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            int monthDayInt = int.Parse(monthDay);
            string month = (monthDayInt / 100).ToString().PadLeft(2, '0');
            string day = (monthDayInt % 100).ToString().PadLeft(2, '0');

            int hourMinInt = int.Parse(hourMinute);
            string hour = (hourMinInt / 100).ToString().PadLeft(2, '0');
            string minute = (hourMinInt % 100).ToString().PadLeft(2, '0');

            int secMsInt = int.Parse(secMilliSec);
            string seconds = (secMsInt / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (secMsInt % 1000).ToString().PadLeft(3, '0');

            return $"{year}-{month}-{day} {hour}:{minute}:{seconds}.{milliseconds}";
        }
        catch (Exception ex)
        {
            Log.Error($"Error formatting timestamp: {ex.Message}");
            return "Invalid Timestamp";
        }
    }

    
    private void UpdateDashboard(string baseDirectory, List<Dictionary<string, object>> dataList)
    {
        string jsonData = JsonConvert.SerializeObject(dataList, new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.EscapeHtml
        });

        string htmlTemplatePath = Path.Combine(baseDirectory, "res", "Template_SetpointLog.html");
        string deviceHtmlPath = UpdateHtmlTemplate(htmlTemplatePath, jsonData); // Returns the path to the updated file

        var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        if (webBrowser != null)
        {
            // string webBrowserUrl = $"file:///{deviceHtmlPath.Replace('\\', '/')}";
            // webBrowser.URL = webBrowserUrl;
            // webBrowser.Refresh();

            string relativePath = $"res/Template_SetpointLog_{deviceName}.html";
            var templatePath = ResourceUri.FromProjectRelativePath(relativePath);
            webBrowser.URL = templatePath;

            Log.Info($"WebBrowser URL set to: {relativePath}");
        }
        else
        {
            Log.Error("WebBrowser control not found in the UI");
        }
    }

    private string UpdateHtmlTemplate(string templatePath, string jsonData)
    {
        // Define the output file path in the same res folder
        string resDirectory = Path.Combine(Path.GetDirectoryName(templatePath)); // Gets res folder
        string templateName = Path.GetFileNameWithoutExtension(templatePath); // e.g., "Setpoint_Log"
        string outputFileName = $"{templateName}_{deviceName}.html"; // e.g., "Setpoint_Log_Device1.html"
        string deviceHtmlPath = Path.Combine(resDirectory, outputFileName);

        try
        {
            if (!File.Exists(templatePath))
            {
                Log.Error($"Template file not found at: {templatePath}");
                return null;
            }

            string htmlContent = File.ReadAllText(templatePath);
            string pattern = @"var\s+tableData\s*=\s*\[[^\]]*\];";
            string replacement = $"var tableData = {jsonData};";

            string updatedContent = System.Text.RegularExpressions.Regex.Replace(
                htmlContent,
                pattern,
                replacement
            );

            if (htmlContent.Equals(updatedContent))
            {
                Log.Warning("No changes made to HTML content. Check if pattern exists in template.");
            }

            File.WriteAllText(deviceHtmlPath, updatedContent);
            Log.Info($"{outputFileName} updated successfully at: {deviceHtmlPath}");

            return deviceHtmlPath; // Return the path to the new file
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to update HTML template: {ex.Message}");
            return null;
        }
    }

}




