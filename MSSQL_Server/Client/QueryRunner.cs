using System.Data.Common;
using System.Diagnostics;
using System.Text;

namespace MSSQL_Server.Client
{
    internal sealed class ResultSetData
    {
        public List<string> Columns { get; } = new();
        public List<string[]> Rows { get; } = new();
        public bool Truncated { get; set; }
        public long TotalRows { get; set; }
    }

    internal sealed class RunOutcome
    {
        /// <summary>Ordered blocks: ResultSetData instances or plain message strings.</summary>
        public List<object> Blocks { get; } = new();
        public bool Failed { get; set; }
        public string? ErrorText { get; set; }
        public int RowsAffected { get; set; }
        public int ResultSets { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Runs an SSMS-style script (batches separated by lines containing only GO) on one open
    /// connection through System.Data.Common, so it works identically with both driver stacks
    /// and with the app's own embedded engine (via the local TDS server).
    /// </summary>
    internal static class QueryRunner
    {
        private const int MaxCaptureRows = 500;
        private const int CommandTimeoutSeconds = 30;

        public static List<string> SplitBatches(string script)
        {
            var batches = new List<string>();
            var current = new StringBuilder();

            foreach (var rawLine in (script ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (rawLine.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    Flush(current, batches);
                }
                else
                {
                    current.AppendLine(rawLine);
                }
            }
            Flush(current, batches);
            return batches;
        }

        private static void Flush(StringBuilder current, List<string> batches)
        {
            if (current.Length > 0)
            {
                batches.Add(current.ToString());
                current.Clear();
            }
        }

        public static RunOutcome Execute(SqlClientAdapter adapter, DbConnection connection, string script)
        {
            var outcome = new RunOutcome();
            var sw = Stopwatch.StartNew();

            try
            {
                var batches = SplitBatches(script);
                if (batches.Count == 0)
                {
                    outcome.Blocks.Add("(nothing to run - the script is empty)");
                    return outcome;
                }

                foreach (var batch in batches)
                {
                    using var command = adapter.CreateCommand(batch, connection);
                    command.CommandTimeout = CommandTimeoutSeconds;
                    using var reader = command.ExecuteReader();

                    do
                    {
                        if (reader.FieldCount > 0)
                        {
                            CaptureResultSet(reader, outcome);
                        }
                        else if (reader.RecordsAffected >= 0)
                        {
                            outcome.RowsAffected += reader.RecordsAffected;
                            outcome.Blocks.Add($"({reader.RecordsAffected} row(s) affected)");
                        }
                    } while (reader.NextResult());
                }
            }
            catch (Exception ex)
            {
                outcome.Failed = true;
                outcome.ErrorText = adapter.DescribeException(ex);
            }
            finally
            {
                outcome.Elapsed = sw.Elapsed;
            }

            return outcome;
        }

        private static void CaptureResultSet(DbDataReader reader, RunOutcome outcome)
        {
            var rs = new ResultSetData();
            outcome.ResultSets++;

            for (int c = 0; c < reader.FieldCount; c++)
                rs.Columns.Add(reader.GetName(c));

            while (reader.Read())
            {
                rs.TotalRows++;
                if (rs.Rows.Count < MaxCaptureRows)
                {
                    var cells = new string[reader.FieldCount];
                    for (int c = 0; c < reader.FieldCount; c++)
                        cells[c] = reader.IsDBNull(c) ? "NULL" : FormatValue(reader.GetValue(c));
                    rs.Rows.Add(cells);
                }
                else
                {
                    rs.Truncated = true;
                }
            }

            outcome.Blocks.Add(rs);
        }

        private static string FormatValue(object value)
        {
            if (value is byte[] bytes)
                return $"0x{Convert.ToHexString(bytes[..Math.Min(bytes.Length, 8)])} ({bytes.Length} bytes)";
            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
            if (value is bool b)
                return b ? "1" : "0";

            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
