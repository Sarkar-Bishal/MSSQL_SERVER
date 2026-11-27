using System.Data.Common;

namespace MSSQL_Server.Client
{
    /// <summary>
    /// Parses and adjusts a normal SQL Server connection string, exactly like a PC client would use:
    ///   Data Source=.\SQLExpress;Initial Catalog=YourDB;Persist Security Info=True;User Id=sa;Password=...
    /// "Data Source" accepts everything SqlClient accepts:
    ///   .\SQLEXPRESS   (local instance on the machine the app runs on)
    ///   (local), localhost, HOST\INSTANCE
    ///   192.168.1.10   or   192.168.1.10,1433   (ip,port)   or   HOST\INSTANCE,PORT
    /// </summary>
    public sealed class ConnectionStringInfo
    {
        public const string EncryptFromConnection = "auto";
        public const string EncryptOff = "false";
        public const string EncryptOn = "true";

        public string Raw { get; }
        public string? Server { get; private set; }
        public string? Database { get; private set; }
        public string? UserId { get; private set; }
        public bool IntegratedSecurity { get; private set; }
        public string? ParseError { get; private set; }

        private ConnectionStringInfo(string raw)
        {
            Raw = raw;
        }

        public static ConnectionStringInfo Parse(string raw)
        {
            var info = new ConnectionStringInfo(raw ?? string.Empty);

            if (string.IsNullOrWhiteSpace(info.Raw))
            {
                info.ParseError = "The connection string is empty.";
                return info;
            }

            try
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = info.Raw };
                info.Server = GetString(builder, "Data Source", "Server", "Address", "Addr", "Network Address");
                info.Database = GetString(builder, "Initial Catalog", "Database");
                info.UserId = GetString(builder, "User ID", "UID");
                info.IntegratedSecurity = IsTrue(GetString(builder, "Integrated Security", "Trusted_Connection"));

                if (info.Server == null)
                    info.ParseError =
                        "'Data Source' (the server) is missing - e.g. Data Source=.\\SQLExpress or Data Source=192.168.1.10,1433.";
            }
            catch (ArgumentException ex)
            {
                info.ParseError = ex.Message;
            }

            return info;
        }

        /// <summary>
        /// Builds the string actually handed to the driver: the user's string plus the optional
        /// UI additions (Encrypt / TrustServerCertificate). Values already present in the string
        /// always win - additions are only appended when the keyword is missing.
        /// </summary>
        public string BuildEffective(bool trustServerCertificate, string encryptChoice, out string appliedNote)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = Raw };
            var notes = new List<string>();

            if (encryptChoice != EncryptFromConnection)
            {
                if (builder.ContainsKey("Encrypt"))
                {
                    notes.Add($"Encrypt already set in string ({builder["Encrypt"]}) - kept");
                }
                else
                {
                    builder["Encrypt"] = encryptChoice;
                    notes.Add($"Encrypt={encryptChoice} added");
                }
            }

            if (trustServerCertificate && !builder.ContainsKey("TrustServerCertificate"))
            {
                builder["TrustServerCertificate"] = "true";
                notes.Add("TrustServerCertificate=True added");
            }

            appliedNote = notes.Count == 0
                ? "string used exactly as entered"
                : string.Join("; ", notes);

            return builder.ConnectionString;
        }

        /// <summary>Short human summary for the status line (never contains the password).</summary>
        public string Summary(string driverName)
        {
            if (ParseError != null) return "INVALID: " + ParseError;

            string auth = IntegratedSecurity
                ? "Integrated/Windows auth (usually NOT available from Android - use User Id/Password)"
                : $"SQL auth (User Id: {UserId ?? "?"})";

            return $"Server: {Server ?? "?"} | Database: {Database ?? "(server default)"} | {auth} | Driver: {driverName}";
        }

        private static string? GetString(DbConnectionStringBuilder builder, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (builder.TryGetValue(key, out var value) && value is string s && s.Trim().Length > 0)
                    return s.Trim();
            }
            return null;
        }

        private static bool IsTrue(string? value) =>
            value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                              || value.Equals("sspi", StringComparison.OrdinalIgnoreCase)
                              || value == "1");
    }
}
