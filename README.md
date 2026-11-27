# MSSQL SERVER (merged)

One Android app that is **both** of the original projects, merged:

| Tab | Comes from | What it does |
|---|---|---|
| **SERVER** | `MSSQL_Server_Test` | Runs the TDS SQL-Server-compatible engine on the device. PC SSMS / PC `SqlClient` apps connect to it like a normal MSSQL server (`ip,port`, login `sa`). |
| **CLIENT** | `SSMS_Unofficial_Secure` | An SSMS-style query client on the phone. Connects to **any** normal SQL Server (PC, SQL Express, Azure, or this app's own local engine) with a standard connection string. |

Not affiliated with Microsoft. SQL Server, SSMS and SqlClient are Microsoft products; this project only speaks the same wire protocol and client APIs.

---

## What is new in the merge

1. **One app, one Activity, two tabs.** The old `activity_main` server UI became `panel_server.xml`; the client UI is new (`panel_client.xml`).
2. **Both driver stacks included and selectable per connection:**
   - `Microsoft.Data.SqlClient` 7.1.2 (modern, recommended)
   - `System.Data.SqlClient` 4.8.6 (classic stack)
   - **Auto** mode: tries Microsoft.Data.SqlClient first, then falls back to System.Data.SqlClient.
   - Both packages declare the *same* namespace and type names, so the source never references a SqlClient type at compile time: no `extern alias`, no csproj `Aliases`. The two adapter files load their assembly **by name at runtime** (`Assembly.Load`) and drive it through `System.Data.Common` (`DbConnection` / `DbCommand` / `DbDataReader`). `TrimmerRootAssembly` entries in the `.csproj` keep both packages inside Release builds.
3. **Normal connection strings, exactly like on a PC.** The CLIENT tab parses and passes through:
   ```
   Data Source=.\SQLExpress;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=yourpassword;
   ```
   - `Data Source` accepts everything SqlClient accepts: `.\SQLEXPRESS`, `(local)`, `localhost`, `HOST\INSTANCE`, `192.168.1.10`, **`ip,port`** (e.g. `192.168.1.10,1433`), `HOST\INSTANCE,PORT`.
   - Optional **Encrypt** selector (only appended when the string has no `Encrypt=` of its own).
   - Optional **Trust server certificate** checkbox (appends `TrustServerCertificate=True` when missing). Default ON so typical PC servers with self-signed certificates work out of the box.
   - Values already written by the user in the string always win; the app only adds missing keywords and tells you what it added.
4. **SSMS-like query experience:** batches separated by lines containing only `GO`, multiple result sets rendered as aligned monospace text ("Results to Text" style), `(N row(s) affected)` messages, SSMS-style errors (`Msg 18456, Level 14, State 1: ...`), best-effort database list from `sys.databases` with one-tap `USE` switching, last connection string remembered (app-private storage).
5. **SERVER tab addition:** shows the device's live Wi-Fi/Ethernet IPv4 addresses as ready-to-type endpoints (`192.168.1.23,1433`) for PC clients.
6. Same `ApplicationId` (`com.mssqlserver.app`), versionCode 6 (v1.1): installing this APK **upgrades** the old MSSQL Server app and keeps its databases, `config.json` and logs.

> **SSMS connect fix (Error 102 "Incorrect syntax near 'DECLARE'"):** after a successful login,
> SSMS immediately runs its own T-SQL (server-property scripts starting with
> `DECLARE @edition sysname; SET @edition = CAST(SERVERPROPERTY('Edition') AS sysname); ...`,
> catalog queries, sometimes `sp_executesql`). The engine now implements that scripting surface:
> scalar variables (`DECLARE` / `SET @v = ...` / `SELECT @v = ...`), `IF`/`ELSE`/`BEGIN...END`,
> `CASE`, `CAST`/`CONVERT`, `EXISTS (SELECT ...)`, `EXEC sp_executesql` (also inside batches),
> `RETURN`, `PRINT`, `@@ROWCOUNT`/`@@ERROR`, plus extra catalog views
> (`sys.objects`, `sys.schemas`, `sys.columns`, `sys.indexes`, `sys.views`, `sys.procedures`,
> `sys.configurations`, `sys.dm_exec_sessions`, `sys.dm_exec_connections`, `sys.dm_os_host_info`,
> `sys.dm_os_sys_info`) and functions (`OBJECT_ID`, `OBJECT_NAME`, `DB_ID`, `HAS_DBACCESS`,
> `DATABASEPROPERTYEX`, extended `SERVERPROPERTY`, `LEFT`, `RIGHT`, `CHARINDEX`, `CEILING`,
> `FLOOR`).

> **Build fix (CS0430 / CS0234):** an earlier revision used `extern alias sds;` / `extern alias msds;`
> plus `Aliases="sds"` / `Aliases="msds"` in the `.csproj`. When a build does not apply that alias
> metadata to the compiler `/reference` option, Visual Studio reports
> `CS0430: The extern alias 'sds' was not specified in a /reference option` and then
> `CS0234: The type or namespace name 'System' does not exist in the namespace 'sds'` for
> `Client/SystemDataSqlClientAdapter.cs`. The alias mechanism is now removed completely: the
> adapters resolve their assemblies at runtime, so nothing has to be wired into the csproj and
> the project compiles on any machine. If you still see those two errors, you are compiling an
> older copy - replace the whole `Client/` folder (and `MSSQL_Server.csproj`) with this revision.

Everything under `Core/`, `Engine/`, `Tds/`, `Sql/`, `Security/`, `Logging/`, `Configuration/`, `Database/` is byte-identical to the original MSSQL_Server_Test engine (verified with `cmp`).

---

## Project layout

```
MSSQL_SERVER/
├── MSSQL_SERVER.sln
└── MSSQL_Server/
    ├── MSSQL_Server.csproj            (both SqlClient packages + TrimmerRootAssembly linker roots)
    ├── AndroidManifest.xml            (INTERNET, ACCESS_NETWORK_STATE)
    ├── MainActivity.cs                (tab host: SERVER / CLIENT)
    ├── MainActivity.ServerPanel.cs    (server UI logic - port of the original app)
    ├── MainActivity.ClientPanel.cs    (client UI logic - new)
    ├── Client/
    │   ├── SqlClientAdapter.cs            (driver abstraction + Auto ordering + runtime loading helpers)
    │   ├── MicrosoftDataSqlClientAdapter.cs (loads Microsoft.Data.SqlClient by name at runtime)
    │   ├── SystemDataSqlClientAdapter.cs    (loads System.Data.SqlClient by name at runtime)
    │   ├── ConnectionStringInfo.cs        (parse/validate/normalize connection strings)
    │   ├── QueryRunner.cs                 (GO batches, result sets, rows affected)
    │   └── QueryResultFormatter.cs        (SSMS "Results to Text" rendering)
    ├── Core/  (ServerManager, ServerState, NetworkInfo)
    ├── Configuration/ Database/ Engine/ Logging/ Security/ Sql/ Tds/   (engine, unchanged)
    └── Resources/
        ├── layout/activity_main.xml   (tab host)
        ├── layout/panel_server.xml    (old server UI + device-IP line)
        ├── layout/panel_client.xml    (new client UI)
        └── values/ mipmap-*/          (icons, app name "MSSQL SERVER")
```

---

## Build

Requirements: Visual Studio 2022 17.12+ with the ".NET desktop + Android" workloads, or the CLI:

```
dotnet workload install android
dotnet build MSSQL_SERVER.sln -f net9.0-android
```

`Microsoft.Data.SqlClient` 7.1.2 and `System.Data.SqlClient` 4.8.6 are restored from nuget.org. Deploy like any .NET Android app (Run in VS, or `dotnet publish -f net9.0-android` for an APK).

> Notes:
> - `TrimMode` is `partial` (the .NET mobile default). Full trimming of `System.Data.SqlClient` (netstandard2.0, not trim-annotated) can break it at runtime on device, so the merged project keeps it safe; Microsoft.Data.SqlClient remains fully functional under this mode as well.
> - The adapters reach both driver assemblies only via reflection, so the csproj registers them as `TrimmerRootAssembly`; the IL linker then keeps both packages inside Release builds, and the **Auto** driver mode still falls back to the other stack if one of them ever fails to load.

---

## Using the CLIENT tab

### 1. Connect to a PC SQL Server / SQL Express (from the phone, same Wi-Fi)

1. On the PC: enable TCP/IP in SQL Server Configuration Manager, enable Mixed Mode auth, make sure the firewall allows the port, note the PC's IP.
2. In the app, set the connection string, for example:
   ```
   Data Source=192.168.1.10,1433;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=yourpassword;
   ```
   (or `Data Source=PCNAME\SQLEXPRESS;...` - instance names use SQL Browser, UDP 1434)
3. Driver: `Auto` (or a specific stack), then **Connect**. Server version and the database list appear; pick a database to switch; type queries and **Run**.

### 2. Connect to the app's own local engine

1. SERVER tab: set an `sa` password, **Apply**, **Start Server**.
2. CLIENT tab: tap **Fill: local engine (this device)** - it fills `Data Source=127.0.0.1,1433;...;Encrypt=False;` - add `;Password=YOUR-SERVER-PASSWORD;` before `User Id` or edit as you like, then **Connect**.

About `.\SQLExpress` and `(local)`: these mean "on **this** device". On a PC they point at that PC's SQL Express; on the phone there is no SQL Express instance, so from the phone use the PC's `ip,port` instead. The parser accepts the strings unchanged either way.

### 3. Connect from PC SSMS to this app's server

1. Phone and PC on the same network; SERVER tab shows `Use in PC SSMS: 192.168.1.23,1433`.
2. SSMS: Server name `192.168.1.23,1433`, Authentication = SQL Server, login `sa` + your password.
3. Encryption: the local engine does not support TLS yet (PRELOGIN answers ENCRYPT_NOT_SUP):
   - SSMS 19: set **Encrypt connection: False** in the connection dialog.
   - SSMS 20/21: set **Encryption: Optional** in the connection dialog.
   - Any PC app using `SqlClient`: add `Encrypt=False;` (or `TrustServerCertificate=True` for the classic stack) to the connection string.

The engine speaks TDS 7.x (PRELOGIN -> LOGIN7 -> SQL batches / `sp_executesql` RPCs), so SSMS, Visual Studio's SQL Server Object Explorer, ADO.NET apps, `sqlcmd` and the in-app CLIENT all work against it. Supported SQL surface (same as the original project, plus the SSMS scripting additions listed above): `SELECT` (WHERE/GROUP BY/HAVING/ORDER BY/JOIN/TOP/DISTINCT, functions incl. `@@VERSION`, `@@SERVERNAME`, `@@SPID`), `INSERT`, `UPDATE`, `DELETE`, `CREATE/DROP TABLE`, `CREATE/DROP DATABASE`, `USE`, `BEGIN/COMMIT/ROLLBACK TRAN`, plus virtual catalogs `sys.databases` / `sys.tables`.

---

## Driver notes

- **Microsoft.Data.SqlClient** (default/Auto): modern TDS, `Encrypt` defaults to **Mandatory**; against a PC server with a self-signed certificate keep the *Trust server certificate* box checked, or add `Encrypt=False;`.
- **System.Data.SqlClient**: classic stack (keyword defaults unchanged, `Encrypt` off by default). Chosen per connection from the Driver spinner; useful when a legacy server or legacy behaviour is expected. It ships as netstandard2.0 and runs through the managed SNI implementation on Android.
- **Auto**: tries Microsoft.Data.SqlClient, and on any failure retries the same connection with System.Data.SqlClient, reporting which one actually connected.

## Security notes (unchanged from the original projects)

- The embedded server stores only a PBKDF2-SHA256 (210k iterations) salt+hash of `sa`; 5 failed logins per IP lock out for 1 minute; config is saved atomically.
- The local TDS listener has **no TLS** - use it on trusted networks only.
- The CLIENT tab stores the last connection string (including any password inside it) in Android app-private preferences, like SSMS's "remember password". Nothing is written outside the app sandbox.
