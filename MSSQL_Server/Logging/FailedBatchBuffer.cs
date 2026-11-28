using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MSSQL_Server.Logging
{
    /// <summary>
    /// Keeps the most recent failing SQL batches in memory so they can be read back from SSMS
    /// (SELECT ... FROM sys.dm_failed_batches) and pasted for analysis. Diagnostic aid only.
    /// </summary>
    public static class FailedBatchBuffer
    {
        public const int Capacity = 25;

        public sealed record Entry(int Id, int ErrorNumber, string ErrorMessage, string Sql);

        private static readonly object Gate = new();
        private static readonly List<Entry> Items = new();
        private static int nextId = 1;

        public static void Add(int errorNumber, string errorMessage, string sql)
        {
            lock (Gate)
            {
                Items.Add(new Entry(nextId++, errorNumber, errorMessage ?? string.Empty, sql ?? string.Empty));
                if (Items.Count > Capacity) Items.RemoveAt(0);
            }
        }

        public static List<Entry> Snapshot()
        {
            lock (Gate) return new List<Entry>(Items);
        }
    }
}
