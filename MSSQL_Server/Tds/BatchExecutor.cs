using Android.Util;
using MSSQL_Server.Configuration;
using MSSQL_Server.Engine;
using MSSQL_Server.Logging;

namespace MSSQL_Server.Tds
{
    /// <summary>Runs a SQL batch on the engine and encodes the outcome as a TDS token stream.</summary>
    internal static class BatchExecutor
    {
        public static byte[] Execute(string sql, SqlEngine engine, SqlSession session, ServerConfiguration cfg, bool rpc = false, ServerLogger? log = null)
        {
            byte doneTok = rpc ? TdsTokens.DoneInProc : TdsTokens.Done;
            var result = engine.ExecuteBatch(sql, session);
            if (result.Error != null)
            {
                log?.Warn($"SQL error {result.Error.Number}: {result.Error.Message} | batch: " +
                          (sql.Length > 50000 ? sql[..50000] + "..." : sql));
                FailedBatchBuffer.Add(result.Error.Number, result.Error.Message, sql);   // <- new
            }
            var tb = new TokenBuilder();
            int n = result.Statements.Count;

            for (int i = 0; i < n; i++)
            {
                var st = result.Statements[i];
                ushort more = (rpc || i < n - 1 || result.Error != null) ? TdsTokens.DoneMore : (ushort)0;

                switch (st.Kind)
                {
                    case StatementKind.Select:
                        tb.Bytes(TdsTokens.ResultSetTokens(st.Result!));
                        tb.Bytes(TdsTokens.DoneToken((ushort)(TdsTokens.DoneCount | more), 0x00C1, (ulong)st.RowCount, doneTok));
                        break;
                    case StatementKind.Insert:
                        tb.Bytes(TdsTokens.DoneToken((ushort)(TdsTokens.DoneCount | more), 0x00C3, (ulong)st.RowCount, doneTok));
                        break;
                    case StatementKind.Delete:
                        tb.Bytes(TdsTokens.DoneToken((ushort)(TdsTokens.DoneCount | more), 0x00C4, (ulong)st.RowCount, doneTok));
                        break;
                    case StatementKind.Update:
                        tb.Bytes(TdsTokens.DoneToken((ushort)(TdsTokens.DoneCount | more), 0x00C5, (ulong)st.RowCount, doneTok));
                        break;
                    default:
                        if (st.NewDatabase != null)
                            tb.Bytes(TdsTokens.EnvChangeToken(1, st.NewDatabase, st.OldDatabase ?? string.Empty));
                        tb.Bytes(TdsTokens.DoneToken(more, 0, 0, doneTok));
                        break;
                }
            }

            if (result.Error != null)
                tb.Bytes(TdsTokens.ErrorToken(result.Error.Number, 1, result.Error.Severity,
                    result.Error.Message, cfg.ServerName));

            if (rpc)
            {
                tb.Bytes(TdsTokens.ReturnStatusToken(result.Error == null ? 0 : -6));
                tb.Bytes(TdsTokens.DoneToken(result.Error == null ? TdsTokens.DoneFinal : TdsTokens.DoneError, 0, 0, TdsTokens.DoneProc));
            }
            else if (result.Error != null)
                tb.Bytes(TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0));
            else if (n == 0)
                tb.Bytes(TdsTokens.DoneToken(TdsTokens.DoneFinal, 0, 0));

            return tb.ToArray();
        }
    }
}
