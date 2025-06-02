#region Using directives
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
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SerialPort;
using FTOptix.Core;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class Pass_userinfo : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]

    public void passuserinfo()
    {

        var panel = Owner.GetVariable("panel");
        var pointed_user = Owner.GetVariable("objectPointer");
        var mainWindow = InformationModel.Get(Owner.GetVariable("panel").Value);
        var panel_user_instance = mainWindow.GetVariable("user_instance");
        //var test = panel.GetVariable("Variable1");

        foreach(var row in mainWindow.Get("HorizontalLayout1/User_List1/body/VerticalLayout1").GetNodesByType<user_row>())

        {
            row.GetVariable("isSelected").Value = false;
        }

        
        panel_user_instance.Value = pointed_user.Value;
        Owner.GetVariable("isSelected").Value = true;
        //var test = mainWindow.GetVariable("Variable1");
        
        //test.Value = Owner.BrowseName;

    }
}
