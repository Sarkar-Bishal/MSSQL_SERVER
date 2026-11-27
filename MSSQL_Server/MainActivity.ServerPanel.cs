using Android.Graphics;
using Android.Widget;
using MSSQL_Server.Core;
using MSSQL_Server.Security;

namespace MSSQL_Server
{
    /// <summary>
    /// SERVER tab: faithful port of the original MSSQL_Server app - start/stop the TDS engine,
    /// configure host/port/'sa' password, watch the log. New: shows the device's LAN addresses
    /// so PC SSMS / PC SqlClient users know exactly what to type.
    /// </summary>
    public partial class MainActivity
    {
        private const int MaxLogLines = 40;

        private TextView? txtStatus;
        private TextView? txtDeviceIp;
        private TextView? txtDatabasePath;
        private TextView? txtLog;
        private EditText? txtHost;
        private EditText? txtPort;
        private EditText? txtPassword;
        private Button? btnStart;
        private Button? btnStop;
        private Button? btnApply;

        private readonly Queue<string> logLines = new();
        private ServerState serverState = ServerState.Stopped;

        private void InitializeServerWidgets()
        {
            txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            txtDeviceIp = FindViewById<TextView>(Resource.Id.txtDeviceIp);
            txtDatabasePath = FindViewById<TextView>(Resource.Id.txtDatabasePath);
            txtLog = FindViewById<TextView>(Resource.Id.txtLog);
            txtHost = FindViewById<EditText>(Resource.Id.txtHost);
            txtPort = FindViewById<EditText>(Resource.Id.txtPort);
            txtPassword = FindViewById<EditText>(Resource.Id.txtPassword);
            btnApply = FindViewById<Button>(Resource.Id.btnApply);
            btnStart = FindViewById<Button>(Resource.Id.btnStart);
            btnStop = FindViewById<Button>(Resource.Id.btnStop);

            txtDatabasePath!.Text = databaseManager!.DatabaseDirectory;
            txtHost!.Text = configuration!.Host;
            txtPort!.Text = configuration.Port.ToString();

            btnApply!.Click += BtnApply_Click;
            btnStart!.Click += BtnStart_Click;
            btnStop!.Click += BtnStop_Click;

            UpdateDeviceIpText();
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            if (configuration == null) return;

            if (serverState is not (ServerState.Stopped or ServerState.Error))
            {
                ShowMessage("Stop the server before changing configuration.");
                return;
            }

            string host = txtHost!.Text?.Trim() ?? "";
            string portText = txtPort!.Text?.Trim() ?? "";
            string password = txtPassword!.Text ?? "";

            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                ShowMessage("Enter a valid IP address (for example 0.0.0.0).");
                return;
            }

            if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
            {
                ShowMessage("Enter a valid TCP port.");
                return;
            }

            if (password.Length > 0 && password.Length < 8)
            {
                ShowMessage("The password must be at least 8 characters.");
                return;
            }

            configuration.Host = host;
            configuration.Port = port;

            if (password.Length > 0)
            {
                var (salt, hash) = PasswordHasher.Hash(password);
                configuration.AdminPasswordSalt = salt;
                configuration.AdminPasswordHash = hash;
                txtPassword.Text = string.Empty;
                logger?.Info("'sa' password updated.");
            }

            try
            {
                configuration.Save(configPath);
                UpdateDeviceIpText();
                ShowMessage("Configuration saved.");
            }
            catch (Exception ex)
            {
                logger?.Error($"Could not save configuration: {ex.Message}");
                ShowMessage("Could not save configuration.");
            }
        }

        private void BtnStart_Click(object? sender, EventArgs e)
        {
            if (configuration is { HasAdminPassword: false })
            {
                ShowMessage("Set an 'sa' password and tap Apply first.");
                return;
            }
            server?.Start();
        }

        private async void BtnStop_Click(object? sender, EventArgs e)
        {
            if (server == null) return;
            btnStop!.Enabled = false;
            await server.StopAsync();
        }

        private void AppendLog(string line)
        {
            RunOnUiThread(() =>
            {
                logLines.Enqueue(line);
                while (logLines.Count > MaxLogLines) logLines.Dequeue();
                if (txtLog != null) txtLog.Text = string.Join("\n", logLines);
            });
        }

        private void UpdateServerStatus(ServerState state)
        {
            serverState = state;
            if (txtStatus == null) return;

            txtStatus.Text = state.ToString().ToUpperInvariant();
            txtStatus.SetTextColor(state switch
            {
                ServerState.Running => Color.Rgb(22, 163, 74),
                ServerState.Starting or ServerState.Stopping => Color.Rgb(217, 119, 6),
                _ => Color.Rgb(220, 38, 38)
            });

            btnStart!.Enabled = state is ServerState.Stopped or ServerState.Error;
            btnStop!.Enabled = state is ServerState.Running or ServerState.Starting;
            UpdateDeviceIpText();
        }

        private void UpdateDeviceIpText()
        {
            if (txtDeviceIp == null || configuration == null) return;

            var ips = NetworkInfo.GetActiveIPv4Addresses();
            txtDeviceIp.Text = ips.Count == 0
                ? "Device IP: (no Wi-Fi/Ethernet network yet - PC SSMS cannot connect)"
                : "Use in PC SSMS:  " + string.Join("   or   ", ips.Select(ip => $"{ip},{configuration.Port}"));
        }
    }
}
