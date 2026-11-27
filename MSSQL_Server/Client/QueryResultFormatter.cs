using System.Text;

namespace MSSQL_Server.Client
{
    /// <summary>Renders query results as aligned monospace text (SSMS "Results to Text" style).</summary>
    internal static class QueryResultFormatter
    {
        private const int MaxCellWidth = 48;

        public static string Format(RunOutcome outcome)
        {
            var sb = new StringBuilder();

            foreach (var block in outcome.Blocks)
            {
                if (sb.Length > 0) sb.AppendLine();
                if (block is ResultSetData rs) FormatResultSet(sb, rs);
                else sb.AppendLine(block.ToString());
            }

            if (outcome.Failed && outcome.ErrorText != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(outcome.ErrorText);
            }

            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"Completed in {outcome.Elapsed.TotalMilliseconds:N0} ms");
            sb.Append(outcome.Failed ? " - FAILED." : ".");
            return sb.ToString();
        }

        private static void FormatResultSet(StringBuilder sb, ResultSetData rs)
        {
            int widthCount = rs.Columns.Count;
            var widths = new int[widthCount];
            for (int c = 0; c < widthCount; c++)
                widths[c] = Math.Min(MaxCellWidth, Math.Max(4, rs.Columns[c].Length));

            foreach (var row in rs.Rows)
                for (int c = 0; c < row.Length && c < widthCount; c++)
                    widths[c] = Math.Max(widths[c], Math.Min(MaxCellWidth, row[c].Length));

            var header = new string[widthCount];
            for (int c = 0; c < widthCount; c++) header[c] = Clip(rs.Columns[c], widths[c]);
            sb.AppendLine(string.Join(" | ", header));
            sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));

            foreach (var row in rs.Rows)
            {
                var cells = new string[widthCount];
                for (int c = 0; c < widthCount; c++)
                    cells[c] = Clip(c < row.Length ? row[c] : string.Empty, widths[c]);
                sb.AppendLine(string.Join(" | ", cells));
            }

            if (rs.Truncated)
                sb.AppendLine($"... ({rs.TotalRows - rs.Rows.Count} more row(s) not shown)");
            sb.AppendLine($"({rs.TotalRows} row(s) returned)");
        }

        private static string Clip(string s, int width)
        {
            var one = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (one.Length > width) return one.Substring(0, Math.Max(1, width - 3)) + "...";
            return one.PadRight(width);
        }
    }
}
