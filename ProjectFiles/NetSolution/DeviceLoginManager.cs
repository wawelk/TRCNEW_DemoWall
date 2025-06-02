using System;
using System.Net.Http;
using System.Threading.Tasks;
using UAManagedCore;
using FTOptix.SerialPort;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;

namespace DeviceLogin
{
    public class DeviceLoginManager : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string ipAddress;
        private readonly IUANode owner;
        private readonly string deviceName;

        public DeviceLoginManager(string ipAddress, IUANode owner, string deviceName)
        {
            this.ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.deviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
            httpClient = new HttpClient(new HttpClientHandler())
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        public async Task<bool> CheckLoginState()
        {
            try
            {
                string loginStateUrl = $"http://{ipAddress}/cgi-bin/get_login_state";
               // Log.Info($"DeviceLoginManager-{deviceName}.CheckLoginState", $"Sending POST request to {loginStateUrl}");
                var response = await httpClient.PostAsync(loginStateUrl, null);

                if (response.IsSuccessStatusCode)
                {
                    string responseText = await response.Content.ReadAsStringAsync();
                   // Log.Info($"DeviceLoginManager-{deviceName}.CheckLoginState", $"Response: {responseText}");

                    if (responseText.Contains("admin_logged")) return true;
                    if (responseText.Contains("admin_no_logged")) return false;

                    Log.Warning($"DeviceLoginManager-{deviceName}.CheckLoginState", $"Unexpected login state response: {responseText}");
                    return false;
                }

                Log.Error($"DeviceLoginManager-{deviceName}.CheckLoginState", $"Failed to check login state: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"DeviceLoginManager-{deviceName}.CheckLoginState", $"Error checking login state: {ex.Message}");
                return false;
            }
        }

        public async Task<string> CheckLockedState()
        {
            try
            {
                string lockedStateUrl = $"http://{ipAddress}/cgi-bin/get_locked_state";
               // Log.Info($"DeviceLoginManager-{deviceName}.CheckLockedState", $"Sending POST request to {lockedStateUrl}");
                var response = await httpClient.PostAsync(lockedStateUrl, null);

                if (response.IsSuccessStatusCode)
                {
                    string responseText = await response.Content.ReadAsStringAsync();
                   // Log.Info($"DeviceLoginManager-{deviceName}.CheckLockedState", $"Response: {responseText}");

                    if (responseText.Contains("No_Locked")) return "No_Locked";
                    if (responseText.Contains("Locked")) return "Locked";

                    Log.Warning($"DeviceLoginManager-{deviceName}.CheckLockedState", $"Unexpected locked state response: {responseText}");
                    return "Unknown";
                }

                Log.Error($"DeviceLoginManager-{deviceName}.CheckLockedState", $"Failed to check locked state: {response.StatusCode}");
                return "Error";
            }
            catch (Exception ex)
            {
                Log.Error($"DeviceLoginManager-{deviceName}.CheckLockedState", $"Error checking locked state: {ex.Message}");
                return "Error";
            }
        }

        public async Task<bool> LoginToDevice(string username, string password)
        {
            try
            {
                string loginUrl = $"http://{ipAddress}/security/cgi-bin/security|3|{username}|{password}";
               // Log.Info($"DeviceLoginManager-{deviceName}.LoginToDevice", $"Sending POST request to {loginUrl}");
                var loginResponse = await httpClient.PostAsync(loginUrl, null);

                if (loginResponse.IsSuccessStatusCode)
                {
                    string responseText = await loginResponse.Content.ReadAsStringAsync();
                   // Log.Info($"DeviceLoginManager-{deviceName}.LoginToDevice", $"Login response: {responseText}");
                    return true; // Always return true if the request succeeds, handled in EnsureLoggedIn
                }

                Log.Error($"DeviceLoginManager-{deviceName}.LoginToDevice", $"Login failed: {loginResponse.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"DeviceLoginManager-{deviceName}.LoginToDevice", $"Error during login: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> EnsureLoggedIn()
        {
           // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Ensuring user is logged in before proceeding");

            try
            {
                string lockedState = await CheckLockedState();
               // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", $"Locked state: {lockedState}");
                if (lockedState == "Locked")
                {
                    Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Device is locked, cannot proceed with login");
                    return false;
                }
                else if (lockedState == "Error")
                {
                    Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Failed to check locked state, aborting");
                    return false;
                }

                bool isLoggedIn = await CheckLoginState();
                if (isLoggedIn)
                {
                   // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "User is already logged in (admin_logged)");
                    return true;
                }

                //Log.Warning($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "User is not logged in (admin_no_logged or unexpected response)");

                var usernameVar = owner.GetVariable("Username");
                var passwordVar = owner.GetVariable("Password");

                if (usernameVar == null || passwordVar == null)
                {
                    Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Username or Password variable not found in Owner");
                    return false;
                }

                string username = usernameVar.Value.Value as string; // Safely cast to string
                string password = passwordVar.Value.Value as string; // Safely cast to string

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Username or Password is null or empty");
                    return false;
                }

               // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", $"Attempting to log in with username: {username}");
                var loginResponse = await httpClient.PostAsync($"http://{ipAddress}/security/cgi-bin/security|3|{username}|{password}", null);
                if (!loginResponse.IsSuccessStatusCode)
                {
                    Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", $"Login failed: {loginResponse.StatusCode}");
                    return false;
                }

                string loginResponseText = await loginResponse.Content.ReadAsStringAsync();
               // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", $"Login response: {loginResponseText}");

                // Check if security is disabled
                if (loginResponseText.Contains("Security_disabled"))
                {
                   // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Security is disabled, no login required");
                    return true; // Proceed without requiring admin_logged
                }

                // If security is enabled, verify login state
                isLoggedIn = await CheckLoginState();
                if (isLoggedIn)
                {
                   // Log.Info($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "Confirmed: User is now logged in (admin_logged)");
                    return true;
                }

                Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", "User is still not logged in after login attempt");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"DeviceLoginManager-{deviceName}.EnsureLoggedIn", $"Unexpected error during login check: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
