# MSSQL SERVER

Not affiliated with Microsoft. SQL Server, SSMS and SqlClient are Microsoft products; this project only speaks the same wire protocol and client APIs.

---

## What is new in the app

1. **One app, one Activity, two tabs.** The old `activity_main` server UI became `panel_server.xml`; the client UI is new (`panel_client.xml`).
2. **Both driver stacks included and selectable per connection:**
   - `Microsoft.Data.SqlClient` 7.1.2 (modern, recommended)
   - `System.Data.SqlClient` 4.8.6 (classic stack)
   - **Auto** mode: tries Microsoft.Data.SqlClient first, then falls back to System.Data.SqlClient.
   - Both packages declare the *same* namespace and type names, so the source never references a SqlClient type at compile time: no `extern alias`, no csproj `Aliases`. The two adapter files load their assembly **by name at runtime** (`Assembly.Load`) and drive it through `System.Data.Common` (`DbConnection` / `DbCommand` / `DbDataReader`). `TrimmerRootAssembly` entries in the `.csproj` keep both packages inside Release builds.
3. **Normal connection strings, exactly like on a PC.** The CLIENT tab parses and passes through:
Data Source=.\SQLExpress;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=yourpassword;
   - `Data Source` accepts everything SqlClient accepts: `.\SQLEXPRESS`, `(local)`, `localhost`, `HOST\INSTANCE`, `192.168.1.10`, **`ip,port`** (e.g. `192.168.1.10,1433`), `HOST\INSTANCE,PORT`.
   - Optional **Encrypt** selector (only appended when the string has no `Encrypt=` of its own).
   - Optional **Trust server certificate** checkbox (appends `TrustServerCertificate=True` when missing). Default ON so typical PC servers with self-signed certificates work out of the box.
   - Values already written by the user in the string always win; the app only adds missing keywords and tells you what it added.
4. **SSMS-like query experience:** batches separated by lines containing only `GO`, multiple result sets rendered as aligned monospace text ("Results to Text" style), `(N row(s) affected)` messages, SSMS-style errors (`Msg 18456, Level 14, State 1: ...`), best-effort database list from `sys.databases` with one-tap `USE` switching, last connection string remembered (app-private storage).
5. **SERVER tab addition:** shows the device's live Wi-Fi/Ethernet IPv4 addresses as ready-to-type endpoints (`192.168.1.23,1433`) for PC clients.
6. Same `ApplicationId` (`com.mssqlserver.app`), versionCode 6 (v1.1): installing this APK **upgrades** the old MSSQL Server app and keeps its databases, `config.json` and logs.

---

## Project layout
MSSQL_SERVER/
├── MSSQL_SERVER.sln
└── MSSQL_Server/
├── MSSQL_Server.csproj (both SqlClient packages + TrimmerRootAssembly linker roots)
├── AndroidManifest.xml (INTERNET, ACCESS_NETWORK_STATE)
├── MainActivity.cs (tab host: SERVER / CLIENT)
├── MainActivity.ServerPanel.cs (server UI logic - port of the original app)
├── MainActivity.ClientPanel.cs (client UI logic - new)
├── Client/
│ ├── SqlClientAdapter.cs (driver abstraction + Auto ordering + runtime loading helpers)
│ ├── MicrosoftDataSqlClientAdapter.cs (loads Microsoft.Data.SqlClient by name at runtime)
│ ├── SystemDataSqlClientAdapter.cs (loads System.Data.SqlClient by name at runtime)
│ ├── ConnectionStringInfo.cs (parse/validate/normalize connection strings)
│ ├── QueryRunner.cs (GO batches, result sets, rows affected)
│ └── QueryResultFormatter.cs (SSMS "Results to Text" rendering)
├── Core/ (ServerManager, ServerState, NetworkInfo)
├── Configuration/ Database/ Engine/ Logging/ Security/ Sql/ Tds/ (engine, unchanged)
└── Resources/
├── layout/activity_main.xml (tab host)
├── layout/panel_server.xml (old server UI + device-IP line)
├── layout/panel_client.xml (new client UI)
└── values/ mipmap-*/ (icons, app name "MSSQL SERVER")

---

## Build

Requirements: Visual Studio 2022 17.12+ with the ".NET desktop + Android" workloads, or the CLI:
dotnet workload install android
dotnet build MSSQL_SERVER.sln -f net9.0-android

`Microsoft.Data.SqlClient` 7.1.2 and `System.Data.SqlClient` 4.8.6 are restored from nuget.org. Deploy like any .NET Android app (Run in VS, or `dotnet publish -f net9.0-android` for an APK).

> Notes:
> - `TrimMode` is `partial` (the .NET mobile default). Full trimming of `System.Data.SqlClient` (netstandard2.0, not trim-annotated) can break it at runtime on device, so the merged project keeps it safe; Microsoft.Data.SqlClient remains fully functional under this mode as well.
> - The adapters reach both driver assemblies only via reflection, so the csproj registers them as `TrimmerRootAssembly`; the IL linker then keeps both packages inside Release builds, and the **Auto** driver mode still falls back to the other stack if one of them ever fails to load.

---

## Using the CLIENT tab

### 1. Connect to a PC SQL Server / SQL Express (from the phone, same Wi-Fi)

1. On the PC: enable TCP/IP in SQL Server Configuration Manager, enable Mixed Mode auth, make sure the firewall allows the port, note the PC's IP.
2. In the app, set the connection string, for example:
Data Source=192.168.1.10,1433;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=yourpassword;
   (or `Data Source=PCNAME\SQLEXPRESS;...` — instance names use SQL Browser, UDP 1434)
3. Driver: `Auto` (or a specific stack), then **Connect**. Server version and the database list appear; pick a database to switch; type queries and **Run**.

### 2. Connect to the app's own local engine

1. SERVER tab: set an `sa` password, **Apply**, **Start Server**.
2. CLIENT tab: tap **Fill: local engine (this device)** — it fills `Data Source=127.0.0.1,1433;...;Encrypt=False;` — add `;Password=YOUR-SERVER-PASSWORD;` before `User Id` or edit as you like, then **Connect**.

About `.\SQLExpress` and `(local)`: these mean "on **this** device". On a PC they point at that PC's SQL Express; on the phone there is no SQL Express instance, so from the phone use the PC's `ip,port` instead. The parser accepts the strings unchanged either way.

### 3. Connect from PC SSMS to this app's server

1. Phone and PC on the same network; SERVER tab shows `Use in PC SSMS: 192.168.1.23,1433`.
2. SSMS: Server name `192.168.1.23,1433`, Authentication = SQL Server, login `sa` + your password.
3. Encryption: the local engine does not support TLS yet (PRELOGIN answers ENCRYPT_NOT_SUP):
   - SSMS 19: set **Encrypt connection: False** in the connection dialog.
   - SSMS 20/21: set **Encryption: Optional** in the connection dialog.
   - Any PC app using `SqlClient`: add `Encrypt=False;` (or `TrustServerCertificate=True` for the classic stack) to the connection string.

The engine speaks TDS 7.x (PRELOGIN -> LOGIN7 -> SQL batches / `sp_executesql` RPCs), so SSMS, Visual Studio's SQL Server Object Explorer, ADO.NET apps, `sqlcmd` and the in-app CLIENT all work against it. Supported SQL surface (same as the original project, plus the SSMS scripting additions listed below): `SELECT` (WHERE/GROUP BY/HAVING/ORDER BY/JOIN/TOP/DISTINCT, functions incl. `@@VERSION`, `@@SERVERNAME`, `@@SPID`), `INSERT`, `UPDATE`, `DELETE`, `CREATE/DROP TABLE`, `CREATE/DROP DATABASE`, `USE`, `BEGIN/COMMIT/ROLLBACK TRAN`, plus virtual catalogs `sys.databases` / `sys.tables`.

---

## Troubleshooting (SSMS against the SERVER tab)

These are the three errors that appear when PC SSMS drives the on-device engine. They are **protocol/feature gaps, not network failures** — confirm reachability first with `Test-NetConnection <phone-ip> -Port 1433` (`TcpTestSucceeded : True` means the engine is listening and the path is fine).

| Symptom | Root cause | Fix |
| --- | --- | --- |
| `Cannot show requested dialog` + `provider: TCP Provider, error: 0 - The wait operation timed out` (Error **258**) | SSMS opened a dialog (Properties / Dashboard / Object Explorer Details) that needs a **fresh** connection and the engine dropped it mid-script. | Use **New Query** windows for work. Avoid right-click → Properties / Dashboard against the phone server (see the patch below to make Properties open anyway). |
| `Msg 208 ... Invalid object name 'Employees'` | Table not present in the **current** database. | Run `SELECT DB_NAME();` to see context, then `USE <db>;` and `CREATE TABLE` / `INSERT` as needed. |
| `Msg 2812 ... Could not find stored procedure 'master.dbo.xp_instance_regread'` | SSMS's server-configuration scripts call `xp_instance_regread`, which the mini engine does not implement. | Apply the **SSMS Properties patch** below. |

### SSMS Properties patch (optional)

Android has no Windows registry, so `xp_instance_regread` is faked by rewriting SSMS's script into plain `SET @var = ...` statements before parsing. Add `using System.Text.RegularExpressions;` to the top of `MSSQL_Server/Engine/SqlEngine.cs`, then wrap the start of `ExecuteBatch(string sql, SqlSession session)` with:

```csharp
// --- SSMS Properties patch: fake xp_instance_regread ---
if (sql.Contains("xp_instance_regread", StringComparison.OrdinalIgnoreCase))
{
    var pattern = @"exec\s+master\.dbo\.xp_instance_regread\s+[^,]+,\s*[^,]+,\s*[^,]+,\s*(@\w+)\s+OUTPUT";
    var dummy = new Dictionary<string, string>
    {
        { "@SmoAuditLevel", "0" }, { "@NumErrorLogs", "0" }, { "@SmoLoginMode", "1" },
        { "@SmoMailProfile", "''" }, { "@BackupDirectory", "'C:\\fake\\backup'" },
        { "@SmoPerfMonMode", "0" }, { "@InstallSqlDataDir", "'C:\\fake\\data'" },
        { "@ServiceStartMode", "2" }, { "@ServiceAccount", "'NT Service\\MSSQLSERVER'" },
        { "@NamedPipesEnabled", "0" }, { "@TcpEnabled", "1" },
        { "@InstallSharedDirectory", "'C:\\fake\\shared'" }, { "@SqlGroup", "''" },
        { "@FilestreamLevel", "0" }, { "@FilestreamShareName", "''" }
    };
    sql = Regex.Replace(sql, pattern, m =>
        dummy.TryGetValue(m.Groups[1].Value, out var v) ? $"SET {m.Groups[1].Value} = {v}" : $"SET {m.Groups[1].Value} = 0",
        RegexOptions.IgnoreCase);
}
// --- end patch ---
After rebuilding, SSMS Server Properties will open against the phone (fields show the dummy values). If it still errors, check the app log for the next Invalid object name / 2812 and add that catalog view or function to the engine's sys surface.
Data persistence (IMPORTANT: databases are deleted on uninstall)
By default the engine writes its database files into the app's private sandbox (/data/data/com.mssqlserver.app/). Android wipes that folder on uninstall, so your created databases disappear with the app.
Do not uninstall the app unless you have backed up. To free memory without losing data use Settings → Apps → MSSQL SERVER → Storage → Clear Cache (never "Clear Data").
To keep databases across uninstall/reinstall, point the engine at external storage instead of the private folder. In MainActivity.cs (where SqlEngine is constructed):
csharp
// Private (default, wiped on uninstall):
// string dbPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);

// External (survives uninstall):
string dbPath = Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, "MSSQL_Server_Data");
Directory.CreateDirectory(dbPath);
var engine = new SqlEngine(dbPath, "MSSQLServer");
Then in AndroidManifest.xml add, before <application>:
xml
  <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
  <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
  <uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE" />
and request WRITE_EXTERNAL_STORAGE at runtime in MainActivity.OnCreate(). (On Android 11+ you may need the "All files access" toggle.) A "Downloads" variant:
csharp
  string dbPath = Path.Combine(
      Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads).AbsolutePath,
      "MSSQL_Server_Data");
Alternatively add a Backup button that copies the private root directory to Download/MSSQL_Backup before you uninstall.
Driver notes
Microsoft.Data.SqlClient (default/Auto): modern TDS, Encrypt defaults to Mandatory; against a PC server with a self-signed certificate keep the Trust server certificate box checked, or add Encrypt=False;.
System.Data.SqlClient: classic stack (keyword defaults unchanged, Encrypt off by default). Chosen per connection from the Driver spinner; useful when a legacy server or legacy behaviour is expected. It ships as netstandard2.0 and runs through the managed SNI implementation on Android.
Auto: tries Microsoft.Data.SqlClient, and on any failure retries the same connection with System.Data.SqlClient, reporting which one actually connected.
Security notes (unchanged from the original projects)
The embedded server stores only a PBKDF2-SHA256 (210k iterations) salt+hash of sa; 5 failed logins per IP lock out for 1 minute; config is saved atomically.
The local TDS listener has no TLS — use it on trusted networks only.
The CLIENT tab stores the last connection string (including any password inside it) in Android app-private preferences, like SSMS's "remember password". Nothing is written outside the app sandbox.
Disclaimer
Not affiliated with Microsoft. This project only speaks the same wire protocol and client APIs as SQL Server / SSMS / SqlClient, which are Microsoft products. The on-device engine implements a practical subset of T-SQL and the sys catalog; it is intended for learning, testing and light workloads, not as a production database server.
