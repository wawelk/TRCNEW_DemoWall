#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.SerialPort;
using FTOptix.Core;
using System.IO;
using System.Linq;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class raC_4_00_raC_Dvc_PM5000_PQEM_Setup : BaseNetLogic
{
    [ExportMethod]
    public void SetupInitialConfiguration()
    {
        try
        {
            var usersFolder = Owner.Get("raC_4_00_raC_Dvc_PM5000_PQEM_Users");
            if (usersFolder == null)
            {
                Log.Error("raC_4_00_raC_Dvc_PM5000_PQEM_Model", "Failed to retrieve users folder: raC_4_00_raC_Dvc_PM5000_PQEM_Users");
                return;
            }

            // Get the RetentivityStorage nodes
            var rsNodes = Owner.Get<RetentivityStorage>("raC_4_00_raC_Dvc_PM5000_PQEM_DataStores/raC_4_00_raC_Dvc_PM5000_PQEM_rs_Users")?.Nodes;
            if (rsNodes == null)
            {
                Log.Error("raC_4_00_raC_Dvc_PM5000_PQEM_Model", "Failed to retrieve RetentivityStorage nodes: raC_4_00_raC_Dvc_PM5000_PQEM_rs_Users");
                return;
            }
            var usersNode = rsNodes.Get("users");
            if (usersNode == null)
            {
                Log.Error("raC_4_00_raC_Dvc_PM5000_PQEM_Model", "Failed to retrieve 'users' node from RetentivityStorage");
                return;
            }
            usersNode.Value = usersFolder.NodeId;
            Log.Info("raC_4_00_raC_Dvc_PM5000_PQEM_Model", $"Successfully completed initial setup");
        }
        catch (Exception ex)
        {
            Log.Error("raC_4_00_raC_Dvc_PM5000_PQEM_Model", $"Error in SetupInitialConfiguration: {ex.Message}");
        }
    }
}
