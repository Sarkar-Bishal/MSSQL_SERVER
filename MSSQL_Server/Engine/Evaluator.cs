using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MSSQL_Server.Sql;

namespace MSSQL_Server.Engine
{
    internal sealed record SchemaCol(string Table, string Name, DataType Type, int Length);
    internal sealed record ColIndex(int Index) : Expr;

    internal sealed class RowContext
    {
        public List<SchemaCol> Schema { get; init; } = new();
        public object?[] Row { get; init; } = Array.Empty<object?>();
        public List<object?[]>? Group { get; init; }
        public SqlEngine Engine { get; init; } = null!;
        public SqlSession Session { get; init; } = null!;
    }

    internal static class Evaluator
    {
        private static readonly HashSet<string> Aggregates = new() { "COUNT", "SUM", "MIN", "MAX", "AVG" };

        public static bool ContainsAggregate(Expr e) => e switch
        {
            Func f => Aggregates.Contains(f.Name) || f.Args.Any(ContainsAggregate),
            Bin b => ContainsAggregate(b.L) || ContainsAggregate(b.R),
            Un u => ContainsAggregate(u.E),
            IsNullExpr n => ContainsAggregate(n.E),
            LikeExpr l => ContainsAggregate(l.E) || ContainsAggregate(l.Pattern),
            InExpr i => ContainsAggregate(i.E) || i.List.Any(ContainsAggregate),
            BetweenExpr b => ContainsAggregate(b.E) || ContainsAggregate(b.Lo) || ContainsAggregate(b.Hi),
            CaseExpr c => (c.Input != null && ContainsAggregate(c.Input))
                          || c.Whens.Any(w => ContainsAggregate(w.When) || ContainsAggregate(w.Then))
                          || (c.Else != null && ContainsAggregate(c.Else)),
            ExistsExpr => false,
            SubqueryExpr => false,
            InSubqueryExpr => false,
            _ => false
        };

        public static bool IsTrue(object? v) => v switch
        {
            null => false,
            bool b => b,
            _ => throw new SqlException(4145, "An expression of non-boolean type specified in a context where a condition is expected.")
        };

        public static int ResolveColumn(Col c, List<SchemaCol> schema)
        {
            int found = -1;
            for (int i = 0; i < schema.Count; i++)
            {
                if (!string.Equals(schema[i].Name, c.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Table != null && !string.Equals(schema[i].Table, c.Table, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) throw new SqlException(209, $"Ambiguous column name '{c.Name}'.");
                found = i;
            }
            if (found < 0)
                throw new SqlException(207, $"Invalid column name '{c.Name}'.");
            return found;
        }

        public static object? Eval(Expr e, RowContext ctx)
        {
            switch (e)
            {
                case Lit l: return l.Value;
                case ColIndex ci: return ctx.Row[ci.Index];
                case Col c: return ctx.Row[ResolveColumn(c, ctx.Schema)];
                case Var v: return Variable(v.Name, ctx);
                case Un u: return EvalUnary(u, ctx);
                case Bin b: return EvalBinary(b, ctx);
                case IsNullExpr n: return (Eval(n.E, ctx) == null) != n.Not;
                case LikeExpr lk:
                    {
                        var s = Eval(lk.E, ctx); var pat = Eval(lk.Pattern, ctx);
                        if (s == null || pat == null) return null;
                        bool m = LikeToRegex(ToStr(pat)).IsMatch(ToStr(s));
                        return m != lk.Not;
                    }
                case InExpr ie:
                    {
                        var v = Eval(ie.E, ctx);
                        if (v == null) return null;
                        bool sawNull = false;
                        foreach (var item in ie.List)
                        {
                            var x = Eval(item, ctx);
                            if (x == null) { sawNull = true; continue; }
                            if (Compare(v, x) == 0) return !ie.Not;
                        }
                        return sawNull ? null : ie.Not;
                    }
                case InSubqueryExpr insq:
                    {
                        var left = Eval(insq.E, ctx);
                        var inner = ctx.Engine.ExecSelect(insq.Subquery, ctx.Session);
                        if (inner.Result == null || inner.Result.Rows.Count == 0) return !insq.Not;
                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var row in inner.Result.Rows)
                        {
                            var v = row[0];
                            set.Add(v == null ? "\0N"
                                : v is string s ? "s:" + s.TrimEnd(' ').ToLowerInvariant()
                                : "n:" + Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                        }
                        string key = left == null ? "\0N"
                            : left is string ls ? "s:" + ls.TrimEnd(' ').ToLowerInvariant()
                            : "n:" + Convert.ToDouble(left, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                        bool hit = set.Contains(key);
                        return insq.Not ? !hit : hit;
                    }
                case BetweenExpr be:
                    {
                        var v = Eval(be.E, ctx); var lo = Eval(be.Lo, ctx); var hi = Eval(be.Hi, ctx);
                        if (v == null || lo == null || hi == null) return null;
                        bool r = Compare(v, lo) >= 0 && Compare(v, hi) <= 0;
                        return r != be.Not;
                    }
                case CaseExpr ce:
                    {
                        object? input = ce.Input != null ? Eval(ce.Input, ctx) : null;
                        foreach (var (w, then) in ce.Whens)
                        {
                            if (ce.Input == null)
                            {
                                if (IsTrue(Eval(w, ctx))) return Eval(then, ctx);
                            }
                            else
                            {
                                var wv = Eval(w, ctx);
                                if (input != null && wv != null && Compare(input, wv) == 0) return Eval(then, ctx);
                            }
                        }
                        return ce.Else != null ? Eval(ce.Else, ctx) : null;
                    }
                case ExistsExpr xe: return ctx.Engine.ExecExists(xe.Select, ctx.Session);
                case SubqueryExpr sq:
                    {
                        var inner = ctx.Engine.ExecSelect(sq.Select, ctx.Session);
                        if (inner.Result == null || inner.Result.Rows.Count == 0) return null;
                        if (inner.Result.Rows.Count > 1)
                            throw new SqlException(512, "Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <= , >, >= or when the subquery is used as an expression.");
                        return inner.Result.Rows[0][0];
                    }
                case Func f: return EvalFunction(f, ctx);
                default: throw new SqlException(102, "Unsupported expression.", 15);
            }
        }

        private static object? Variable(string name, RowContext ctx)
        {
            if (!name.StartsWith("@@") && ctx.Session.Parameters.TryGetValue(name, out var pv)) return pv;
            return SystemVariable(name, ctx);
        }

        private static object? SystemVariable(string name, RowContext ctx) =>
            name.ToUpperInvariant() switch
            {
                "@@VERSION" => SqlEngine.VersionText,
                "@@SERVERNAME" => ctx.Engine.ServerName,
                "@@SPID" => (long)ctx.Session.SessionId,
                "@@LANGUAGE" => "us_english",
                "@@LANGCID" => 1033L,
                "@@TRANCOUNT" => (long)ctx.Session.TranCount,
                "@@ROWCOUNT" => ctx.Session.LastRowCount,
                "@@ERROR" => 0L,
                "@@DATEFIRST" => 7L,
                "@@MAX_PRECISION" => 38L,
                "@@MICROSOFTVERSION" => 268435456L,
                "@@TOTAL_ERRORS" => 0L,
                _ => throw new SqlException(137, $"Must declare the scalar variable \"{name}\".")
            };

        private static object? EvalUnary(Un u, RowContext ctx)
        {
            var v = Eval(u.E, ctx);
            if (v == null) return null;
            if (u.Op == "NOT") return v is bool b ? !b : throw new SqlException(4145, "An expression of non-boolean type specified in a context where a condition is expected.");
            var n = ToNumber(v);
            if (u.Op == "~") return n is long bw ? ~bw : ~Convert.ToInt64(n, CultureInfo.InvariantCulture);
            return n is long sg ? -sg : -(double)n;
        }

        private static object? EvalBinary(Bin b, RowContext ctx)
        {
            if (b.Op == "AND")
            {
                var l = AsBool(Eval(b.L, ctx));
                if (l == false) return false;
                var r = AsBool(Eval(b.R, ctx));
                if (r == false) return false;
                return l == null || r == null ? null : true;
            }
            if (b.Op == "OR")
            {
                var l = AsBool(Eval(b.L, ctx));
                if (l == true) return true;
                var r = AsBool(Eval(b.R, ctx));
                if (r == true) return true;
                return l == null || r == null ? null : false;
            }

            var left = Eval(b.L, ctx);
            var right = Eval(b.R, ctx);
            if (left == null || right == null) return null;

            switch (b.Op)
            {
                case "=": return Compare(left, right) == 0;
                case "<>": return Compare(left, right) != 0;
                case "<": return Compare(left, right) < 0;
                case ">": return Compare(left, right) > 0;
                case "<=": return Compare(left, right) <= 0;
                case ">=": return Compare(left, right) >= 0;
                case "+" when left is string ls && right is string rs: return ls + rs;
            }

            var x = ToNumber(left); var y = ToNumber(right);

            if (b.Op is "&" or "|" or "^")
            {
                long la = Convert.ToInt64(x, CultureInfo.InvariantCulture);
                long lc = Convert.ToInt64(y, CultureInfo.InvariantCulture);
                return b.Op switch { "&" => la & lc, "|" => la | lc, _ => la ^ lc };
            }

            if (x is long a && y is long c)
            {
                try
                {
                    checked
                    {
                        switch (b.Op)
                        {
                            case "+": return a + c;
                            case "-": return a - c;
                            case "*": return a * c;
                            case "/": if (c == 0) throw DivZero(); return a / c;
                            case "%": if (c == 0) throw DivZero(); return a % c;
                        }
                    }
                }
                catch (OverflowException)
                {
                    throw new SqlException(8115, "Arithmetic overflow error converting expression to data type bigint.");
                }
            }
            double da = Convert.ToDouble(x, CultureInfo.InvariantCulture);
            double db = Convert.ToDouble(y, CultureInfo.InvariantCulture);
            return b.Op switch
            {
                "+" => da + db,
                "-" => da - db,
                "*" => da * db,
                "/" => db == 0 ? throw DivZero() : da / db,
                "%" => db == 0 ? throw DivZero() : da % db,
                _ => throw new SqlException(102, $"Incorrect syntax near '{b.Op}'.", 15)
            };
        }

        private static SqlException DivZero() => new(8134, "Divide by zero error encountered.");

        private static bool? AsBool(object? v) => v switch
        {
            null => null,
            bool b => b,
            _ => throw new SqlException(4145, "An expression of non-boolean type specified in a context where a condition is expected.")
        };

        public static object ToNumber(object v)
        {
            switch (v)
            {
                case long or double: return v;
                case bool b: return b ? 1L : 0L;
                case string s:
                    if (long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                    if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                    throw new SqlException(245, $"Conversion failed when converting the varchar value '{s}' to data type int.");
                default: throw new SqlException(8114, "Error converting data type.");
            }
        }

        public static string ToStr(object v) => v switch
        {
            string s => s,
            bool b => b ? "1" : "0",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };

        public static int Compare(object a, object b)
        {
            if (a is string sa && b is string sb)
                return string.Compare(sa.TrimEnd(' '), sb.TrimEnd(' '), StringComparison.OrdinalIgnoreCase);
            var x = ToNumber(a); var y = ToNumber(b);
            if (x is long lx && y is long ly) return lx.CompareTo(ly);
            return Convert.ToDouble(x, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(y, CultureInfo.InvariantCulture));
        }

        public static object? CastValue(object? v, DataType type, int len)
        {
            if (v == null) return null;
            switch (type)
            {
                case DataType.Int:
                case DataType.BigInt:
                    {
                        var n = ToNumber(v);
                        long l = n is long x ? x : (long)Math.Truncate((double)n);
                        if (type == DataType.Int && (l < int.MinValue || l > int.MaxValue))
                            throw new SqlException(8115, "Arithmetic overflow error converting expression to data type int.");
                        return l;
                    }
                case DataType.Bit:
                    return Convert.ToDouble(ToNumber(v), CultureInfo.InvariantCulture) != 0;
                case DataType.Float:
                    return Convert.ToDouble(ToNumber(v), CultureInfo.InvariantCulture);
                default:
                    {
                        string s = ToStr(v);
                        if (len > 0 && s.Length > len) s = s.Substring(0, len);
                        return s;
                    }
            }
        }

        private static DataType ParseDataTypeName(string? s) =>
            Enum.TryParse<DataType>(s, ignoreCase: true, out var dt) ? dt : DataType.NVarChar;

        private static Regex LikeToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char c in pattern)
                sb.Append(c switch { '%' => ".*", '_' => ".", _ => Regex.Escape(c.ToString()) });
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        private static object? EvalFunction(Func f, RowContext ctx)
        {
            if (Aggregates.Contains(f.Name)) return EvalAggregate(f, ctx);

            object? Arg(int i) => i < f.Args.Count ? Eval(f.Args[i], ctx) : throw ArgCount(f);

            switch (f.Name)
            {
                case "QUOTENAME":
                    {
                        var qs = Arg(0);
                        if (qs == null) return null;
                        string qstr = ToStr(qs);
                        var qarg = f.Args.Count > 1 ? Arg(1) : null;
                        string qchars = qarg == null ? "[" : ToStr(qarg);
                        if (qchars.Length != 1 || qstr.Length > 128) return null;
                        char qc = qchars[0];
                        string? quoted = qc switch
                        {
                            '[' or ']' => "[" + qstr.Replace("]", "]]") + "]",
                            '(' or ')' => "(" + qstr + ")",
                            '<' or '>' => "<" + qstr + ">",
                            '{' or '}' => "{" + qstr + "}",
                            '\'' or '"' or '`' => qc + qstr.Replace(qc.ToString(), qc.ToString() + qc) + qc,
                            _ => null
                        };
                        return quoted;
                    }
                case "IS_SRVROLEMEMBER":
                    {
                        var role = Arg(0);
                        if (role == null) return null;
                        string rn = ToStr(role).ToLowerInvariant();
                        if (rn is "sysadmin" or "securityadmin" or "serveradmin" or "setupadmin"
                               or "processadmin" or "diskadmin" or "dbcreator" or "bulkadmin")
                            return 1L;   // the only login is 'sa', a sysadmin
                        return null;
                    }
                case "UPPER": { var v = Arg(0); return v == null ? null : ToStr(v).ToUpperInvariant(); }
                case "LOWER": { var v = Arg(0); return v == null ? null : ToStr(v).ToLowerInvariant(); }
                case "LTRIM": { var v = Arg(0); return v == null ? null : ToStr(v).TrimStart(); }
                case "RTRIM": { var v = Arg(0); return v == null ? null : ToStr(v).TrimEnd(); }
                case "LEN": { var v = Arg(0); return v == null ? null : (long)ToStr(v).TrimEnd(' ').Length; }
                case "ABS":
                    {
                        var v = Arg(0); if (v == null) return null;
                        var n = ToNumber(v);
                        return n is long l ? Math.Abs(l) : Math.Abs((double)n);
                    }
                case "CEILING":
                    {
                        var v = Arg(0); if (v == null) return null;
                        var n = ToNumber(v);
                        return n is long l ? l : (long)Math.Ceiling((double)n);
                    }
                case "FLOOR":
                    {
                        var v = Arg(0); if (v == null) return null;
                        var n = ToNumber(v);
                        return n is long l ? l : (long)Math.Floor((double)n);
                    }
                case "ISNULL": { var v = Arg(0); return v ?? Arg(1); }
                case "COALESCE":
                    foreach (var a in f.Args) { var v = Eval(a, ctx); if (v != null) return v; }
                    return null;
                case "REPLACE":
                    {
                        var s = Arg(0); var from = Arg(1); var to = Arg(2);
                        if (s == null || from == null || to == null) return null;
                        string fs = ToStr(from);
                        return fs.Length == 0 ? ToStr(s) : ToStr(s).Replace(fs, ToStr(to), StringComparison.OrdinalIgnoreCase);
                    }
                case "SUBSTRING":
                    {
                        var s = Arg(0); var st = Arg(1); var ln = Arg(2);
                        if (s == null || st == null || ln == null) return null;
                        string str = ToStr(s);
                        long start = Convert.ToInt64(ToNumber(st)) - 1, len = Convert.ToInt64(ToNumber(ln));
                        if (len < 0) throw new SqlException(536, "Invalid length parameter passed to the substring function.");
                        if (start < 0) { len += start; start = 0; }
                        if (len <= 0 || start >= str.Length) return "";
                        return str.Substring((int)start, (int)Math.Min(len, str.Length - start));
                    }
                case "LEFT":
                    {
                        var s = Arg(0); var n = Arg(1);
                        if (s == null || n == null) return null;
                        long k = Convert.ToInt64(ToNumber(n));
                        string str = ToStr(s);
                        return k <= 0 ? "" : str.Substring(0, (int)Math.Min(k, str.Length));
                    }
                case "RIGHT":
                    {
                        var s = Arg(0); var n = Arg(1);
                        if (s == null || n == null) return null;
                        long k = Convert.ToInt64(ToNumber(n));
                        string str = ToStr(s);
                        return k <= 0 ? "" : str.Substring((int)Math.Max(0, str.Length - k));
                    }
                case "CHARINDEX":
                    {
                        var needle = Arg(0); var hay = Arg(1);
                        if (needle == null || hay == null) return null;
                        int start = 0;
                        if (f.Args.Count > 2)
                        {
                            var st = Eval(f.Args[2], ctx);
                            if (st == null) return null;
                            start = (int)Convert.ToInt64(ToNumber(st)) - 1;
                            if (start < 0) start = 0;
                        }
                        int idx = ToStr(hay).IndexOf(ToStr(needle), start < 0 ? 0 : Math.Min(start, ToStr(hay).Length),
                            StringComparison.OrdinalIgnoreCase);
                        return (long)(idx + 1);
                    }
                case "CAST":
                    {
                        if (f.Args.Count != 3) throw ArgCount(f);
                        var type = f.Args[1] as Lit;
                        var len = f.Args[2] as Lit;
                        return CastValue(Eval(f.Args[0], ctx),
                            ParseDataTypeName(type?.Value as string),
                            len?.Value is long cl ? (int)cl : 0);
                    }
                case "CONVERT":
                    {
                        if (f.Args.Count < 3 || f.Args.Count > 4) throw ArgCount(f);
                        var type = f.Args[0] as Lit;
                        var len = f.Args[1] as Lit;
                        return CastValue(Eval(f.Args[2], ctx),
                            ParseDataTypeName(type?.Value as string),
                            len?.Value is long cl ? (int)cl : 0);
                    }
                case "DB_NAME":
                    {
                        if (f.Args.Count == 0) return ctx.Session.Database;
                        var id = Arg(0);
                        return id == null ? ctx.Session.Database : ctx.Engine.DatabaseNameOf(Convert.ToInt64(ToNumber(id)));
                    }
                case "DB_ID":
                    {
                        if (f.Args.Count == 0) return ctx.Engine.DatabaseIdOf(null, ctx.Session);
                        var name = Arg(0);
                        return name == null ? null : ctx.Engine.DatabaseIdOf(ToStr(name), ctx.Session);
                    }
                case "OBJECT_ID":
                    {
                        var name = Arg(0);
                        return name == null ? null : ctx.Engine.ObjectIdOf(ToStr(name), ctx.Session);
                    }
                case "OBJECT_NAME":
                    {
                        var id = Arg(0);
                        return id == null ? null : ctx.Engine.ObjectNameOf(Convert.ToInt64(ToNumber(id)), ctx.Session);
                    }
                case "HAS_DBACCESS":
                    {
                        var name = Arg(0);
                        return name != null && ctx.Engine.DatabaseExists(ToStr(name)) ? 1L : 0L;
                    }
                case "DATABASEPROPERTYEX":
                    {
                        var prop = Arg(1);
                        if (f.Args.Count < 2 || Arg(0) == null || prop == null) return null;
                        return ToStr(prop).ToUpperInvariant() switch
                        {
                            "ISAUTOSHRINK" => 0L,
                            "ISAUTOCREATESTATISTICS" => 1L,
                            "ISAUTOUPDATESTATISTICS" => 1L,
                            "COLLATION" => "SQL_Latin1_General_CP1_CI_AS",
                            "RECOVERY" => "SIMPLE",
                            "UPDATEABILITY" => "READ_WRITE",
                            "USERACCESS" => "MULTI_USER",
                            "STATUS" => "ONLINE",
                            "VERSION" => 907L,
                            _ => null
                        };
                    }
                case "SERVERPROPERTY":
                    {
                        var p = Arg(0);
                        return p == null ? null : ToStr(p).ToUpperInvariant() switch
                        {
                            "PRODUCTVERSION" => "16.0.1000.0",
                            "PRODUCTMAJORVERSION" => 16L,
                            "PRODUCTMINORVERSION" => 0L,
                            "PRODUCTBUILD" => 1000L,
                            "PRODUCTREVISION" => 0L,
                            "PRODUCTLEVEL" => "RTM",
                            "PRODUCTBUILDTYPE" => "OD",
                            "EDITION" => "MSSQL Server (unofficial)",
                            "SERVERNAME" => ctx.Engine.ServerName,
                            "ENGINEEDITION" => 2L,
                            "ISINTEGRATEDSECURITYONLY" => 0L,
                            "ISHADRENABLED" => 0L,
                            "ISCLUSTERED" => 0L,
                            "ISSINGLEUSER" => 0L,
                            "ISFULLTEXTINSTALLED" => 0L,
                            "ISXTPSUPPORTED" => 0L,
                            "COLLATION" => "SQL_Latin1_General_CP1_CI_AS",
                            "MACHINENAME" => ctx.Engine.ServerName,
                            "COMPUTERNAMEPHYSICALNETBIOS" => ctx.Engine.ServerName,
                            "SERVICENAME" => "MSSQLSERVER",
                            "INSTANCENAME" => null,
                            "BUILDCLRVERSION" => "4.0.30319.0",
                            "LANGUAGE" => "us_english",
                            _ => null
                        };
                    }
                case "CONNECTIONPROPERTY":
                    {
                        var p = Arg(0);
                        if (p == null) return null;
                        return ToStr(p).ToLowerInvariant() switch
                        {
                            "net_transport" => "TCP",
                            "physical_net_transport" => "TCP",
                            "protocol_type" => "TSQL",
                            "auth_scheme" => "SQL",
                            "local_net_address" => ctx.Session.LocalAddress,
                            "local_tcp_port" => ctx.Session.LocalPort > 0 ? ctx.Session.LocalPort : null,
                            "client_net_address" => ctx.Session.ClientAddress,
                            "client_tcp_port" => ctx.Session.ClientPort > 0 ? ctx.Session.ClientPort : null,
                            _ => null
                        };
                    }
                case "APP_NAME": return "MSSQL SERVER (unofficial)";
                case "HOST_NAME": return "android-device";
                case "SUSER_SNAME": case "ORIGINAL_LOGIN": return "sa";
                case "USER_NAME": case "USER_ID": return "dbo";
                case "SCHEMA_NAME":
                    {
                        if (f.Args.Count == 0) return "dbo";
                        var id = Arg(0);
                        return id == null ? null : "dbo";
                    }
                case "SCHEMA_ID":
                    {
                        if (f.Args.Count == 0) return 1L;
                        var name = Arg(0);
                        return name == null ? null
                            : ToStr(name).Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 1L
                            : ToStr(name).Equals("sys", StringComparison.OrdinalIgnoreCase) ? 2L : null;
                    }
                case "OBJECT_SCHEMA_NAME": return "dbo";
                case "TYPE_NAME": return "int";
                case "TYPE_ID": return 56L;
                default:
                    throw new SqlException(195, $"'{f.Name}' is not a recognized built-in function name.");
            }
        }

        private static SqlException ArgCount(Func f) =>
            new(174, $"The {f.Name.ToLowerInvariant()} function requires more arguments.");

        private static object? EvalAggregate(Func f, RowContext ctx)
        {
            if (ctx.Group == null)
                throw new SqlException(147, "An aggregate may not appear in the WHERE clause unless it is in a subquery.");

            if (f.Name == "COUNT" && f.Star) return (long)ctx.Group.Count;
            if (f.Args.Count != 1) throw ArgCount(f);

            var values = new List<object>();
            foreach (var row in ctx.Group)
            {
                var rc = new RowContext { Schema = ctx.Schema, Row = row, Engine = ctx.Engine, Session = ctx.Session };
                var v = Eval(f.Args[0], rc);
                if (v != null) values.Add(v);
            }

            switch (f.Name)
            {
                case "COUNT": return (long)values.Count;
                case "MIN": return values.Count == 0 ? null : values.Aggregate((a, b) => Compare(a, b) <= 0 ? a : b);
                case "MAX": return values.Count == 0 ? null : values.Aggregate((a, b) => Compare(a, b) >= 0 ? a : b);
                case "SUM":
                    {
                        if (values.Count == 0) return null;
                        var nums = values.Select(ToNumber).ToList();
                        if (nums.All(n => n is long))
                        {
                            try { return nums.Aggregate(0L, (acc, n) => checked(acc + (long)n)); }
                            catch (OverflowException) { throw new SqlException(8115, "Arithmetic overflow error converting expression to data type bigint."); }
                        }
                        return nums.Sum(n => Convert.ToDouble(n, CultureInfo.InvariantCulture));
                    }
                case "AVG":
                    return values.Count == 0 ? null
                        : values.Select(v => Convert.ToDouble(ToNumber(v), CultureInfo.InvariantCulture)).Average();
                default: throw new SqlException(195, $"'{f.Name}' is not a recognized built-in function name.");
            }
        }
    }
}