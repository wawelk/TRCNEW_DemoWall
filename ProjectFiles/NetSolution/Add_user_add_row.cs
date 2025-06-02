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
using FTOptix.Core;
using FTOptix.SerialPort;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class Add_user_add_row : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        //string teesss = CreateUser("user","1","user1@test.com",out teesss);

        // Find the users folder for PMNotification_User instances
       

        LoadExistingUsers();
        
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    
    

    [ExportMethod]
    public void CreateUser(string firstName, string lastName, string email, out string status)
    {
        // Validate inputs
        if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName) || string.IsNullOrEmpty(email))
        {
            status = "Please fill in all fields.";
            return;
        }

        // Validate email format
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            if (addr.Address != email)
            {
                status = "Invalid email format.";
                return;
            }
        }
        catch
        {
            status = "Invalid email format.";
            return;
        }

        // Find the users folder for PMNotification_User instances
        var usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
        
        if (usersFolder == null)
        {
            status = "Users folder not found.";
            Log.Error("Add_user_add_row.CreateUser", "NotificationUsers folder not found.");
            return;
        }

        // Check if user with this email already exists
        var existingUsers = usersFolder.GetNodesByType<PMNotification_User>();
        foreach (var user in existingUsers)
        {
            if (user.email == email)
            {
                status = "User with this email already exists.";
                //Log.Info("Add_user_add_row.CreateUser", $"Attempt to add duplicate user with email '{email}' rejected.");
                return;
            }
        }

        // Create a unique browse name for the new user
        string userBrowseName = $"{firstName}{lastName}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        var newUser = InformationModel.Make<PMNotification_User>(userBrowseName);

        // Initialize user properties
        newUser.Name = firstName;
        newUser.LastName = lastName;
        newUser.email = email;
        newUser.isActive = false; // Default to inactive
        newUser.nameToDisplay = $"{firstName} {lastName}";

        // Add the user to the users folder
        usersFolder.Add(newUser);

        // Create a corresponding PMNotification_ObjectListRow instance
        string rowBrowseName = $"Row_{userBrowseName}";
        var newRow = InformationModel.Make<user_row>(rowBrowseName);
        //newRow.DisplayName = newUser.nameToDisplay;

        newRow.GetVariable("objectPointer").Value = newUser.NodeId; // Set the objectPointer to the NodeId of the new user
        newRow.GetVariable("panel").Value = Owner.NodeId; // Pass main panel node ID to each row for further processing

        // Add the row to the user list folder
        var userlist = Owner.Get("HorizontalLayout1/User_List1/body/VerticalLayout1");
        userlist.Add(newRow);
        
        // Set the status and log the success
        status = "User added successfully.";
        //Log.Info("Add_user_add_row.CreateUser", $"User '{newUser.nameToDisplay}' with email '{email}' added successfully.");
    }


    [ExportMethod]
    public void LoadExistingUsers()
    {
        // Find the users folder containing PMNotification_User instances
        var usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
        
        if (usersFolder == null)
        {
            Log.Error("Add_user_add_row.LoadExistingUsers", "NotificationUsers folder not found.");
            return;
        }

        // Get the user list panel where rows will be added
        var userList = Owner.Get("HorizontalLayout1/User_List1/body/VerticalLayout1");
        if (userList == null)
        {
            Log.Error("Add_user_add_row.LoadExistingUsers", "User list panel not found.");
            return;
        }

        // Get all existing users
        var existingUsers = usersFolder.GetNodesByType<PMNotification_User>();
        
        // Counter for successfully loaded users
        int loadedCount = 0;

        // Iterate through each existing user and create corresponding rows
        foreach (var user in existingUsers)
        {
            // Create a unique browse name for the row using user's browse name
            string rowBrowseName = $"Row_{user.BrowseName}";
            
            // Create new row instance
            var newRow = InformationModel.Make<user_row>(rowBrowseName);
            
            // Set row properties
            newRow.GetVariable("objectPointer").Value = user.NodeId; // Link to the user object
            newRow.GetVariable("panel").Value = Owner.NodeId; // Set panel reference
            
            // Optionally set display name if needed
            // newRow.DisplayName = user.nameToDisplay;

            // Add the row to the user list panel
            userList.Add(newRow);

            if (loadedCount == 0)
            {
                newRow.GetVariable("isSelected").Value = true;
                Owner.GetVariable("user_instance").Value = user.NodeId;
            }
            
            loadedCount++;
        }

        // Log the result
        if (loadedCount > 0)
        {
            Log.Info("Add_user_add_row.LoadExistingUsers", $"Successfully loaded {loadedCount} existing users into the panel.");
        }
        else
        {
            Log.Info("Add_user_add_row.LoadExistingUsers", "No existing users found to load into the panel.");
        }
    }


    [ExportMethod]
    public void PMNotification_RemoveAllUsers(out string status)
    {
        // Initialize status
        status = "Unknown error occurred.";

        // Find the users folder
        var usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
        if (usersFolder == null)
        {
            status = "Users folder not found.";
            Log.Error("Add_user_add_row.PMNotification_RemoveAllUsers", "NotificationUsers folder not found.");
            return;
        }

        // Get the user list panel
        var userList = Owner.Get("HorizontalLayout1/User_List1/body/VerticalLayout1");
        if (userList == null)
        {
            status = "User list panel not found.";
            Log.Error("Add_user_add_row.PMNotification_RemoveAllUsers", "User list panel not found.");
            return;
        }

        // Get all rows from the user list
        var allRows = userList.GetNodesByType<user_row>();
        if (allRows == null)
        {
            status = "No users found to remove.";
           // Log.Info("Add_user_add_row.PMNotification_RemoveAllUsers", "No user rows found in the panel.");
            return;
        }

        // Counter for removed users
        int removedCount = 0;

        // Iterate through all rows and remove both row and corresponding user
        foreach (var row in allRows)
        {
            // Get the user NodeId from the row's objectPointer
            NodeId userNodeId = row.GetVariable("objectPointer").Value;
            
            if (userNodeId != null && !userNodeId.IsEmpty)
            {
                // Get the user object using the NodeId
                var user = InformationModel.Get<PMNotification_User>(userNodeId);
                
                if (user != null)
                {
                    // Remove the user from the users folder
                    usersFolder.Remove(user);
                    removedCount++;
                    
                    // Log individual removal
                    // Log.Info("Add_user_add_row.PMNotification_RemoveAllUsers",
                    //     $"Removed user '{user.nameToDisplay}' with NodeId {userNodeId}");
                }
            }
            
            // Remove the row from the user list
            userList.Remove(row);
        }

        // Set final status and log result
        status = $"Successfully removed {removedCount} users.";
        //Log.Info("Add_user_add_row.PMNotification_RemoveAllUsers", $"Completed removal process: {removedCount} users and their rows removed.");
    }

    [ExportMethod]
    public void PMNotification_RemoveSelectedUsers(out string status)
    {
        // Initialize status
        status = "Unknown error occurred.";

        // Find the users folder
        var usersFolder = Project.Current.Get("Model/raC_4_00_raC_Dvc_PM5000_PQEM_Model/raC_4_00_raC_Dvc_PM5000_PQEM_Users");
        if (usersFolder == null)
        {
            status = "Users folder not found.";
            Log.Error("Add_user_add_row.PMNotification_RemoveSelectedUsers", "NotificationUsers folder not found.");
            return;
        }

        // Get the user list panel
        var userList = Owner.Get("HorizontalLayout1/User_List1/body/VerticalLayout1");
        if (userList == null)
        {
            status = "User list panel not found.";
            Log.Error("Add_user_add_row.PMNotification_RemoveSelectedUsers", "User list panel not found.");
            return;
        }

        // Get all rows from the user list
        var allRows = userList.GetNodesByType<user_row>();
        if (allRows == null)
        {
            status = "No users found to remove.";
            //Log.Info("Add_user_add_row.PMNotification_RemoveSelectedUsers", "No user rows found in the panel.");
            return;
        }

        // Counter for removed users
        int removedCount = 0;

        // Iterate through all rows and remove only selected rows and corresponding users
        foreach (var row in allRows)
        {
            // Check if the row is selected
            var isSelected = row.GetVariable("isSelected").Value;
            if (isSelected != true)
            {
                continue; // Skip non-selected rows
            }

            // Get the user NodeId from the row's objectPointer
            NodeId userNodeId = row.GetVariable("objectPointer").Value;
            
            if (userNodeId != null && !userNodeId.IsEmpty)
            {
                // Get the user object using the NodeId
                var user = InformationModel.Get<PMNotification_User>(userNodeId);
                
                if (user != null)
                {
                    // Remove the user from the users folder
                    usersFolder.Remove(user);
                    removedCount++;
                    
                    // Log individual removal
                    // Log.Info("Add_user_add_row.PMNotification_RemoveSelectedUsers",
                    //     $"Removed user '{user.nameToDisplay}' with NodeId {userNodeId}");
                }
            }
            
            // Remove the row from the user list
            userList.Remove(row);
        }

        // Set final status and log result
        status = removedCount > 0 
            ? $"Successfully removed {removedCount} selected users." 
            : "No selected users were removed.";
        //Log.Info("Add_user_add_row.PMNotification_RemoveSelectedUsers",$"Completed removal process: {removedCount} selected users and their rows removed.");
    }

}
