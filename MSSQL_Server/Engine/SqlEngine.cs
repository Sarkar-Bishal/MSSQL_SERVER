using System.Globalization;
using System.Text;
using MSSQL_Server.Sql;

namespace MSSQL_Server.Engine
{
    /// <summary>
    /// Executes parsed statements against file-backed tables. Engine-wide lock: one statement at a time.
    /// </summary>
    public sealed class SqlEngine
    {
        public const string VersionText =
            "MSSQL Server (unofficial) 0.5 - custom engine, not a Microsoft product";

        private readonly object gate = new();
        private readonly string root;
        private readonly Dictionary<string, Database> databases = new(StringComparer.OrdinalIgnoreCase);

        public string ServerName { get; }

        public SqlEngine(string rootDirectory, string serverName = "MSSQLServer")
        {
            root = rootDirectory;
            ServerName = serverName;
            Directory.CreateDirectory(root);

            foreach (var dir in Directory.GetDirectories(root))
            {
                string nameFile = Path.Combine(dir, "db.name");
                if (!File.Exists(nameFile)) continue;
                string name = File.ReadAllText(nameFile).Trim();
                if (name.Length > 0) databases[name] = new Database(name, dir);
            }
            if (!databases.ContainsKey("master")) CreateDatabaseInternal("master");
        }

        public IReadOnlyList<string> DatabaseNames
        {
            get { lock (gate) return databases.Values.Select(d => d.Name).OrderBy(n => n).ToList(); }
        }

        // ---------------------------------------------------------------- entry point

        public BatchResult ExecuteBatch(string sql, SqlSession session)
        {
            var result = new BatchResult();
            List<Stmt> statements;
            try { statements = Parser.ParseBatch(sql); }
            catch (SqlException ex) { result.Error = ex; return result; }

            lock (gate)
            {
                foreach (var stmt in statements)
                {
                    RunStatement(stmt, session, result);
                    if (result.Error != null || result.Stopped) break;
                }
            }
            return result;
        }

        /// <summary>
        /// Runs one statement and appends its result(s). Control-flow statements (IF/BEGIN...END,
        /// RETURN, EXEC sp_executesql) can produce several results, so they recurse into RunStatement.
        /// </summary>
        private void RunStatement(Stmt stmt, SqlSession ses, BatchResult result)
        {
            switch (stmt)
            {
                case IfStmt s:
                {
                    bool cond;
                    try { cond = Evaluator.IsTrue(Evaluator.Eval(s.Condition, EmptyContext(ses))); }
                    catch (SqlException ex) { result.Error = ex; return; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        result.Error = new SqlException(50000, "Internal error: " + ex.Message);
                        return;
                    }
                    if (cond) RunStatement(s.Then, ses, result);
                    else if (s.Else != null) RunStatement(s.Else, ses, result);
                    return;
                }
                case BlockStmt b:
                    foreach (var st in b.Statements)
                    {
                        RunStatement(st, ses, result);
                        if (result.Error != null || result.Stopped) return;
                    }
                    return;
                case ReturnStmt:
                    result.Stopped = true;
                    return;
                case ExecSql e:
                    RunExecSql(e, ses, result);
                    return;
                default:
                {
                    StatementResult r;
                    try { r = Execute(stmt, ses); }
                    catch (SqlException ex) { result.Error = ex; return; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        result.Error = new SqlException(50000, "Internal error: " + ex.Message);
                        return;
                    }
                    ses.LastRowCount = r.RowCount;
                    result.Statements.Add(r);
                    return;
                }
            }
        }

        private void RunExecSql(ExecSql e, SqlSession ses, BatchResult result)
        {
            string sql;
            try
            {
                var ctx = EmptyContext(ses);
                var batchObj = Evaluator.Eval(e.Batch, ctx);
                if (e.Params != null)
                    foreach (var prm in e.Params)
                        ses.Parameters[prm.Name] = Evaluator.Eval(prm.Value, ctx);
                sql = Evaluator.ToStr(batchObj ?? string.Empty);
            }
            catch (SqlException ex) { result.Error = ex; return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Error = new SqlException(50000, "Internal error: " + ex.Message);
                return;
            }

            List<Stmt> inner;
            try { inner = Parser.ParseBatch(sql); }
            catch (SqlException ex) { result.Error = ex; return; }

            foreach (var st in inner)
            {
                RunStatement(st, ses, result);
                if (result.Error != null || result.Stopped) return;
            }
        }

        private StatementResult ExecDeclare(DeclareVars d, SqlSession ses)
        {
            var ctx = EmptyContext(ses);
            foreach (var v in d.Vars)
                ses.Parameters[v.Name] = v.Init == null
                    ? null
                    : Evaluator.CastValue(Evaluator.Eval(v.Init, ctx), v.Type, v.Length);
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecSetVar(SetVar s, SqlSession ses)
        {
            ses.Parameters[s.Name] = Evaluator.Eval(s.Value, EmptyContext(ses));
            return new StatementResult { Kind = StatementKind.Other };
        }

        /// <summary>EXISTS (SELECT ...) support used by Evaluator.</summary>
        internal bool ExecExists(Select s, SqlSession ses)
        {
            var r = ExecSelect(s, ses);
            return r.Result != null && r.Result.Rows.Count > 0;
        }

        private StatementResult Execute(Stmt stmt, SqlSession ses) => stmt switch
        {
            NoOp => new StatementResult { Kind = StatementKind.None },
            BeginTran => ExecBegin(ses),
            CommitTran => ExecCommit(ses),
            RollbackTran => ExecRollback(ses),
            Select s => ExecSelect(s, ses),
            Insert i => ExecInsert(i, ses),
            Update u => ExecUpdate(u, ses),
            Delete d => ExecDelete(d, ses),
            CreateTable c => ExecCreateTable(c, ses),
            DropTable d => ExecDropTable(d, ses),
            CreateDb c => ExecCreateDb(c),
            DropDb d => ExecDropDb(d, ses),
            UseDb u => ExecUse(u, ses),
            DeclareVars d2 => ExecDeclare(d2, ses),
            SetVar sv => ExecSetVar(sv, ses),
            _ => throw new SqlException(102, "Unsupported statement.", 15)
        };

        // ---------------------------------------------------------------- transactions
        // Atomicity via in-memory undo: changes are applied immediately (other sessions can see them,
        // i.e. READ UNCOMMITTED), written to disk only on COMMIT, and undone on ROLLBACK or disconnect.

        public void EndSession(SqlSession session)
        {
            lock (gate) { if (session.Tx != null) RollbackAll(session); }
        }

        private static void Touch(SqlSession s, Database db, Table t)
        {
            if (s.Tx != null && !s.Tx.Original.ContainsKey(t)) s.Tx.Original[t] = (t.Rows, t.IdentityNext);
        }

        private static void Persist(SqlSession s, Database db, Table t)
        {
            if (s.Tx != null) s.Tx.Dirty[t] = db; else db.Save(t);
        }

        private StatementResult ExecBegin(SqlSession ses)
        {
            ses.Tx ??= new TransactionState();
            ses.Tx.Depth++;
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecCommit(SqlSession ses)
        {
            var tx = ses.Tx ?? throw new SqlException(3902, "The COMMIT TRANSACTION request has no corresponding BEGIN TRANSACTION.");
            if (--tx.Depth > 0) return new StatementResult { Kind = StatementKind.Other };

            foreach (var (table, db) in tx.Dirty)
                if (db.Tables.TryGetValue(table.Name, out var cur) && ReferenceEquals(cur, table)) db.Save(table);
            foreach (var (db, table) in tx.Dropped) db.DeleteFile(table);
            ses.Tx = null;
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecRollback(SqlSession ses)
        {
            if (ses.Tx == null)
                throw new SqlException(3903, "The ROLLBACK TRANSACTION request has no corresponding BEGIN TRANSACTION.");
            RollbackAll(ses);
            return new StatementResult { Kind = StatementKind.Other };
        }

        private static void RollbackAll(SqlSession ses)
        {
            var tx = ses.Tx!;
            foreach (var (table, orig) in tx.Original) { table.Rows = orig.Rows; table.IdentityNext = orig.Identity; }
            foreach (var (db, table) in tx.Dropped) db.Tables[table.Name] = table;
            foreach (var (db, table) in tx.Created) db.Tables.Remove(table.Name);
            ses.Tx = null;
        }

        // ---------------------------------------------------------------- databases

        private Database CreateDatabaseInternal(string name)
        {
            string dir = Path.Combine(root, Convert.ToHexString(Encoding.UTF8.GetBytes(name.ToLowerInvariant())));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "db.name"), name);
            var db = new Database(name, dir);
            databases[name] = db;
            return db;
        }

        private Database GetDatabase(string name) =>
            databases.TryGetValue(name, out var db) ? db
                : throw new SqlException(911, $"Database '{name}' does not exist. Make sure that the name is entered correctly.");

        private StatementResult ExecCreateDb(CreateDb c)
        {
            if (databases.ContainsKey(c.Name))
                throw new SqlException(1801, $"Database '{c.Name}' already exists. Choose a different database name.");
            if (c.Name.Length > 128) throw new SqlException(102, "Database name too long.", 15);
            CreateDatabaseInternal(c.Name);
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecDropDb(DropDb d, SqlSession ses)
        {
            var db = GetDatabase(d.Name);
            if (db.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
                throw new SqlException(3701, "Cannot drop the system database 'master'.");
            if (db.Name.Equals(ses.Database, StringComparison.OrdinalIgnoreCase))
                throw new SqlException(3702, $"Cannot drop database \"{db.Name}\" because it is currently in use.");
            databases.Remove(db.Name);
            Directory.Delete(db.Directory, recursive: true);
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecUse(UseDb u, SqlSession ses)
        {
            var db = GetDatabase(u.Name);
            string old = ses.Database;
            ses.Database = db.Name;
            return new StatementResult { Kind = StatementKind.Other, NewDatabase = db.Name, OldDatabase = old };
        }

        // ---------------------------------------------------------------- table lookup

        private static bool IsSystemSchema(string? schema) =>
            schema != null && (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
                               schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase));

        private Table ResolveTable(ObjectName n, SqlSession ses, out Database db, bool forWrite = false)
        {
            db = GetDatabase(n.Database ?? ses.Database);
            if (IsSystemSchema(n.Schema))
            {
                if (forWrite) throw new SqlException(259, "Ad hoc updates to system catalogs are not allowed.");
                return BuildVirtualTable(n, db, ses) ?? throw InvalidObject(n);
            }
            return db.Tables.TryGetValue(n.Name, out var t) ? t : throw InvalidObject(n);
        }

        private static SqlException InvalidObject(ObjectName n) =>
            new(208, $"Invalid object name '{(n.Schema != null ? n.Schema + "." : "")}{n.Name}'.");

        private Table? BuildVirtualTable(ObjectName n, Database db, SqlSession ses)
        {
            ColumnDef S(string name) => new(name, DataType.NVarChar, 128, false, false, false);
            ColumnDef I(string name) => new(name, DataType.Int, 0, false, false, false);
            ColumnDef B(string name) => new(name, DataType.BigInt, 0, false, false, false);
            const string stamp = "2026-01-01T00:00:00.000";
            string key = (n.Schema + "." + n.Name).ToLowerInvariant();

            List<ColumnDef> ObjectColumns() => new()
                { S("name"), B("object_id"), I("schema_id"), S("type"), S("type_desc"), S("create_date"), S("modify_date"), I("is_ms_shipped") };
            object?[] ObjectRow(string name, long id, string type, string typeDesc) =>
                new object?[] { name, id, 1L, type, typeDesc, stamp, stamp, 0L };

            switch (key)
            {
                case "sys.databases":
                {
                    // Wide shape: SMO / Object Explorer (Databases folder) SELECTs dozens of
                    // columns from sys.databases; any missing column aborts the tree build with
                    // "Invalid column name". Values are sensible defaults for a fresh engine.
                    var t = new Table("databases", new()
                    {
                        S("name"), B("database_id"), B("source_database_id"), S("create_date"),
                        I("compatibility_level"), S("collation_name"), I("state"), S("state_desc"),
                        I("is_read_only"), I("user_access"), S("user_access_desc"),
                        I("is_auto_close_on"), I("is_auto_shrink_on"),
                        I("is_auto_create_stats_on"), I("is_auto_update_stats_on"),
                        I("is_ansi_nulls_on"), I("is_ansi_padding_on"), I("is_ansi_warnings_on"),
                        I("is_arithabort_on"), I("is_concat_null_yields_null_on"),
                        I("is_quoted_identifiers_on"), I("is_numeric_round_abort_on"),
                        I("is_recursive_triggers_on"), I("is_cursor_close_on_commit_on"),
                        I("is_local_cursor_default"), I("is_fulltext_enabled"),
                        I("is_trustworthy_on"), I("is_db_chaining_on"),
                        I("is_parameterization_forced"), I("is_master_key_encrypted_by_server"),
                        I("is_query_store_on"), I("is_broker_enabled"), I("is_honor_broker_option_on"),
                        I("is_supplemental_logging_enabled"), I("snapshot_isolation_state"),
                        S("snapshot_isolation_state_desc"), I("is_read_committed_snapshot_on"),
                        I("recovery_model"), S("recovery_model_desc"), I("page_verify_option"),
                        S("page_verify_option_desc"), I("log_reuse_wait"), S("log_reuse_wait_desc"),
                        I("is_date_correlation_on"), I("is_cdc_enabled"), I("is_encrypted"),
                        I("is_memory_optimized_elevate_to_snapshot_on"), I("containment"),
                        S("containment_desc"), I("default_language_lcid"), S("default_language_name"),
                        I("default_fulltext_language_lcid"), S("default_fulltext_language_name"),
                    });
                    int id = 1;
                    foreach (var d in databases.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        t.Rows.Add(new object?[]
                        {
                            d.Name, (long)id++, null, stamp, 160L, "SQL_Latin1_General_CP1_CI_AS",
                            0L, "ONLINE", 0L, 0L, "MULTI_USER",
                            0L, 0L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 0L, 0L, 0L, 1L, 0L,
                            0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, "OFF", 0L,
                            3L, "SIMPLE", 0L, "NONE", 0L, "NOTHING",
                            0L, 0L, 0L, 0L, 0L, "NONE", 1033L, "us_english", 1033L, "us_english",
                        });
                    return t;
                }
                case "sys.server_principals":
                {
                    var t = new Table("server_principals", new()
                    {
                        B("principal_id"), B("sid"), S("name"), S("type"), S("type_desc"),
                        I("is_disabled"), S("create_date"), S("modify_date"),
                        S("default_database_name"), S("default_language_name"),
                        S("default_schema_name"), B("credential_id"), B("owning_principal_id"),
                    });
                    t.Rows.Add(new object?[] { 1L, null, "sa", "S", "SQL_LOGIN", 0L, stamp, stamp,
                        "master", "us_english", "dbo", null, null });
                    return t;
                }
                case "sys.sql_logins":
                {
                    var t = new Table("sql_logins", new()
                    {
                        B("principal_id"), B("sid"), S("name"), S("type"), S("type_desc"),
                        I("is_disabled"), S("create_date"), S("modify_date"),
                        S("default_database_name"), S("default_language_name"),
                        I("is_policy_checked"), I("is_expiration_checked"), B("credential_id"),
                    });
                    t.Rows.Add(new object?[] { 1L, null, "sa", "S", "SQL_LOGIN", 0L, stamp, stamp,
                        "master", "us_english", 0L, 0L, null });
                    return t;
                }
                case "sys.tables":
                {
                    var t = new Table("tables", ObjectColumns());
                    long id = 100;
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        t.Rows.Add(ObjectRow(tb.Name, id++, "U ", "USER_TABLE"));
                    return t;
                }
                case "sys.views":
                    return new Table("views", ObjectColumns()); // views are not stored (yet)
                case "sys.procedures":
                    return new Table("procedures", ObjectColumns());
                case "sys.objects":
                case "sys.all_objects":
                {
                    var t = new Table("objects", ObjectColumns());
                    long id = 100;
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        t.Rows.Add(ObjectRow(tb.Name, id++, "U ", "USER_TABLE"));
                    return t;
                }
                case "sys.schemas":
                {
                    var t = new Table("schemas", new() { B("schema_id"), S("name") });
                    t.Rows.Add(new object?[] { 1L, "dbo" });
                    t.Rows.Add(new object?[] { 2L, "sys" });
                    return t;
                }
                case "sys.columns":
                {
                    var t = new Table("columns", new()
                        { B("object_id"), B("column_id"), S("name"), B("system_type_id"), B("max_length"), I("precision"), I("scale"), I("is_nullable"), I("is_identity") });
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        long oid = TableObjectId(db, tb.Name);
                        for (int i = 0; i < tb.Columns.Count; i++)
                        {
                            var c = tb.Columns[i];
                            var (sysId, maxLen, prec, scale) = SysTypeOf(c.Type, c.Length);
                            t.Rows.Add(new object?[] { oid, (long)(i + 1), c.Name, sysId, maxLen, prec, scale,
                                c.NotNull ? 0L : 1L, c.Identity ? 1L : 0L });
                        }
                    }
                    return t;
                }
                case "sys.indexes":
                {
                    var t = new Table("indexes", new()
                        { B("object_id"), S("name"), B("index_id"), B("type"), S("type_desc"), I("is_unique"), I("is_primary_key"), I("is_unique_constraint"), I("fill_factor") });
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        bool pk = tb.Columns.Any(c => c.PrimaryKey);
                        t.Rows.Add(new object?[] { TableObjectId(db, tb.Name), pk ? "PK_" + tb.Name : null,
                            1L, 1L, "CLUSTERED", pk ? 1L : 0L, pk ? 1L : 0L, 0L, 0L });
                    }
                    return t;
                }
                case "sys.configurations":
                {
                    var t = new Table("configurations", new()
                        { B("configuration_id"), S("name"), B("value"), B("minimum"), B("maximum"), B("value_in_use"), I("is_dynamic"), I("is_advanced"), S("description") });
                    void Add(long id, string name, long value, long min, long max, bool dyn, string desc) =>
                        t.Rows.Add(new object?[] { id, name, value, min, max, value, dyn ? 1L : 0L, 0L, desc });
                    Add(100, "remote query timeout (s)", 600, 0, 2147483647, true, "remote query timeout");
                    Add(101, "user connections", 0, 0, 32767, false, "user connections");
                    Add(102, "max server memory (MB)", 2147483647, 16, 2147483647, true, "max server memory");
                    Add(103, "min server memory (MB)", 0, 0, 2147483647, true, "min server memory");
                    Add(104, "fill factor (%)", 0, 0, 100, false, "fill factor");
                    Add(105, "nested triggers", 1, 0, 1, true, "nested triggers");
                    Add(106, "transform noise words", 0, 0, 1, true, "transform noise words");
                    Add(107, "two digit year cutoff", 2049, 1753, 9999, false, "two digit year cutoff");
                    Add(108, "cost threshold for parallelism", 5, 0, 32767, true, "cost threshold for parallelism");
                    Add(109, "max degree of parallelism", 0, 0, 32767, true, "max degree of parallelism");
                    return t;
                }
                case "sys.dm_exec_sessions":
                {
                    var t = new Table("dm_exec_sessions", new()
                        { B("session_id"), S("login_time"), S("host_name"), S("program_name"), S("login_name"), S("status"), B("database_id"), I("is_user_process") });
                    t.Rows.Add(new object?[] { (long)ses.SessionId, stamp, "android-device", "MSSQL SERVER (unofficial)", "sa", "running", DatabaseIdOf(null, ses) ?? 1L, 1L });
                    return t;
                }
                case "sys.dm_exec_connections":
                {
                    var t = new Table("dm_exec_connections", new()
                        { B("session_id"), S("connect_time"), S("net_transport"), S("protocol_type"), S("auth_scheme"), S("encrypt_option") });
                    t.Rows.Add(new object?[] { (long)ses.SessionId, stamp, "TCP", "TSQL", "SQL", "FALSE" });
                    return t;
                }
                case "sys.dm_os_host_info":
                {
                    var t = new Table("dm_os_host_info", new()
                        { S("host_platform"), S("host_distribution"), S("host_release"), S("host_service_pack_level"), I("host_sku"), I("host_os_language_id") });
                    t.Rows.Add(new object?[] { "Linux", "Android", "15.0", "", 0L, 1033L });
                    return t;
                }
                case "sys.dm_os_sys_info":
                {
                    var t = new Table("dm_os_sys_info", new()
                        { I("cpu_count"), I("hyperthread_ratio"), B("physical_memory_kb"), B("committed_kb"), B("committed_target_kb"), I("scheduler_count"), I("socket_count") });
                    int cpus = Environment.ProcessorCount;
                    t.Rows.Add(new object?[] { (long)cpus, (long)cpus, 4194304L, 524288L, 524288L, (long)cpus, 1L });
                    return t;
                }
                case "information_schema.tables":
                {
                    var t = new Table("TABLES", new() { S("TABLE_CATALOG"), S("TABLE_SCHEMA"), S("TABLE_NAME"), S("TABLE_TYPE") });
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        t.Rows.Add(new object?[] { db.Name, "dbo", tb.Name, "BASE TABLE" });
                    return t;
                }
                case "information_schema.columns":
                {
                    var t = new Table("COLUMNS", new()
                    {
                        S("TABLE_CATALOG"), S("TABLE_SCHEMA"), S("TABLE_NAME"), S("COLUMN_NAME"),
                        I("ORDINAL_POSITION"), S("DATA_TYPE"), S("IS_NULLABLE")
                    });
                    foreach (var tb in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        for (int i = 0; i < tb.Columns.Count; i++)
                        {
                            var c = tb.Columns[i];
                            string type = c.Type switch
                            {
                                DataType.Int => "int", DataType.BigInt => "bigint", DataType.Bit => "bit",
                                DataType.Float => "float", _ => "nvarchar"
                            };
                            t.Rows.Add(new object?[] { db.Name, "dbo", tb.Name, c.Name, (long)(i + 1), type, c.NotNull ? "NO" : "YES" });
                        }
                    return t;
                }
                default: return null;
            }
        }

        // Stable synthetic ids: tables get 100 + position (ordered by name); databases 1-based.
        private static long TableObjectId(Database db, string tableName)
        {
            int i = 0;
            foreach (var t in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)) return 100 + i;
                i++;
            }
            return 100 + i;
        }

        private static (long SysTypeId, long MaxLength, long Precision, long Scale) SysTypeOf(DataType t, int len) => t switch
        {
            DataType.Int => (56, 4, 10, 0),
            DataType.BigInt => (127, 8, 19, 0),
            DataType.Bit => (104, 1, 1, 0),
            DataType.Float => (62, 8, 53, 0),
            _ => (231, len > 0 ? Math.Min(len * 2, 8000) : -1, 0, 0) // 231 = NVARCHAR
        };

        // ------------------------------------------------------------ id / name lookups for OBJECT_ID etc.

        internal bool DatabaseExists(string name) => databases.ContainsKey(name);

        internal long? DatabaseIdOf(string? name, SqlSession ses)
        {
            var ordered = databases.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            name ??= ses.Database;
            int i = ordered.FindIndex(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? null : i + 1;
        }

        internal string? DatabaseNameOf(long id)
        {
            var ordered = databases.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return id >= 1 && id <= ordered.Count ? ordered[(int)id - 1].Name : null;
        }

        internal long? ObjectIdOf(string objectName, SqlSession ses)
        {
            if (!databases.TryGetValue(ses.Database, out var db)) return null;
            int i = 0;
            foreach (var t in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (t.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) return 100 + i;
                i++;
            }
            return null;
        }

        internal string? ObjectNameOf(long id, SqlSession ses)
        {
            if (!databases.TryGetValue(ses.Database, out var db)) return null;
            int i = 0;
            foreach (var t in db.Tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (100 + i == id) return t.Name;
                i++;
            }
            return null;
        }

        // ---------------------------------------------------------------- DDL

        private StatementResult ExecCreateTable(CreateTable c, SqlSession ses)
        {
            var db = GetDatabase(c.Name.Database ?? ses.Database);
            if (IsSystemSchema(c.Name.Schema)) throw new SqlException(262, "CREATE TABLE permission denied in system schema.");
            if (db.Tables.ContainsKey(c.Name.Name))
                throw new SqlException(2714, $"There is already an object named '{c.Name.Name}' in the database.");
            var table = new Table(c.Name.Name, c.Columns);
            db.Tables[table.Name] = table;
            if (ses.Tx != null) ses.Tx.Created.Add((db, table));
            Persist(ses, db, table);
            return new StatementResult { Kind = StatementKind.Other };
        }

        private StatementResult ExecDropTable(DropTable d, SqlSession ses)
        {
            var db = GetDatabase(d.Name.Database ?? ses.Database);
            if (IsSystemSchema(d.Name.Schema) || !db.Tables.TryGetValue(d.Name.Name, out var table))
                throw new SqlException(3701, $"Cannot drop the table '{d.Name.Name}', because it does not exist or you do not have permission.");
            if (ses.Tx == null) db.Drop(table);
            else
            {
                db.Tables.Remove(table.Name);
                if (ses.Tx.Created.RemoveAll(x => ReferenceEquals(x.Table, table)) > 0) ses.Tx.Dirty.Remove(table);
                else { ses.Tx.Dirty.Remove(table); ses.Tx.Dropped.Add((db, table)); }
            }
            return new StatementResult { Kind = StatementKind.Other };
        }

        // ---------------------------------------------------------------- value coercion

        private static object? Coerce(object? v, ColumnDef col, string table)
        {
            if (v == null) return null;
            switch (col.Type)
            {
                case DataType.Int:
                case DataType.BigInt:
                {
                    var n = Evaluator.ToNumber(v);
                    long l = n is long x ? x : (double.IsFinite((double)n) ? (long)Math.Truncate((double)n) : throw Overflow(col));
                    if (col.Type == DataType.Int && (l < int.MinValue || l > int.MaxValue)) throw Overflow(col);
                    return l;
                }
                case DataType.Bit:
                    if (v is bool b) return b;
                    if (v is string s)
                    {
                        string t = s.Trim();
                        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return Convert.ToDouble(Evaluator.ToNumber(v), CultureInfo.InvariantCulture) != 0;
                case DataType.Float:
                    return Convert.ToDouble(Evaluator.ToNumber(v), CultureInfo.InvariantCulture);
                default:
                {
                    string str = Evaluator.ToStr(v);
                    if (col.Length > 0 && str.Length > col.Length)
                        throw new SqlException(8152, "String or binary data would be truncated.");
                    return str;
                }
            }
        }

        private static SqlException Overflow(ColumnDef col) =>
            new(8115, $"Arithmetic overflow error converting expression to data type {(col.Type == DataType.Int ? "int" : "bigint")}.");

        private static void CheckNotNull(object?[] row, Table t)
        {
            for (int i = 0; i < row.Length; i++)
                if (row[i] == null && t.Columns[i].NotNull)
                    throw new SqlException(515,
                        $"Cannot insert the value NULL into column '{t.Columns[i].Name}', table '{t.Name}'; column does not allow nulls.");
        }

        private static string KeyOf(object? v) => v switch
        {
            null => "\0N",
            string s => "s:" + s.TrimEnd(' ').ToLowerInvariant(),
            bool b => b ? "n:1" : "n:0",
            _ => "n:" + Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
        };

        private static void CheckPrimaryKey(Table t, IEnumerable<object?[]> rows)
        {
            var pk = t.Columns.Select((c, i) => (c, i)).Where(x => x.c.PrimaryKey).Select(x => x.i).ToArray();
            if (pk.Length == 0) return;
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                string key = string.Join("\u0001", pk.Select(i => KeyOf(row[i])));
                if (!seen.Add(key))
                    throw new SqlException(2627,
                        $"Violation of PRIMARY KEY constraint 'PK_{t.Name}'. Cannot insert duplicate key in object 'dbo.{t.Name}'. " +
                        $"The duplicate key value is ({string.Join(", ", pk.Select(i => Evaluator.ToStr(row[i]!)))}).");
            }
        }

        // ---------------------------------------------------------------- DML

        private RowContext EmptyContext(SqlSession ses) => new() { Engine = this, Session = ses };

        private StatementResult ExecInsert(Insert ins, SqlSession ses)
        {
            var table = ResolveTable(ins.Table, ses, out var db, forWrite: true);

            int[] map;
            if (ins.Columns == null)
                map = table.Columns.Select((c, i) => (c, i)).Where(x => !x.c.Identity).Select(x => x.i).ToArray();
            else
            {
                map = ins.Columns.Select(n =>
                {
                    int idx = table.ColumnIndex(n);
                    return idx >= 0 ? idx : throw new SqlException(207, $"Invalid column name '{n}'.");
                }).ToArray();
                if (map.Distinct().Count() != map.Length)
                    throw new SqlException(264, "The column name is specified more than once in the SET clause or column list of an INSERT.");
            }

            long identity = table.IdentityNext;
            var newRows = new List<object?[]>();
            var ctx = EmptyContext(ses);

            foreach (var exprs in ins.Rows)
            {
                if (exprs.Count != map.Length)
                    throw new SqlException(213, "Column name or number of supplied values does not match table definition.");

                var row = new object?[table.Columns.Count];
                for (int k = 0; k < map.Length; k++)
                {
                    var col = table.Columns[map[k]];
                    if (col.Identity)
                        throw new SqlException(544, $"Cannot insert explicit value for identity column in table '{table.Name}' when IDENTITY_INSERT is set to OFF.");
                    row[map[k]] = Coerce(Evaluator.Eval(exprs[k], ctx), col, table.Name);
                }
                for (int i = 0; i < table.Columns.Count; i++)
                    if (table.Columns[i].Identity) row[i] = identity++;

                CheckNotNull(row, table);
                newRows.Add(row);
            }

            CheckPrimaryKey(table, table.Rows.Concat(newRows));
            Touch(ses, db, table);
            table.Rows = new List<object?[]>(table.Rows.Concat(newRows));
            table.IdentityNext = identity;
            Persist(ses, db, table);
            return new StatementResult { Kind = StatementKind.Insert, RowCount = newRows.Count };
        }

        private static List<SchemaCol> SchemaOf(Table t, string alias) =>
            t.Columns.Select(c => new SchemaCol(alias, c.Name, c.Type, c.Length)).ToList();

        private StatementResult ExecUpdate(Update u, SqlSession ses)
        {
            var table = ResolveTable(u.Table, ses, out var db, forWrite: true);
            var schema = SchemaOf(table, table.Name);

            var sets = u.Sets.Select(s =>
            {
                int idx = table.ColumnIndex(s.Column);
                if (idx < 0) throw new SqlException(207, $"Invalid column name '{s.Column}'.");
                if (table.Columns[idx].Identity)
                    throw new SqlException(8102, $"Cannot update identity column '{s.Column}'.");
                return (idx, s.Value);
            }).ToList();

            var result = new List<object?[]>(table.Rows.Count);
            long count = 0;
            foreach (var row in table.Rows)
            {
                var ctx = new RowContext { Schema = schema, Row = row, Engine = this, Session = ses };
                if (u.Where != null && !Evaluator.IsTrue(Evaluator.Eval(u.Where, ctx))) { result.Add(row); continue; }

                var copy = (object?[])row.Clone();
                foreach (var (idx, expr) in sets)
                    copy[idx] = Coerce(Evaluator.Eval(expr, ctx), table.Columns[idx], table.Name);
                CheckNotNull(copy, table);
                result.Add(copy);
                count++;
            }

            CheckPrimaryKey(table, result);
            Touch(ses, db, table);
            table.Rows = result;
            Persist(ses, db, table);
            return new StatementResult { Kind = StatementKind.Update, RowCount = count };
        }

        private StatementResult ExecDelete(Delete d, SqlSession ses)
        {
            var table = ResolveTable(d.Table, ses, out var db, forWrite: true);
            var schema = SchemaOf(table, table.Name);

            var kept = new List<object?[]>();
            foreach (var row in table.Rows)
            {
                var ctx = new RowContext { Schema = schema, Row = row, Engine = this, Session = ses };
                if (d.Where == null || Evaluator.IsTrue(Evaluator.Eval(d.Where, ctx))) continue;
                kept.Add(row);
            }
            long count = table.Rows.Count - kept.Count;
            Touch(ses, db, table);
            table.Rows = kept;
            Persist(ses, db, table);
            return new StatementResult { Kind = StatementKind.Delete, RowCount = count };
        }

        // ---------------------------------------------------------------- SELECT

        private sealed record OutItem(Expr Expression, string Name);

        private StatementResult ExecSelect(Select s, SqlSession ses)
        {
            var schema = new List<SchemaCol>();
            List<object?[]> rows;

            if (s.From == null)
                rows = new() { Array.Empty<object?>() };
            else
            {
                var table = ResolveTable(s.From.Name, ses, out _);
                schema.AddRange(SchemaOf(table, s.From.Alias));
                rows = table.Rows.ToList();

                foreach (var join in s.Joins)
                {
                    var right = ResolveTable(join.Table.Name, ses, out _);
                    var rs = SchemaOf(right, join.Table.Alias);
                    var combinedSchema = schema.Concat(rs).ToList();
                    var joined = new List<object?[]>();

                    foreach (var l in rows)
                    {
                        bool matched = false;
                        foreach (var r in right.Rows)
                        {
                            var combined = new object?[l.Length + r.Length];
                            l.CopyTo(combined, 0); r.CopyTo(combined, l.Length);
                            var ctx = new RowContext { Schema = combinedSchema, Row = combined, Engine = this, Session = ses };
                            if (Evaluator.IsTrue(Evaluator.Eval(join.On, ctx))) { joined.Add(combined); matched = true; }
                        }
                        if (!matched && join.Left)
                        {
                            var padded = new object?[l.Length + rs.Count];
                            l.CopyTo(padded, 0);
                            joined.Add(padded);
                        }
                    }
                    schema = combinedSchema;
                    rows = joined;
                }
            }

            RowContext Ctx(object?[] row, List<object?[]>? group = null) =>
                new() { Schema = schema, Row = row, Group = group, Engine = this, Session = ses };

            if (s.Where != null)
            {
                if (Evaluator.ContainsAggregate(s.Where))
                    throw new SqlException(147, "An aggregate may not appear in the WHERE clause unless it is in a subquery.");
                rows = rows.Where(r => Evaluator.IsTrue(Evaluator.Eval(s.Where, Ctx(r)))).ToList();
            }

            // Split "@var = expr" assignments from real output items, then expand the select list.
            var assignItems = s.Items.Where(i => i.AssignVar != null).ToList();
            var rawItems = s.Items.Where(i => i.AssignVar == null).ToList();

            var items = new List<OutItem>();
            foreach (var it in rawItems)
            {
                if (it.Star)
                {
                    bool any = false;
                    for (int i = 0; i < schema.Count; i++)
                    {
                        if (it.StarTable != null && !schema[i].Table.Equals(it.StarTable, StringComparison.OrdinalIgnoreCase)) continue;
                        items.Add(new OutItem(new ColIndex(i), schema[i].Name));
                        any = true;
                    }
                    if (!any) throw new SqlException(208, $"Invalid object name '{it.StarTable ?? "*"}'.");
                }
                else
                {
                    string name = it.Alias ?? (it.Expression is Col c ? c.Name : "");
                    items.Add(new OutItem(it.Expression!, name));
                }
            }

            bool aggregated = s.GroupBy.Count > 0 || s.Having != null
                || items.Any(i => Evaluator.ContainsAggregate(i.Expression))
                || assignItems.Any(i => Evaluator.ContainsAggregate(i.Expression!));

            var output = new List<(object?[] Values, object?[] Keys)>();

            object?[] OrderKeys(RowContext ctx, object?[] values)
            {
                var keys = new object?[s.OrderBy.Count];
                for (int k = 0; k < keys.Length; k++)
                {
                    var e = s.OrderBy[k].Expression;
                    if (e is Lit { Value: long pos })
                    {
                        if (pos < 1 || pos > values.Length)
                            throw new SqlException(108, "The ORDER BY position number is out of range of the number of items in the select list.");
                        keys[k] = values[pos - 1];
                    }
                    else if (e is Col { Table: null } oc && items.FindIndex(x => x.Name.Equals(oc.Name, StringComparison.OrdinalIgnoreCase)) is int ai and >= 0)
                        keys[k] = values[ai];
                    else
                        keys[k] = Evaluator.Eval(e, ctx);
                }
                return keys;
            }

            if (aggregated)
            {
                if (s.Items.Any(i => i.Star)) throw new SqlException(8120, "Column is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.");

                var groups = new List<List<object?[]>>();
                if (s.GroupBy.Count == 0) groups.Add(rows);
                else
                {
                    var index = new Dictionary<string, List<object?[]>>();
                    foreach (var r in rows)
                    {
                        var ctx = Ctx(r);
                        string key = string.Join("\u0001", s.GroupBy.Select(g => KeyOf(Evaluator.Eval(g, ctx))));
                        if (!index.TryGetValue(key, out var g)) { g = new(); index[key] = g; groups.Add(g); }
                        g.Add(r);
                    }
                }

                foreach (var g in groups)
                {
                    var rep = g.Count > 0 ? g[0] : new object?[schema.Count];
                    var ctx = Ctx(rep, g);
                    if (s.Having != null && !Evaluator.IsTrue(Evaluator.Eval(s.Having, ctx))) continue;
                    RunAssignments(assignItems, ctx);
                    if (items.Count == 0) continue;
                    var values = items.Select(i => Evaluator.Eval(i.Expression, ctx)).ToArray();
                    output.Add((values, OrderKeys(ctx, values)));
                }
            }
            else
            {
                foreach (var r in rows)
                {
                    var ctx = Ctx(r);
                    RunAssignments(assignItems, ctx);
                    if (items.Count == 0) continue;
                    var values = items.Select(i => Evaluator.Eval(i.Expression, ctx)).ToArray();
                    output.Add((values, OrderKeys(ctx, values)));
                }
            }

            // A select list made only of "@var = expr" assignments returns no result set
            // (T-SQL semantics); the variables keep the value of the last scanned row.
            if (items.Count == 0)
                return new StatementResult { Kind = StatementKind.Other, RowCount = rows.Count };

            if (s.Distinct)
            {
                var seen = new HashSet<string>();
                output = output.Where(o => seen.Add(string.Join("\u0001", o.Values.Select(KeyOf)))).ToList();
            }

            if (s.OrderBy.Count > 0)
            {
                IOrderedEnumerable<(object?[] Values, object?[] Keys)>? ordered = null;
                for (int k = 0; k < s.OrderBy.Count; k++)
                {
                    int kk = k;
                    var cmp = Comparer<object?>.Create((a, b) =>
                        a == null ? (b == null ? 0 : -1) : b == null ? 1 : Evaluator.Compare(a, b));
                    ordered = ordered == null
                        ? (s.OrderBy[k].Descending ? output.OrderByDescending(o => o.Keys[kk], cmp) : output.OrderBy(o => o.Keys[kk], cmp))
                        : (s.OrderBy[k].Descending ? ordered.ThenByDescending(o => o.Keys[kk], cmp) : ordered.ThenBy(o => o.Keys[kk], cmp));
                }
                output = ordered!.ToList();
            }

            if (s.Top.HasValue) output = output.Take(Math.Max(0, s.Top.Value)).ToList();

            // Build the result set with inferred column types.
            var rs2 = new ResultSet();
            for (int i = 0; i < items.Count; i++)
            {
                var (type, len) = InferType(items[i].Expression, schema, output.Select(o => o.Values[i]));
                rs2.Columns.Add(new ResultColumn(items[i].Name, type, len));
            }
            foreach (var o in output)
            {
                for (int i = 0; i < o.Values.Length; i++)
                {
                    var type = rs2.Columns[i].Type;
                    if (o.Values[i] is long l && type == DataType.Int && (l < int.MinValue || l > int.MaxValue))
                        throw new SqlException(8115, "Arithmetic overflow error converting expression to data type int.");
                    if (o.Values[i] != null && type != DataType.NVarChar && o.Values[i] is string)
                        throw new SqlException(245, "Conversion failed while building the result set.");
                }
                rs2.Rows.Add(o.Values);
            }
            return new StatementResult { Kind = StatementKind.Select, Result = rs2, RowCount = rs2.Rows.Count };
        }

        private static void RunAssignments(List<SelectItem> assignItems, RowContext ctx)
        {
            foreach (var a in assignItems)
                ctx.Session.Parameters[a.AssignVar!] = Evaluator.Eval(a.Expression!, ctx);
        }

        // ---------------------------------------------------------------- result typing

        private static (DataType Type, int Len) InferType(Expr e, List<SchemaCol> schema, IEnumerable<object?> values)
        {
            switch (e)
            {
                case ColIndex ci: return (schema[ci.Index].Type, schema[ci.Index].Length);
                case Col c: { var sc = schema[Evaluator.ResolveColumn(c, schema)]; return (sc.Type, sc.Length); }
                case Lit { Value: long l }: return (l >= int.MinValue && l <= int.MaxValue ? DataType.Int : DataType.BigInt, 0);
                case Func f when f.Name == "COUNT" || f.Name == "LEN": return (DataType.Int, 0);
                case Func f when f.Name == "SUM" && f.Args.Count == 1:
                    return InferType(f.Args[0], schema, values).Type == DataType.Float ? (DataType.Float, 0) : (DataType.BigInt, 0);
                case Func f when f.Name == "AVG": return (DataType.Float, 0);
                case Func f when (f.Name == "MIN" || f.Name == "MAX") && f.Args.Count == 1:
                    return InferType(f.Args[0], schema, values);
                case Bin b when b.Op is "+" or "-" or "*" or "/" or "%":
                {
                    var l = InferType(b.L, schema, Enumerable.Empty<object?>());
                    var r = InferType(b.R, schema, Enumerable.Empty<object?>());
                    if (l.Type == DataType.NVarChar && r.Type == DataType.NVarChar && b.Op == "+") break;
                    if (l.Type == DataType.Float || r.Type == DataType.Float) return (DataType.Float, 0);
                    if (l.Type == DataType.BigInt || r.Type == DataType.BigInt) return (DataType.BigInt, 0);
                    if (l.Type == DataType.NVarChar || r.Type == DataType.NVarChar) break;
                    return (DataType.Int, 0);
                }
            }

            var list = values.ToList();
            var first = list.FirstOrDefault(v => v != null);
            switch (first)
            {
                case long: return (list.OfType<long>().Any(x => x < int.MinValue || x > int.MaxValue) ? DataType.BigInt : DataType.Int, 0);
                case bool: return (DataType.Bit, 0);
                case double: return (DataType.Float, 0);
                default:
                    int max = list.OfType<string>().Select(x => x.Length).DefaultIfEmpty(1).Max();
                    return (DataType.NVarChar, Math.Clamp(max, 1, 4000));
            }
        }
    }
}
