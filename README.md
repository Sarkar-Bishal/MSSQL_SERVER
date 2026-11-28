<h1 align="center">MSSQL SERVER for Android</h1>

<p align="center">
  An independent SQL database server running directly on Android using C# and .NET.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Android-green" alt="Platform">
  <img src="https://img.shields.io/badge/Language-C%23-blue" alt="Language">
  <img src="https://img.shields.io/badge/Framework-.NET%209.0-purple" alt="Framework">
  <img src="https://img.shields.io/badge/Protocol-TDS-orange" alt="Protocol">
  <img src="https://img.shields.io/badge/Engine-Custom%20Engine-blue" alt="Database">
  <img src="https://img.shields.io/badge/License-TBD-lightgrey" alt="License">
</p>

---

## 1. Project Overview

**MSSQL SERVER** is an open-source project focused on developing an independent SQL database server that runs directly on Android devices.

The project is developed using **C# and .NET**, with a custom database engine and Microsoft TDS (Tabular Data Stream) protocol compatibility.

The primary objective is to allow Android devices to host a database server that can be accessed remotely from computers and other applications using familiar SQL Server client technologies.

The project is not based on installing or running Microsoft's SQL Server database engine on Android. Instead, it implements its own database engine and communication layer.

### Project Vision

Build a lightweight, independent SQL database server that provides:

- Custom database storage engine.
- SQL query execution.
- Microsoft TDS protocol compatibility.
- TCP-based remote database connectivity.
- Android internal database storage.
- Multiple client connections.
- Database authentication and security.
- Background server operation.
- Android database management interface.
- Compatibility with SQL Server client applications.

**Important:** This project is independent software. It is not developed, maintained, endorsed, or officially supported by Microsoft.

---

## 2. Project Information

| Property | Details |
|---|---|
| Project Name | MSSQL SERVER |
| Project Type | Independent SQL Database Server |
| Target Platform | Android |
| Programming Language | C# |
| Framework | .NET for Android |
| Database Engine | Custom Engine |
| Client Protocol | Microsoft TDS |
| Network Communication | TCP/IP |
| Storage | Android Internal Storage |
| SQL Language | T-SQL-compatible subset |
| Development Environment | Visual Studio / .NET CLI |
| Repository | GitHub |
| License | To be decided |

### Technology Stack

| Technology | Purpose |
|---|---|
| C# | Core application and database engine |
| .NET | Application framework |
| Android XML | User interface |
| TCP/IP | Network communication |
| TDS | Database client communication |
| Custom Storage Engine | Database persistence |
| SQL Parser | SQL statement processing |
| JSON | Server configuration |
| PBKDF2-SHA256 | Password hashing |

---

## 3. Current Application Features

The current application contains two main sections.

### SERVER

The SERVER section manages the database server running on the Android device.

Features include:

- Server configuration.
- Server start and stop controls.
- Server authentication configuration.
- TCP listener.
- Configurable server port.
- Device IPv4 address detection.
- Database server connection endpoint.
- Application logging.
- Local database access.

### CLIENT

The CLIENT section allows the Android application to connect to SQL database servers.

Features include:

- SQL Server connection strings.
- Microsoft.Data.SqlClient integration.
- System.Data.SqlClient integration.
- Automatic driver selection.
- Manual driver selection.
- Database selection.
- SQL query execution.
- Multiple result sets.
- Query result formatting.
- SQL error reporting.
- GO batch processing.

The CLIENT section can connect to external SQL Server installations as well as the application's local database server.

---

## 4. Database Engine

The project uses an independent custom database engine.

It does not use SQLite as its underlying database engine.

### Implemented SQL Features

The current SQL implementation includes:

| Category | Supported Features |
|---|---|
| Database | CREATE DATABASE, DROP DATABASE |
| Tables | CREATE TABLE, DROP TABLE |
| Data | SELECT, INSERT, UPDATE, DELETE |
| Filtering | WHERE |
| Sorting | ORDER BY |
| Grouping | GROUP BY, HAVING |
| Joins | JOIN |
| Query Options | TOP, DISTINCT |
| Transactions | BEGIN TRAN, COMMIT, ROLLBACK |
| Database Selection | USE |
| System Information | @@VERSION, @@SERVERNAME, @@SPID |
| System Catalogs | sys.databases, sys.tables |

The SQL implementation represents a practical subset of T-SQL, not complete Microsoft SQL Server compatibility.

### Planned Database Engine Improvements

- Database file format specification.
- Database headers and metadata.
- Page-based storage management.
- Page allocation.
- Buffer pool.
- Advanced data types.
- B-Tree indexing.
- Write-Ahead Logging (WAL).
- Transaction isolation.
- Lock management.
- Crash recovery.
- Storage integrity validation.

---

## 5. Microsoft TDS Protocol Compatibility

The server implements a TDS-based communication layer to allow external SQL Server client applications to communicate with the Android-hosted database engine.

### Current Protocol Capabilities

- TDS packet communication.
- PRELOGIN negotiation.
- LOGIN7 authentication.
- SQL Batch processing.
- RPC request processing.
- SQL response tokens.
- Result-set serialization.
- Column metadata.
- Row data transmission.
- SQL error responses.

### Client Compatibility

The project targets compatibility with:

| Client | Status |
|---|---|
| Microsoft.Data.SqlClient | Supported integration |
| System.Data.SqlClient | Supported integration |
| SQL Server Management Studio (SSMS) | Basic connectivity demonstrated |
| ADO.NET applications | Basic connectivity demonstrated |
| sqlcmd | Target compatibility |

Compatibility is limited to the protocol features and SQL functionality implemented by the project.

### Important Protocol Limitation

The current server does not support TLS encryption.

For connections to the Android server, encryption must be disabled or configured as optional in the client.

Do not expose the current server to untrusted networks.

---

## 6. Project Architecture

### Current Repository Structure

```text
MSSQL_SERVER/
│
├── MSSQL_SERVER.sln
│
└── MSSQL_Server/
    │
    ├── MainActivity.cs
    ├── MainActivity.ServerPanel.cs
    ├── MainActivity.ClientPanel.cs
    ├── MSSQL_Server.csproj
    ├── AndroidManifest.xml
    │
    ├── Client/
    │   ├── SqlClientAdapter.cs
    │   ├── MicrosoftDataSqlClientAdapter.cs
    │   ├── SystemDataSqlClientAdapter.cs
    │   ├── ConnectionStringInfo.cs
    │   ├── QueryRunner.cs
    │   └── QueryResultFormatter.cs
    │
    ├── Core/
    │
    ├── Configuration/
    │
    ├── Database/
    │
    ├── Engine/
    │
    ├── Logging/
    │
    ├── Security/
    │
    ├── Sql/
    │
    ├── Tds/
    │
    └── Resources/
        ├── layout/
        │   ├── activity_main.xml
        │   ├── panel_server.xml
        │   └── panel_client.xml
        │
        ├── values/
        │
        └── mipmap-*/
```

### Proposed Future Modular Architecture

As development progresses, the project is intended to evolve into a modular architecture.

```text
DroidSQL/
│
├── DroidSQL.sln
│
├── src/
│   ├── DroidSQL.Android/
│   ├── DroidSQL.Core/
│   ├── DroidSQL.Storage/
│   ├── DroidSQL.Sql/
│   └── DroidSQL.Tds/
│
├── tests/
│   ├── DroidSQL.Core.Tests/
│   ├── DroidSQL.Storage.Tests/
│   ├── DroidSQL.Sql.Tests/
│   └── DroidSQL.Tds.Tests/
│
├── docs/
│   ├── ARCHITECTURE.md
│   ├── DATABASE-FORMAT.md
│   ├── SQL-SPECIFICATION.md
│   ├── TDS-COMPATIBILITY.md
│   └── SECURITY.md
│
├── samples/
│
├── LICENSE
├── README.md
└── CONTRIBUTING.md
```

The Android application must remain independent of the database engine, SQL execution engine, and TDS implementation.

---

## 7. Development Roadmap

The project is divided into ten major development phases.

### Phase 1 — Android Application Foundation

**Objective:** Establish the Android application and server management foundation.

Tasks:

- [x] Android project creation.
- [x] MainActivity implementation.
- [x] Android XML user interface.
- [x] Server management interface.
- [x] Client management interface.
- [x] Basic server configuration.
- [x] Server state management.
- [x] Application logging.
- [ ] Complete configuration persistence and validation.
- [ ] Foundation testing.

**Deliverable:** Android server management application.

### Phase 2 — Custom Database Storage Engine

**Objective:** Develop a persistent, independent database storage engine.

Tasks:

- [x] Initial custom database engine.
- [x] Basic database creation and management.
- [x] Basic table management.
- [x] Data insertion and retrieval.
- [x] Data updating and deletion.
- [ ] Formal database file format.
- [ ] Database header and metadata.
- [ ] Page management.
- [ ] Page allocation.
- [ ] Buffer pool.
- [ ] B-Tree indexing.
- [ ] Write-Ahead Logging (WAL).
- [ ] Crash recovery.
- [ ] Storage integrity testing.

**Deliverable:** Reliable persistent custom database engine.

### Phase 3 — SQL Language Engine

**Objective:** Develop the SQL processing and execution system.

Tasks:

- [x] Basic SQL statement execution.
- [x] SELECT statements.
- [x] INSERT statements.
- [x] UPDATE statements.
- [x] DELETE statements.
- [x] CREATE DATABASE.
- [x] CREATE TABLE.
- [x] DROP DATABASE.
- [x] DROP TABLE.
- [x] WHERE conditions.
- [x] ORDER BY.
- [x] GROUP BY and HAVING.
- [x] JOIN operations.
- [ ] Dedicated SQL Lexer.
- [ ] Dedicated SQL Parser.
- [ ] Abstract Syntax Tree (AST).
- [ ] Query planner.
- [ ] Query optimizer.
- [ ] Expanded SQL functions and expressions.

**Deliverable:** Extensible SQL execution engine.

### Phase 4 — TCP Network Server

**Objective:** Enable remote database connections.

Tasks:

- [x] TCP listener.
- [x] Configurable server port.
- [x] Remote connection support.
- [x] Client connection handling.
- [x] Request processing.
- [x] Response handling.
- [x] Device IP detection.
- [ ] Advanced session management.
- [ ] Connection timeout management.
- [ ] Graceful shutdown.
- [ ] Advanced network error handling.
- [ ] Connection stress testing.

**Deliverable:** Network-accessible Android database server.

### Phase 5 — Microsoft TDS Protocol

**Objective:** Implement TDS compatibility for SQL Server client applications.

Tasks:

- [x] TDS packet communication.
- [x] PRELOGIN negotiation.
- [x] LOGIN7 authentication.
- [x] SQL Batch processing.
- [x] RPC processing.
- [x] SQL response serialization.
- [x] Column metadata.
- [x] Row data serialization.
- [ ] Expanded TDS version compatibility.
- [ ] Complete error and informational token handling.
- [ ] Advanced transaction protocol support.
- [ ] TLS encryption.
- [ ] Expanded SSMS compatibility testing.

**Deliverable:** Expanded TDS-compatible database server.

### Phase 6 — Authentication and Security

**Objective:** Protect database access and user information.

Tasks:

- [x] Server password authentication.
- [x] PBKDF2-SHA256 password hashing.
- [x] Basic failed-login rate limiting.
- [ ] Multiple database users.
- [ ] Database permissions.
- [ ] Role management.
- [ ] TLS certificate management.
- [ ] Session security improvements.
- [ ] Security auditing.
- [ ] Comprehensive security testing.

**Deliverable:** Secure database access system.

### Phase 7 — Android Background Server

**Objective:** Allow the database server to operate independently of the management interface.

Tasks:

- [ ] Android Foreground Service.
- [ ] Persistent server notification.
- [ ] Start server from background service.
- [ ] Stop server.
- [ ] Restart server.
- [ ] Server lifecycle management.
- [ ] Battery and resource monitoring.
- [ ] Network disconnection handling.
- [ ] Crash recovery.
- [ ] Background execution testing.

**Deliverable:** Android-hosted database server operating through a foreground service.

### Phase 8 — Database Management Dashboard

**Objective:** Build a complete database administration interface.

Tasks:

- [x] Server management interface.
- [x] Client connection interface.
- [x] SQL query editor.
- [x] Query execution results.
- [x] Basic server information.
- [ ] Live connection monitoring.
- [ ] Database explorer.
- [ ] Table explorer.
- [ ] Advanced server monitoring.
- [ ] Configuration management.
- [ ] Backup and restore.
- [ ] Storage monitoring.
- [ ] Improved log viewer.

**Deliverable:** Complete Android database management dashboard.

### Phase 9 — Testing and Compatibility

**Objective:** Validate reliability, performance, and client compatibility.

Tasks:

- [ ] Unit testing.
- [ ] Storage engine testing.
- [ ] SQL execution testing.
- [ ] TDS protocol testing.
- [ ] Multiple client testing.
- [ ] SSMS compatibility testing.
- [ ] Microsoft.Data.SqlClient testing.
- [ ] System.Data.SqlClient testing.
- [ ] Stress testing.
- [ ] Crash recovery testing.
- [ ] Android device compatibility testing.
- [ ] Performance benchmarking.

**Deliverable:** Tested and documented database server.

### Phase 10 — Open-Source Release

**Objective:** Prepare the project for public development and community contributions.

Tasks:

- [x] Public GitHub repository.
- [ ] Final project license.
- [ ] Complete README documentation.
- [ ] Installation guide.
- [ ] Developer documentation.
- [ ] Architecture documentation.
- [ ] TDS compatibility matrix.
- [ ] Database format documentation.
- [ ] Release APK.
- [ ] Versioning strategy.
- [ ] Community contribution guidelines.
- [ ] First stable release.

**Deliverable:** Public open-source project.

---

## 8. Development Progress

<details>
<summary><strong>Current development status</strong></summary>

| Phase | Development Area | Status |
|---|---|---|
| 1 | Android Application Foundation | In Progress |
| 2 | Custom Database Storage Engine | In Progress |
| 3 | SQL Language Engine | In Progress |
| 4 | TCP Network Server | In Progress |
| 5 | Microsoft TDS Protocol | In Progress |
| 6 | Authentication and Security | In Progress |
| 7 | Android Background Server | Planned |
| 8 | Database Management Dashboard | In Progress |
| 9 | Testing and Compatibility | Planned |
| 10 | Open-source Release | In Progress |

**Progress measurement:** The table describes implementation maturity, not verified completion of every task. Individual checkboxes must be updated as features are implemented and tested.

</details>

### Project Milestones

| Milestone | Expected Result |
|---|---|
| M1 | Android application foundation |
| M2 | Persistent database files |
| M3 | SQL query execution |
| M4 | Network-accessible database |
| M5 | Initial TDS client connectivity |
| M6 | Authentication and security |
| M7 | Background server operation |
| M8 | Complete management dashboard |
| M9 | Compatibility and performance testing |
| M10 | Public release |

---

## 9. Development Principles

The following principles guide the project.

**1. Independent Database Engine**

The project must implement its own database storage and execution engine. SQLite must not be used as the underlying database engine.

**2. Architecture Independence**

The database engine, SQL engine, core components, and TDS implementation must not depend on Android user interface APIs.

**3. Data Integrity**

Database persistence, transactions, locking, WAL, and recovery must be designed and tested before production-readiness claims.

**4. Protocol Compatibility**

TDS compatibility must be demonstrated through actual connections using SQL Server client applications.

**5. Modular Development**

Each major component should be independently testable and maintainable.

**6. Security First**

Authentication, password protection, encryption, session management, and database permissions must be treated as essential components.

**7. Open-Source Development**

The project should encourage developers to contribute improvements, report issues, and extend functionality.

---

## 10. Build and Development Setup

### Requirements

- Windows 10/11 or another supported .NET development environment.
- Visual Studio 2022 version 17.12 or later.
- .NET for Android workload.
- Android SDK.
- C# development experience.

### Install Android Workload

```bash
dotnet workload install android
```

### Clone Repository

```bash
git clone https://github.com/Sarkar-Bishal/MSSQL_SERVER.git
```

### Navigate to Project

```bash
cd MSSQL_SERVER
```

### Build Solution

```bash
dotnet build MSSQL_SERVER.sln -f net9.0-android
```

### Publish Android Application

```bash
dotnet publish MSSQL_SERVER.sln -f net9.0-android
```

For a release APK, configure the appropriate Android release signing and publishing settings.

---

## 11. Connecting from SQL Server Management Studio

The Android server can be accessed from a computer connected to the same network.

### Connection Instructions

1. Start the database server on the Android device.
2. Identify the Android device's IPv4 address.
3. Open SQL Server Management Studio on the computer.
4. Select SQL Server Authentication.
5. Enter the Android device IP address and port.
6. Enter the configured server username and password.
7. Configure encryption as Optional or disabled, as appropriate for the current server implementation.
8. Connect.

Example server address:

```text
192.168.1.23,1433
```

Example connection string:

```text
Data Source=192.168.1.23,1433;
Initial Catalog=master;
User Id=sa;
Password=YOUR_PASSWORD;
Encrypt=False;
```

**Security warning:** The current server does not support TLS. Use only trusted networks and do not expose the listener directly to the public internet.

---

## 12. Data Storage and Backup

The database engine currently uses Android application-private storage.

Important considerations:

- Application-private files are protected by Android's application sandbox.
- Uninstalling the application deletes its private database files.
- Clearing application data also deletes the database files.
- Database backups should be created before uninstalling or resetting the application.

Database backup and restore functionality is part of the development roadmap.

---

## 13. Known Limitations

The project is under active development and is not a replacement for production Microsoft SQL Server.

Current limitations include:

- Incomplete T-SQL compatibility.
- Incomplete SQL Server system catalog compatibility.
- No TLS encryption in the current server.
- Limited SSMS administrative functionality.
- Incomplete storage engine architecture.
- Advanced indexing and transaction features remain under development.
- Android background service support remains planned.
- Comprehensive automated testing is pending.

---

## 14. Open-Source Contribution

Contributions from developers are welcome.

Potential contribution areas:

- C# and .NET development.
- Database storage engine.
- SQL parsing and execution.
- TDS protocol implementation.
- Android development.
- Network programming.
- Security.
- Performance optimization.
- Testing and documentation.

### Contribution Process

1. Fork the repository.
2. Create a feature branch.
3. Implement your changes.
4. Test your implementation.
5. Submit a pull request.
6. Provide a clear description of the changes.

---

## 15. Repository

**GitHub:**  
https://github.com/Sarkar-Bishal/MSSQL_SERVER

Issues, feature requests, bug reports, and contributions should be submitted through the GitHub repository.

---

## 16. License

The final open-source license has not yet been selected.

MIT or Apache License 2.0 are being considered.

Until a license is added to the repository, users should not assume that the project is available under either license.

---

## 17. Disclaimer

This is an independent software development project.

Microsoft, SQL Server, SQL Server Management Studio (SSMS), Microsoft.Data.SqlClient, and System.Data.SqlClient are Microsoft products or trademarks.

This project is not affiliated with, endorsed by, or officially supported by Microsoft.

The project implements its own database engine and aims to support compatible client communication through the TDS protocol.

---

<p align="center">

**Built with C# and .NET**

**An independent SQL database server for Android.**

</p>
