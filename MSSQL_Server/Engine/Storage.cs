using System.Text;
using MSSQL_Server.Sql;

namespace MSSQL_Server.Engine
{
    internal sealed class Table
    {
        public string Name { get; }
        public List<ColumnDef> Columns { get; }
        public List<object?[]> Rows { get; set; } = new();
        public long IdentityNext { get; set; } = 1;

        public Table(string name, List<ColumnDef> columns) { Name = name; Columns = columns; }

        public int ColumnIndex(string name) =>
            Columns.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class TransactionState
    {
        public int Depth;
        public Dictionary<Table, (List<object?[]> Rows, long Identity)> Original = new();
        public Dictionary<Table, Database> Dirty = new();
        public List<(Database Db, Table Table)> Created = new();
        public List<(Database Db, Table Table)> Dropped = new();
    }

    /// <summary>
    /// A database is a directory with one file per table. Each save rewrites the table file
    /// atomically (temp file + rename). There is no write-ahead log or B-tree yet.
    /// </summary>
    internal sealed class Database
    {
        private const string Magic = "MSQT1";

        public string Name { get; }
        public string Directory { get; }
        public Dictionary<string, Table> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Database(string name, string directory)
        {
            Name = name;
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            foreach (var file in System.IO.Directory.GetFiles(directory, "*.tbl"))
            {
                try { var t = Load(file); Tables[t.Name] = t; }
                catch (Exception) { /* skip unreadable table file */ }
            }
        }

        private string FileFor(string table) =>
            Path.Combine(Directory, Convert.ToHexString(Encoding.UTF8.GetBytes(table.ToLowerInvariant())) + ".tbl");

        public void Save(Table table)
        {
            string path = FileFor(table.Name);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(table.Name);
                w.Write(table.IdentityNext);
                w.Write(table.Columns.Count);
                foreach (var c in table.Columns)
                {
                    w.Write(c.Name); w.Write((byte)c.Type); w.Write(c.Length);
                    w.Write(c.NotNull); w.Write(c.PrimaryKey); w.Write(c.Identity);
                }
                w.Write(table.Rows.Count);
                foreach (var row in table.Rows)
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        var v = row[i];
                        w.Write(v == null);
                        if (v == null) continue;
                        switch (table.Columns[i].Type)
                        {
                            case DataType.Int: case DataType.BigInt: w.Write((long)v); break;
                            case DataType.Bit: w.Write((bool)v); break;
                            case DataType.Float: w.Write((double)v); break;
                            default: w.Write((string)v); break;
                        }
                    }
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }

        private static Table Load(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadString() != Magic) throw new InvalidDataException("Bad table file.");
            string name = r.ReadString();
            long identity = r.ReadInt64();
            int cc = r.ReadInt32();
            var cols = new List<ColumnDef>(cc);
            for (int i = 0; i < cc; i++)
                cols.Add(new ColumnDef(r.ReadString(), (DataType)r.ReadByte(), r.ReadInt32(),
                                       r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean()));
            var table = new Table(name, cols) { IdentityNext = identity };
            int rc = r.ReadInt32();
            for (int n = 0; n < rc; n++)
            {
                var row = new object?[cc];
                for (int i = 0; i < cc; i++)
                {
                    if (r.ReadBoolean()) continue;
                    row[i] = cols[i].Type switch
                    {
                        DataType.Int or DataType.BigInt => r.ReadInt64(),
                        DataType.Bit => r.ReadBoolean(),
                        DataType.Float => r.ReadDouble(),
                        _ => r.ReadString()
                    };
                }
                table.Rows.Add(row);
            }
            return table;
        }

        public void Drop(Table table)
        {
            Tables.Remove(table.Name);
            DeleteFile(table);
        }

        public void DeleteFile(Table table)
        {
            string path = FileFor(table.Name);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
