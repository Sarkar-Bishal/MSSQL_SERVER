using Android.Content;
using Android.Graphics;
using Android.Widget;
using MSSQL_Server.Client;
using System.Data.Common;

namespace MSSQL_Server
{
    /// <summary>
    /// CLIENT tab: the SSMS-style client (merged from SSMS_Unofficial_Secure). Connects to ANY
    /// normal SQL Server - PC, SQL Express, or this app's own local engine - using a standard
    /// connection string, through Microsoft.Data.SqlClient or System.Data.SqlClient.
    /// </summary>
    public partial class MainActivity
    {
        private static readonly string DefaultConnectionString =
            "Data Source=.\\SQLExpress;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=yourpassword;";
        private static readonly string DefaultQuery = "SELECT @@VERSION;";

        private EditText? txtConnStr;
        private Spinner? spinDriver;
        private Spinner? spinEncrypt;
        private CheckBox? chkTrustServer;
        private Button? btnLocal;
        private Button? btnConnect;
        private Button? btnDisconnect;
        private TextView? txtConnStatus;
        private Spinner? spinDb;
        private EditText? txtQuery;
        private Button? btnRun;
        private TextView? txtResults;

        private DbConnection? clientConnection;
        private SqlClientAdapter? activeAdapter;
        private string? connectedServerVersion;
        private string? currentDatabase;
        private List<string> lastDatabases = new();
        private bool suppressDbSpinner;
        private bool clientBusy;

        private sealed class ConnectResult
        {
            public DbConnection? Connection;
            public SqlClientAdapter? Adapter;
            public string Version = string.Empty;
            public List<string> Databases = new();
            public string Applied = string.Empty;
            public string? Error;
        }

        private void InitializeClientWidgets()
        {
            txtConnStr = FindViewById<EditText>(Resource.Id.txtConnStr);
            spinDriver = FindViewById<Spinner>(Resource.Id.spinDriver);
            spinEncrypt = FindViewById<Spinner>(Resource.Id.spinEncrypt);
            chkTrustServer = FindViewById<CheckBox>(Resource.Id.chkTrustServer);
            btnLocal = FindViewById<Button>(Resource.Id.btnLocal);
            btnConnect = FindViewById<Button>(Resource.Id.btnConnect);
            btnDisconnect = FindViewById<Button>(Resource.Id.btnDisconnect);
            txtConnStatus = FindViewById<TextView>(Resource.Id.txtConnStatus);
            spinDb = FindViewById<Spinner>(Resource.Id.spinDb);
            txtQuery = FindViewById<EditText>(Resource.Id.txtQuery);
            btnRun = FindViewById<Button>(Resource.Id.btnRun);
            txtResults = FindViewById<TextView>(Resource.Id.txtResults);

            txtConnStr!.Text = DefaultConnectionString;
            txtQuery!.Text = DefaultQuery;
            chkTrustServer!.Checked = true;

            spinDriver!.Adapter = MakeSpinnerAdapter(new List<string>
            {
                "Auto (Microsoft.Data.SqlClient, then System.Data.SqlClient)",
                "Microsoft.Data.SqlClient",
                "System.Data.SqlClient"
            });
            spinEncrypt!.Adapter = MakeSpinnerAdapter(new List<string>
            {
                "Encrypt: keep value from string",
                "Encrypt: False (no TLS)",
                "Encrypt: True (TLS)"
            });
            spinDb!.Adapter = MakeSpinnerAdapter(new List<string> { "(connect first)" });

            RestoreClientPrefs();

            btnLocal!.Click += BtnLocal_Click;
            btnConnect!.Click += BtnConnect_Click;
            btnDisconnect!.Click += BtnDisconnect_Click;
            btnRun!.Click += BtnRun_Click;
            spinDb.ItemSelected += SpinDb_ItemSelected;

            UpdateClientUi();
        }

        private ArrayAdapter<string> MakeSpinnerAdapter(List<string> items)
        {
            var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, items);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            return adapter;
        }

        private void RestoreClientPrefs()
        {
            try
            {
                var prefs = GetSharedPreferences("mssql_client", FileCreationMode.Private);
                if (prefs == null) return;

                var saved = prefs.GetString("conn", null);
                if (!string.IsNullOrEmpty(saved)) txtConnStr!.Text = saved;
                spinDriver!.SetSelection(Math.Min(prefs.GetInt("driver", 0), 2));
                spinEncrypt!.SetSelection(Math.Min(prefs.GetInt("encrypt", 0), 2));
                chkTrustServer!.Checked = prefs.GetBoolean("trust", true);
            }
            catch (Exception)
            {
                // Preferences are convenience only; defaults stay in place on any failure.
            }
        }

        private void SaveClientPrefs(string conn, int driverIndex, int encryptIndex, bool trust)
        {
            try
            {
                var prefs = GetSharedPreferences("mssql_client", FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit == null) return;
                edit.PutString("conn", conn);
                edit.PutInt("driver", driverIndex);
                edit.PutInt("encrypt", encryptIndex);
                edit.PutBoolean("trust", trust);
                edit.Apply();
            }
            catch (Exception)
            {
                // Preferences are convenience only.
            }
        }

        private static ClientDriver SelectedDriver(int spinnerIndex) => spinnerIndex switch
        {
            1 => ClientDriver.MicrosoftDataSqlClient,
            2 => ClientDriver.SystemDataSqlClient,
            _ => ClientDriver.Auto
        };

        private static string SelectedEncrypt(int spinnerIndex) => spinnerIndex switch
        {
            1 => ConnectionStringInfo.EncryptOff,
            2 => ConnectionStringInfo.EncryptOn,
            _ => ConnectionStringInfo.EncryptFromConnection
        };

        // ---------------------------------------------------------------- actions

        private void BtnLocal_Click(object? sender, EventArgs e)
        {
            int port = configuration?.Port ?? 1433;
            txtConnStr!.Text =
                $"Data Source=127.0.0.1,{port};Initial Catalog=master;Persist Security Info=True;User Id=sa;Encrypt=False;";
            ShowMessage(
                "This talks to the SERVER tab inside this app. Add your 'sa' password (Password=...) and tap Connect.",
                @long: true);
        }

        private async void BtnConnect_Click(object? sender, EventArgs e)
        {
            if (clientBusy) return;

            string raw = txtConnStr!.Text ?? string.Empty;
            var info = ConnectionStringInfo.Parse(raw);
            if (info.ParseError != null)
            {
                ShowMessage("Connection string problem: " + info.ParseError, @long: true);
                return;
            }

            bool trust = chkTrustServer!.Checked;
            string encryptChoice = SelectedEncrypt(spinEncrypt!.SelectedItemPosition);
            ClientDriver requested = SelectedDriver(spinDriver!.SelectedItemPosition);
            int driverIndex = spinDriver.SelectedItemPosition;
            int encryptIndex = spinEncrypt.SelectedItemPosition;

            clientBusy = true;
            UpdateClientUi();
            txtConnStatus!.Text = "CONNECTING...";
            txtConnStatus.SetTextColor(Color.Rgb(217, 119, 6));
            txtResults!.Text = string.Empty;

            try
            {
                // Network work runs on a worker thread; blocking Open() is fine there.
                ConnectResult result = await Task.Run(() => TryConnect(info, trust, encryptChoice, requested));

                if (result.Connection != null && result.Adapter != null)
                {
                    clientConnection = result.Connection;
                    activeAdapter = result.Adapter;
                    connectedServerVersion = result.Version;
                    lastDatabases = result.Databases;

                    FillDatabaseSpinner(result.Databases, result.Connection);
                    txtConnStatus.Text = "CONNECTED" +
                        (result.Version.Length > 0 ? " - " + result.Version : "");
                    txtConnStatus.SetTextColor(Color.Rgb(22, 163, 74));
                    ShowMessage($"Connected with {result.Adapter.DisplayName} ({result.Applied}).", @long: true);
                    SaveClientPrefs(raw, driverIndex, encryptIndex, trust);
                }
                else
                {
                    txtConnStatus.Text = "NOT CONNECTED - " + (result.Error ?? "unknown failure");
                    txtConnStatus.SetTextColor(Color.Rgb(220, 38, 38));
                }
            }
            catch (Exception ex)
            {
                ShowMessage("Connect failed: " + ex.Message, @long: true);
            }
            finally
            {
                clientBusy = false;
                UpdateClientUi();
            }
        }

        private ConnectResult TryConnect(
            ConnectionStringInfo info, bool trust, string encryptChoice, ClientDriver requested)
        {
            var result = new ConnectResult();
            string effective = info.BuildEffective(trust, encryptChoice, out string applied);
            result.Applied = applied;
            string? lastError = null;

            foreach (var (_, adapter) in SqlClientAdapter.ResolveOrder(requested))
            {
                DbConnection? conn = null;
                try
                {
                    conn = adapter.CreateConnection(effective);
                    conn.Open();

                    result.Version = QueryFirstLine(adapter, conn);
                    result.Databases = QueryNameList(adapter, conn);
                    result.Connection = conn;
                    result.Adapter = adapter;
                    logger?.Info($"[client] connected to {info.Server ?? "?"} using {adapter.DisplayName}");
                    return result;
                }
                catch (Exception ex)
                {
                    try { conn?.Dispose(); } catch (Exception) { }
                    lastError = adapter.DescribeException(ex);
                    logger?.Warn($"[client] {adapter.DisplayName} connect failed: {lastError}");
                }
            }

            result.Error = lastError;
            return result;
        }

        private static string QueryFirstLine(SqlClientAdapter adapter, DbConnection conn)
        {
            try
            {
                using var cmd = adapter.CreateCommand("SELECT @@VERSION;", conn);
                cmd.CommandTimeout = 15;
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && reader.FieldCount > 0 && !reader.IsDBNull(0))
                {
                    string full = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
                    int nl = full.IndexOf('\n');
                    return (nl >= 0 ? full.Substring(0, nl) : full).Trim();
                }
            }
            catch (Exception)
            {
                // @@VERSION is best-effort; the connection itself is already established.
            }
            return string.Empty;
        }

        private static List<string> QueryNameList(SqlClientAdapter adapter, DbConnection conn)
        {
            var names = new List<string>();
            try
            {
                using var cmd = adapter.CreateCommand("SELECT name FROM sys.databases ORDER BY name", conn);
                cmd.CommandTimeout = 15;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        string name = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
                        if (name.Length > 0) names.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // The database list is best-effort (permissions may block sys.databases).
            }
            return names;
        }

        private void BtnDisconnect_Click(object? sender, EventArgs e) => DisconnectClient();

        private void DisconnectClient()
        {
            var conn = clientConnection;
            clientConnection = null;
            activeAdapter = null;
            connectedServerVersion = null;
            currentDatabase = null;
            lastDatabases.Clear();

            if (conn != null)
            {
                try { conn.Dispose(); } catch (Exception) { }
            }

            RunOnUiThread(() =>
            {
                txtConnStatus!.Text = "NOT CONNECTED";
                txtConnStatus.SetTextColor(Color.Rgb(220, 38, 38));
                suppressDbSpinner = true;
                spinDb!.Adapter = MakeSpinnerAdapter(new List<string> { "(connect first)" });
                suppressDbSpinner = false;
                txtResults!.Text = string.Empty;
                UpdateClientUi();
            });
        }

        private async void SpinDb_ItemSelected(object? sender, AdapterView.ItemSelectedEventArgs e)
        {
            if (suppressDbSpinner || e.Position <= 0) return; // row 0 = "(current: ...)" placeholder
            var conn = clientConnection;
            var adapter = activeAdapter;
            if (conn == null || adapter == null) return;

            string? name = spinDb!.GetItemAtPosition(e.Position)?.ToString();
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, currentDatabase, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                await Task.Run(() => conn.ChangeDatabase(name));
                currentDatabase = name;
                logger?.Info($"[client] changed database to '{name}'.");
                FillDatabaseSpinner(lastDatabases, conn);
                string version = connectedServerVersion ?? string.Empty;
                txtConnStatus!.Text = "CONNECTED" +
                    (version.Length > 0 ? " - " + version : "") +
                    $" | db: {name}";
                ShowMessage("Using database: " + name);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not switch database: " + adapter.DescribeException(ex), @long: true);
            }
        }

        private async void BtnRun_Click(object? sender, EventArgs e)
        {
            if (clientBusy) return;
            var adapter = activeAdapter;
            var conn = clientConnection;
            if (adapter == null || conn == null)
            {
                ShowMessage("Connect to a server first. Start the SERVER tab, then use 'Fill: local engine'.");
                return;
            }

            string script = txtQuery!.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(script))
            {
                ShowMessage("Type a query first.");
                return;
            }

            clientBusy = true;
            UpdateClientUi();
            txtResults!.Text = "Running...";

            try
            {
                RunOutcome outcome = await Task.Run(() => QueryRunner.Execute(adapter, conn, script));
                txtResults.Text = QueryResultFormatter.Format(outcome);
                if (outcome.Failed) ShowMessage("Batch failed - see RESULTS.", @long: true);
            }
            catch (Exception ex)
            {
                txtResults.Text = "ERROR: " + ex.Message;
            }
            finally
            {
                clientBusy = false;
                UpdateClientUi();
            }
        }

        private void FillDatabaseSpinner(List<string> databases, DbConnection conn)
        {
            string currentDb;
            try { currentDb = conn.Database; }
            catch (Exception) { currentDb = string.Empty; }
            currentDatabase = string.IsNullOrEmpty(currentDb) ? null : currentDb;

            var items = new List<string>
            {
                string.IsNullOrEmpty(currentDb) ? "(select database)" : $"(current: {currentDb})"
            };
            items.AddRange(databases);

            suppressDbSpinner = true;
            spinDb!.Adapter = MakeSpinnerAdapter(items);
            suppressDbSpinner = false;
        }

        private void UpdateClientUi()
        {
            bool connected = clientConnection != null;
            btnConnect!.Enabled = !clientBusy && !connected;
            btnDisconnect!.Enabled = !clientBusy && connected;
            btnRun!.Enabled = !clientBusy && connected;
            btnLocal!.Enabled = !clientBusy;
            txtConnStr!.Enabled = !clientBusy && !connected;
        }
    }
}
