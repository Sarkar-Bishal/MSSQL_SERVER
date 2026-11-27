using MSSQL_Server.Sql;

namespace MSSQL_Server.Engine
{
    public sealed record ResultColumn(string Name, DataType Type, int MaxChars);

    public sealed class ResultSet
    {
        public List<ResultColumn> Columns { get; } = new();
        public List<object?[]> Rows { get; } = new();
    }

    public enum StatementKind { None, Select, Insert, Update, Delete, Other }

    public sealed class StatementResult
    {
        public StatementKind Kind { get; init; }
        public ResultSet? Result { get; init; }
        public long RowCount { get; init; }
        public string? NewDatabase { get; init; }
        public string? OldDatabase { get; init; }
    }

    public sealed class BatchResult
    {
        public List<StatementResult> Statements { get; } = new();
        public SqlException? Error { get; set; }
        /// <summary>Set by RETURN to stop executing the remaining statements of the batch.</summary>
        public bool Stopped { get; set; }
    }

    public sealed class SqlSession
    {
        public string Database { get; set; } = "master";
        public int SessionId { get; init; } = 51;
        internal TransactionState? Tx { get; set; }
        public int TranCount => Tx?.Depth ?? 0;
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Row count of the last finished statement (feeds @@ROWCOUNT). Declared scalar
        /// variables also live in Parameters ("@name" keys) for the whole session.</summary>
        public long LastRowCount { get; set; }

        /// <summary>Connection endpoints as reported by the TCP socket, feeding
        /// CONNECTIONPROPERTY (SSMS / SMO query these right after login).</summary>
        public string? ClientAddress { get; set; }
        public long ClientPort { get; set; }
        public string? LocalAddress { get; set; }
        public long LocalPort { get; set; }
    }
}
