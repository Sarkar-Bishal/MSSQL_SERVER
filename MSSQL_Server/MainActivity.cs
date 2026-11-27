using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using MSSQL_Server.Configuration;
using MSSQL_Server.Core;
using MSSQL_Server.Database;
using MSSQL_Server.Engine;
using MSSQL_Server.Logging;

namespace MSSQL_Server
{
    /// <summary>
    /// MSSQL SERVER 1.0 - the two projects merged into one app:
    ///   SERVER tab: the TDS SQL Server engine from MSSQL_Server_Test (SSMS/SqlClient connectable)
    ///   CLIENT tab: the SSMS-style client from SSMS_Unofficial_Secure (connects to any SQL Server:
    ///               .\SQLEXPRESS, host\instance, ip or ip,port - via Microsoft.Data.SqlClient
    ///               or System.Data.SqlClient).
    /// </summary>
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public partial class MainActivity : Activity
    {
        // ---------------------------------------------------------------- shared app objects
        private ServerConfiguration? configuration;
        private DatabaseManager? databaseManager;
        private ServerLogger? logger;
        private ServerManager? server;
        private string configPath = string.Empty;

        // ---------------------------------------------------------------- tab host widgets
        private RadioGroup? rgTabs;
        private FrameLayout? panelContainer;
        private View? serverPanelView;
        private View? clientPanelView;
        private int activeTab;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            rgTabs = FindViewById<RadioGroup>(Resource.Id.rgTabs);
            panelContainer = FindViewById<FrameLayout>(Resource.Id.panelContainer);

            InflatePanels();
            InitializeApplication();
            RegisterTabEvents();
            SwitchTab(0);
        }

        protected override void OnDestroy()
        {
            DisconnectClient();
            server?.Dispose();
            base.OnDestroy();
        }

        private void InflatePanels()
        {
            var serverPanel = LayoutInflater!.Inflate(Resource.Layout.panel_server, panelContainer, false)!;
            panelContainer!.AddView(serverPanel);
            serverPanelView = serverPanel;

            var clientPanel = LayoutInflater.Inflate(Resource.Layout.panel_client, panelContainer, false)!;
            panelContainer.AddView(clientPanel);
            clientPanelView = clientPanel;
        }

        private void InitializeApplication()
        {
            try
            {
                string appPath = FilesDir!.AbsolutePath;

                databaseManager = new DatabaseManager(appPath);
                configPath = Path.Combine(databaseManager.RootDirectory, "config.json");
                configuration = ServerConfiguration.Load(configPath);

                logger = new ServerLogger(Path.Combine(databaseManager.RootDirectory, "Logs"));
                logger.LineWritten += AppendLog;

                var engine = new SqlEngine(databaseManager.DatabaseDirectory, configuration.ServerName);
                server = new ServerManager(configuration, logger, engine);
                server.StateChanged += s => RunOnUiThread(() => UpdateServerStatus(s));

                InitializeServerWidgets();
                InitializeClientWidgets();

                UpdateServerStatus(ServerState.Stopped);
                logger.Info("MSSQL SERVER 1.0 initialised (engine + client merged).");
            }
            catch (Exception ex)
            {
                UpdateServerStatus(ServerState.Error);
                ShowMessage(ex.Message, @long: true);
            }
        }

        private void RegisterTabEvents()
        {
            rgTabs!.CheckedChange += (s, e) =>
                SwitchTab(e.CheckedId == Resource.Id.tabServer ? 0
                        : e.CheckedId == Resource.Id.tabClient ? 1
                        : activeTab);
        }

        private void SwitchTab(int index)
        {
            activeTab = index;
            if (serverPanelView != null)
                serverPanelView.Visibility = index == 0 ? ViewStates.Visible : ViewStates.Gone;
            if (clientPanelView != null)
                clientPanelView.Visibility = index == 1 ? ViewStates.Visible : ViewStates.Gone;
        }

        /// <summary>Shared toast helper for both panels.</summary>
        private void ShowMessage(string message, bool @long = false) =>
            Toast.MakeText(this, message, @long ? ToastLength.Long : ToastLength.Short)!.Show();
    }
}
