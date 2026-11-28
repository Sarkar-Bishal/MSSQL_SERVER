using System.Text.RegularExpressions;
using MSSQL_Server.Sql;

namespace MSSQL_Server.Engine
{
    /// <summary>
    /// Shortcuts for fixed T-SQL scripts that SSMS / SMO send right after login and that the
    /// engine cannot parse generally (temp tables, INSERT ... EXEC xp_msver, CROSS APPLY (VALUES ...),
    /// scalar subqueries, QUOTENAME, CONCAT, bitwise ops on @@MICROSOFTVERSION ...).
    /// Instead of running the script, the answer SSMS expects is built directly.
    /// Add a new shortcut here whenever the server log shows another SSMS batch failing.
    /// </summary>
    internal static class SmoCompat
    {
        // The script starts with: create table #SVer(ID int, Name sysname, Internal_Value int, Value nvarchar(512))
        private static readonly Regex ServerInfoScript =
            new(@"create\s+table\s+#SVer\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // SSMS also sends a lone cleanup batch after a failed attempt: drop table #SVer
        private static readonly Regex DropSVer =
            new(@"^\s*drop\s+table\s+#SVer\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Server-settings script (declare @MasterPath ... exec xp_instance_regread ... 28-column SELECT)
        private static readonly Regex ServerPropsScript =
            new(@"AS\s+\[IsCaseSensitive\].*AS\s+\[IsFabricServer\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Returns true when the batch was answered here and must not be parsed.</summary>
        public static bool TryHandle(string sql, string serverName, string dataRoot, BatchResult result)
        {
            if (DropSVer.IsMatch(sql))
            {
                result.Statements.Add(new StatementResult { Kind = StatementKind.Other });
                return true;
            }

            if (ServerInfoScript.IsMatch(sql) &&
                sql.IndexOf("[Server_Name]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Statements.Add(BuildServerInfo(serverName));
                return true;
            }

            if (ServerPropsScript.IsMatch(sql) &&
                sql.IndexOf("xp_instance_regread", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Statements.Add(BuildServerProps(serverName, dataRoot));
                return true;
            }

            return false;
        }

        // The 28 columns of the final SELECT of SSMS's server-settings script, in order.
        private static StatementResult BuildServerProps(string serverName, string dataRoot)
        {
            var rs = new ResultSet();
            void N(string name, int max = 256) => rs.Columns.Add(new ResultColumn(name, DataType.NVarChar, max));
            void I(string name) => rs.Columns.Add(new ResultColumn(name, DataType.Int, 0));
            void Bt(string name) => rs.Columns.Add(new ResultColumn(name, DataType.Bit, 0));

            Bt("IsCaseSensitive"); I("MaxPrecision"); N("ErrorLogPath", 512); N("RootDirectory", 512);
            N("PathSeparator", 4); Bt("IsFullTextInstalled"); N("MasterDBLogPath", 512); N("MasterDBPath", 512);
            N("VersionString", 128); N("Edition", 128); N("ProductLevel", 128); Bt("IsSingleUser");
            I("EngineEdition"); N("Collation", 128); N("NetName", 128); Bt("IsClustered");
            N("ResourceVersionString", 128); N("ResourceLastUpdateDateTime", 64); I("CollationID");
            I("ComparisonStyle"); I("SqlCharSet"); N("SqlCharSetName", 128); I("SqlSortOrder");
            N("SqlSortOrderName", 128); N("BuildClrVersionString", 128); N("ComputerNamePhysicalNetBIOS", 128);
            Bt("IsPolyBaseInstalled"); Bt("IsFabricServer");

            rs.Rows.Add(new object?[]
            {
                false,                              // IsCaseSensitive ('a' <> 'A' is false: case-insensitive)
                38L,                                // MaxPrecision
                "",                                 // ErrorLogPath
                dataRoot,                           // RootDirectory
                "/",                                // PathSeparator (host platform is Linux)
                false,                              // IsFullTextInstalled
                dataRoot,                           // MasterDBLogPath
                dataRoot,                           // MasterDBPath
                "16.0.1000.0",                      // VersionString
                "MSSQL Server (unofficial)",        // Edition
                "RTM",                              // ProductLevel
                false,                              // IsSingleUser
                2L,                                 // EngineEdition
                "SQL_Latin1_General_CP1_CI_AS",     // Collation
                serverName,                         // NetName
                false,                              // IsClustered
                "16.00.1000",                       // ResourceVersionString
                null,                               // ResourceLastUpdateDateTime
                872468488L,                         // CollationID
                196609L,                            // ComparisonStyle
                1L,                                 // SqlCharSet
                "iso_1",                            // SqlCharSetName
                52L,                                // SqlSortOrder
                "nocase_iso",                       // SqlSortOrderName
                "v4.0.30319",                       // BuildClrVersionString
                serverName,                         // ComputerNamePhysicalNetBIOS
                false,                              // IsPolyBaseInstalled
                false                               // IsFabricServer
            });

            return new StatementResult { Kind = StatementKind.Select, Result = rs, RowCount = 1 };
        }

        // The 15 columns of the final SELECT in SSMS's server-information script, in order.
        private static StatementResult BuildServerInfo(string serverName)
        {
            var rs = new ResultSet();
            rs.Columns.Add(new ResultColumn("Server_Name", DataType.NVarChar, 128));
            rs.Columns.Add(new ResultColumn("Server_Urn", DataType.NVarChar, 300));
            rs.Columns.Add(new ResultColumn("Server_ServerType", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("Server_Status", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("Server_IsContainedAuthentication", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("VersionMajor", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("VersionMinor", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("BuildNumber", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("IsSingleUser", DataType.Bit, 0));
            rs.Columns.Add(new ResultColumn("Edition", DataType.NVarChar, 128));
            rs.Columns.Add(new ResultColumn("EngineEdition", DataType.Int, 0));
            rs.Columns.Add(new ResultColumn("IsXTPSupported", DataType.Bit, 0));
            rs.Columns.Add(new ResultColumn("VersionString", DataType.NVarChar, 128));
            rs.Columns.Add(new ResultColumn("HostPlatform", DataType.NVarChar, 256));
            rs.Columns.Add(new ResultColumn("IsFullTextInstalled", DataType.Bit, 0));

            // QUOTENAME(name, '''') => 'name' with embedded quotes doubled
            string urn = "Server[@Name='" + serverName.Replace("'", "''") + "']";

            rs.Rows.Add(new object?[]
            {
                serverName,                      // Server_Name
                urn,                             // Server_Urn
                null,                            // Server_ServerType  (CAST(null AS int))
                1L,                              // Server_Status      (0x0001)
                0L,                              // Server_IsContainedAuthentication
                16L,                             // VersionMajor       (matches SERVERPROPERTY ProductVersion 16.0.1000.0)
                0L,                              // VersionMinor
                1000L,                           // BuildNumber
                false,                           // IsSingleUser
                "MSSQL Server (unofficial)",     // Edition            (same as SERVERPROPERTY('Edition'))
                2L,                              // EngineEdition      (same as SERVERPROPERTY('EngineEdition'))
                false,                           // IsXTPSupported
                "16.0.1000.0",                   // VersionString
                "Linux",                         // HostPlatform       (Android is Linux-based)
                false                            // IsFullTextInstalled
            });

            return new StatementResult { Kind = StatementKind.Select, Result = rs, RowCount = 1 };
        }
    }
}