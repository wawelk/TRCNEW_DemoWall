#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.RAEtherNetIP;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.OPCUAServer;
using FTOptix.Retentivity;
using FTOptix.CommunicationDriver;
using FTOptix.NetLogic;
using FTOptix.AuditSigning;
using FTOptix.EventLogger;
using FTOptix.Store;
using FTOptix.Core;
using FTOptix.Alarm;
using FTOptix.UI;
using FTOptix.InfluxDBStoreRemote;
using FTOptix.MicroController;
#endregion

public class raSDK1_NL_AccordionEnableTriggerDelay : BaseNetLogic
{
    private DelayedTask myDelayedTask;

    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        myDelayedTask = new DelayedTask(SetDelayOn, 500, LogicObject);
        myDelayedTask.Start();
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    public void SetDelayOn()
    {
        Owner.Owner.GetVariable("_EnableAutoExpand").Value = true;
    }

}
