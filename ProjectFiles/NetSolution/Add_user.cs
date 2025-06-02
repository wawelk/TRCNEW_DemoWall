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

public class Add_user : BaseNetLogic
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
    public void CreateUser(string firstName, string lastName, string email, out string status)
    {
        // Get the log label
        var label = Owner.Get<Label>("log");
        status = "";
        
        try
        {
            // Validate inputs
            NodeId panel_id = Owner.GetVariable("PMNotification_AddUserPopup").Value;
            var panel = InformationModel.Get<PMNotification_UserInformation>(panel_id);

            if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName) || string.IsNullOrEmpty(email))
            {
                status = "Please fill in all fields.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                Log.Warning("Add_user.CreateUser", "Empty fields detected");
                return;
            }

            // Validate email format
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                if (addr.Address != email)
                {
                    status = "Invalid email format.";
                    if (label != null)
                    {
                        label.Text = status;
                        label.Visible = true;
                    }
                    Log.Warning("Add_user.CreateUser", $"Invalid email format: {email}");
                    return;
                }
            }
            catch (FormatException ex)
            {
                status = "Invalid email format.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                Log.Warning("Add_user.CreateUser", $"Email validation failed: {ex.Message}");
                return;
            }

            // Find the users folder
            var usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
            if (usersFolder == null)
            {
                status = "Users folder not found.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                Log.Error("Add_user.CreateUser", "Users folder not found in model");
                return;
            }

            // Check for existing user
            try
            {
                var existingUsers = usersFolder.GetNodesByType<PMNotification_User>();
                foreach (var user in existingUsers)
                {
                    if (user.email == email)
                    {
                        status = "User with this email already exists.";
                        if (label != null)
                        {
                            label.Text = status;
                            label.Visible = true;
                        }
                        //Log.Info("Add_user.CreateUser", $"Duplicate email detected: {email}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                status = "Error checking existing users.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                Log.Error("Add_user.CreateUser", $"Failed to check existing users: {ex.Message}");
                return;
            }

            // Create new user
            try
            {
                string userBrowseName = $"{firstName}{lastName}_{Guid.NewGuid().ToString().Substring(0, 8)}";
                var newUser = InformationModel.Make<PMNotification_User>(userBrowseName);

                // Initialize user properties
                newUser.Name = firstName;
                newUser.LastName = lastName;
                newUser.email = email;
                newUser.isActive = false;
                newUser.nameToDisplay = $"{firstName} {lastName}";

                // Add user to folder
                usersFolder.Add(newUser);

                // Create corresponding row
                string rowBrowseName = $"Row_{userBrowseName}";
                var newRow = InformationModel.Make<user_row>(rowBrowseName);
                
                newRow.GetVariable("objectPointer").Value = newUser.NodeId;
                newRow.GetVariable("panel").Value = panel.NodeId;

                var userlist = panel.Get("HorizontalLayout1/User_List1/body/VerticalLayout1");
                userlist.Add(newRow);

                status = "User added successfully.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                //Log.Info("Add_user.CreateUser", $"Successfully created user: {newUser.nameToDisplay} ({email})");
                
                (Owner as Dialog)?.Close();
            }
            catch (Exception ex)
            {
                status = "Failed to create user.";
                if (label != null)
                {
                    label.Text = status;
                    label.Visible = true;
                }
                Log.Error("Add_user.CreateUser", $"User creation failed: {ex.Message}");
                return;
            }
        }
        catch (Exception ex)
        {
            status = "An unexpected error occurred.";
            if (label != null)
            {
                label.Text = status;
                label.Visible = true;
            }
            Log.Error("Add_user.CreateUser", $"Unexpected error: {ex.Message}");
        }
    }
}
