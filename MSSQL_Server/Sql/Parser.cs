using System.Globalization;

namespace MSSQL_Server.Sql
{
    internal sealed class Parser
    {
        private static readonly HashSet<string> StatementStarts = new(StringComparer.OrdinalIgnoreCase)
            { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "USE", "SET", "GO", "BEGIN", "COMMIT", "ROLLBACK",
              "DECLARE", "IF", "ELSE", "EXEC", "EXECUTE", "PRINT", "RETURN", "WHILE" };

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
            { "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "JOIN", "INNER", "LEFT", "ON", "AS",
              "UNION", "SET", "VALUES", "AND", "OR", "NOT", "BY", "OUTER",
              // control-flow / scripting keywords - must never be mistaken for column refs or aliases
              "WHEN", "THEN", "ELSE", "END", "IF", "DECLARE", "EXEC", "EXECUTE", "PRINT", "RETURN",
              "WHILE", "OUTPUT", "CASE", "CAST", "CONVERT", "EXISTS" };

        private readonly List<Token> t;
        private int p;

        private Parser(List<Token> tokens) { t = tokens; }

        public static List<Stmt> ParseBatch(string sql)
        {
            var parser = new Parser(Lexer.Tokenize(sql));
            var list = new List<Stmt>();
            while (true)
            {
                while (parser.AcceptSym(";")) { }
                if (parser.Peek.Kind == Tk.End) break;
                list.Add(parser.ParseStatement());
            }
            return list;
        }

        // ------------------------------------------------------------ helpers

        private Token Peek => t[p];
        private Token Next() => t[p < t.Count - 1 ? p++ : p];

        private SqlException Syntax() =>
            new(102, Peek.Kind == Tk.End ? "Incorrect syntax near the end of the batch."
                                          : $"Incorrect syntax near '{Peek.Text}'.", 15);

        private bool AcceptKw(string kw) { if (Peek.Is(kw)) { p++; return true; } return false; }
        private bool AcceptSym(string s) { if (Peek.IsSym(s)) { p++; return true; } return false; }
        private void ExpectKw(string kw) { if (!AcceptKw(kw)) throw Syntax(); }
        private void ExpectSym(string s) { if (!AcceptSym(s)) throw Syntax(); }

        private string Identifier()
        {
            if (Peek.Kind != Tk.Ident) throw Syntax();
            return Next().Text;
        }

        private ObjectName ParseObjectName()
        {
            var parts = new List<string> { Identifier() };
            while (AcceptSym(".")) parts.Add(Identifier());
            return parts.Count switch
            {
                1 => new ObjectName(null, null, parts[0]),
                2 => new ObjectName(null, parts[0], parts[1]),
                3 => new ObjectName(parts[0], parts[1], parts[2]),
                4 => new ObjectName(parts[1], parts[2], parts[3]),
                _ => throw Syntax()
            };
        }

        // ------------------------------------------------------------ statements

        private Stmt ParseStatement()
        {
            if (AcceptKw("SELECT")) return ParseSelect();
            if (AcceptKw("INSERT")) return ParseInsert();
            if (AcceptKw("UPDATE")) return ParseUpdate();
            if (AcceptKw("DELETE")) return ParseDelete();
            if (AcceptKw("CREATE")) return ParseCreate();
            if (AcceptKw("DROP")) return ParseDrop();
            if (AcceptKw("USE")) return new UseDb(Identifier());
            if (AcceptKw("DECLARE")) return ParseDeclare();
            if (AcceptKw("SET")) return ParseSetStatement();
            if (AcceptKw("IF")) return ParseIf();
            if (AcceptKw("EXEC") || AcceptKw("EXECUTE")) return ParseExec();
            if (AcceptKw("PRINT")) { SkipToStatementEnd(); return new NoOp(); }
            if (AcceptKw("RETURN")) return new ReturnStmt();
            if (AcceptKw("WHILE")) throw new SqlException(102, "WHILE loops are not supported by this engine.", 15);
            if (AcceptKw("GO")) return new NoOp();
            if (AcceptKw("BEGIN")) return ParseBegin();
            if (AcceptKw("COMMIT")) { if (AcceptKw("TRAN") || AcceptKw("TRANSACTION")) SkipTransactionName(); return new CommitTran(); }
            if (AcceptKw("ROLLBACK")) { if (AcceptKw("TRAN") || AcceptKw("TRANSACTION")) SkipTransactionName(); return new RollbackTran(); }
            throw Syntax();
        }

        private void SkipTransactionName()
        {
            if ((Peek.Kind == Tk.Ident && (Peek.Quoted || !StatementStarts.Contains(Peek.Text))) || Peek.Kind == Tk.Variable) p++;
        }

        private void SkipToStatementEnd()
        {
            while (Peek.Kind != Tk.End && !Peek.IsSym(";") &&
                   !(Peek.Kind == Tk.Ident && !Peek.Quoted && StatementStarts.Contains(Peek.Text)))
                p++;
        }

        private void SkipSemicolons() { while (AcceptSym(";")) { } }

        // BEGIN TRAN ...   |   BEGIN <statements> END   (control-flow block)
        private Stmt ParseBegin()
        {
            if (AcceptKw("TRAN") || AcceptKw("TRANSACTION"))
            {
                SkipTransactionName();
                return new BeginTran();
            }

            var stmts = new List<Stmt>();
            while (true)
            {
                SkipSemicolons();
                if (Peek.Is("END")) { p++; break; }
                if (Peek.Kind == Tk.End)
                    throw new SqlException(102, "Incorrect syntax near the end of the batch: BEGIN without a matching END.", 15);
                stmts.Add(ParseStatement());
            }
            return new BlockStmt(stmts);
        }

        private Stmt ParseDeclare()
        {
            var vars = new List<VarDecl>();
            do
            {
                if (Peek.Kind != Tk.Variable || Peek.Text.StartsWith("@@")) throw Syntax();
                string name = Next().Text;
                AcceptKw("AS");
                var (type, len) = ParseTypeSpec();
                Expr? init = AcceptSym("=") ? ParseExpr() : null;
                vars.Add(new VarDecl(name, type, len, init));
            } while (AcceptSym(","));
            return new DeclareVars(vars);
        }

        // Data type spec used by DECLARE / CAST / CONVERT: NAME [(n | MAX [, s])]
        private (DataType Type, int Length) ParseTypeSpec()
        {
            if (Peek.Kind != Tk.Ident) throw Syntax();
            string typeName = Next().Text.ToUpperInvariant();
            if (AcceptSym(".")) typeName = Identifier().ToUpperInvariant(); // schema-prefixed type, e.g. sys.sysname
            if (typeName == "TABLE") throw new SqlException(102, "Table variables are not supported by this engine.", 15);
            if (typeName == "CURSOR") throw new SqlException(102, "Cursor declarations are not supported by this engine.", 15);

            int len = 0;
            if (AcceptSym("("))
            {
                len = AcceptKw("MAX") ? -1 : (int)ParseNumberLiteral();
                while (AcceptSym(",")) ParseNumberLiteral(); // DECIMAL(p,s) precision digits
                ExpectSym(")");
            }

            var (type, defLen) = ResolveDataType(typeName);
            if (type == DataType.NVarChar)
            {
                if (defLen > 0) len = defLen; // SYSNAME=128, UNIQUEIDENTIFIER=36, text/date-time/binary family=4000
                if (len < 0 || len > 4000) len = 4000;
            }
            return (type, len);
        }

        /// <summary>
        /// Shared data-type name resolution for DECLARE/CAST/CONVERT (ParseTypeSpec) and
        /// CREATE TABLE (ParseColumnDef). SMO's Object Explorer batches declare columns and
        /// variables typed SYSNAME, UNIQUEIDENTIFIER, DATETIME, BINARY and similar system
        /// types, so every unrecognized name resolves to Unicode character data instead of
        /// raising error 2715 ("Cannot find data type"), which breaks Object Explorer
        /// enumeration. Returns the engine type plus the fixed default length for system
        /// types (0 = caller decides: explicit (n), or the SQL Server default for bare names).
        /// </summary>
        private static (DataType Type, int DefaultLength) ResolveDataType(string typeName)
        {
            DataType type = typeName switch
            {
                "INT" or "INTEGER" or "SMALLINT" or "TINYINT" => DataType.Int,
                "BIGINT" => DataType.BigInt,
                "BIT" => DataType.Bit,
                "FLOAT" or "REAL" or "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" => DataType.Float,
                _ => DataType.NVarChar
            };
            if (type != DataType.NVarChar) return (type, 0);

            // Everything else is stored as Unicode character data.
            int defLen = typeName switch
            {
                "SYSNAME" => 128,                               // built-in alias of nvarchar(128)
                "UNIQUEIDENTIFIER" => 36,
                "TEXT" or "NTEXT" or "XML" or "SQL_VARIANT" or
                "BINARY" or "VARBINARY" or "IMAGE" or
                "TIMESTAMP" or "ROWVERSION" or "TIME" => 4000,
                _ when typeName.StartsWith("DATE") || typeName.StartsWith("DATETIME") => 4000,
                _ => 0                                          // NVARCHAR/VARCHAR/NCHAR/CHAR keep caller default
            };
            return (type, defLen);
        }

        private Stmt ParseSetStatement()
        {
            // SET @variable = expr   (assignment)
            if (Peek.Kind == Tk.Variable && !Peek.Text.StartsWith("@@"))
            {
                string name = Next().Text;
                if (!AcceptSym("=")) throw Syntax();
                return new SetVar(name, ParseExpr());
            }
            // SET NOCOUNT ON / SET ANSI_NULLS ON / SET LOCK_TIMEOUT n / ... - accepted, no effect
            SkipToStatementEnd();
            return new NoOp();
        }

        private Stmt ParseIf()
        {
            var cond = ParseExpr();
            SkipSemicolons();
            var then = ParseStatement();
            Stmt? els = null;
            if (AcceptKw("ELSE"))
            {
                SkipSemicolons();
                els = ParseStatement();
            }
            return new IfStmt(cond, then, els);
        }

        private Stmt ParseExec()
        {
            // EXEC ('batch text')
            if (AcceptSym("("))
            {
                var e = ParseExpr();
                ExpectSym(")");
                return new ExecSql(e, null);
            }

            var name = ParseObjectName();
            if (!name.Name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                throw new SqlException(2812,
                    $"Could not find stored procedure '{(name.Schema != null ? name.Schema + "." : "")}{name.Name}'. Only sp_executesql is supported.");

            var args = new List<Expr>();
            var prms = new List<ExecParam>();
            if (AcceptSym(","))
            {
                do
                {
                    // @param = value [OUTPUT]
                    if (Peek.Kind == Tk.Variable && !Peek.Text.StartsWith("@@") &&
                        p + 1 < t.Count && t[p + 1].IsSym("="))
                    {
                        string pn = Next().Text;
                        p++; // '='
                        var val = ParseExpr();
                        bool output = AcceptKw("OUTPUT") || AcceptKw("OUT");
                        prms.Add(new ExecParam(pn, val, output));
                    }
                    else
                    {
                        args.Add(ParseExpr()); // N'batch' or N'@p type,...' definition string
                    }
                } while (AcceptSym(","));
            }

            if (args.Count == 0)
                throw new SqlException(201,
                    "Procedure or function 'sp_executesql' expects parameter '@stmt', which was not supplied.");
            return new ExecSql(args[0], prms.Count > 0 ? prms : null);
        }

        private Stmt ParseCreate()
        {
            if (AcceptKw("DATABASE")) return new CreateDb(Identifier());
            ExpectKw("TABLE");
            var name = ParseObjectName();
            ExpectSym("(");
            var cols = new List<ColumnDef>();
            do { cols.Add(ParseColumnDef()); } while (AcceptSym(","));
            ExpectSym(")");
            if (cols.Select(c => c.Name.ToLowerInvariant()).Distinct().Count() != cols.Count)
                throw new SqlException(2705, "Column names in each table must be unique.");
            return new CreateTable(name, cols);
        }

        private ColumnDef ParseColumnDef()
        {
            string name = Identifier();
            string typeName = Identifier().ToUpperInvariant();
            if (AcceptSym(".")) typeName = Identifier().ToUpperInvariant(); // schema-prefixed type, e.g. sys.sysname
            int length = 0;
            if (AcceptSym("("))
            {
                if (AcceptKw("MAX")) length = -1;
                else length = (int)ParseNumberLiteral();
                while (AcceptSym(",")) ParseNumberLiteral(); // DECIMAL(p,s)
                ExpectSym(")");
            }

            // Any system type name is accepted: SMO temp-table definitions use SYSNAME,
            // DATETIME, UNIQUEIDENTIFIER, BINARY etc. Types the engine does not model
            // natively fall back to Unicode character data (raising error 2715 would
            // break SSMS Object Explorer enumeration).
            var (type, defLen) = ResolveDataType(typeName);
            if (type == DataType.NVarChar)
            {
                if (defLen > 0) length = defLen;                // SYSNAME=128, UNIQUEIDENTIFIER=36, text/date-time=4000
                else if (length == 0) length = 1;               // bare VARCHAR/NVARCHAR/NCHAR/CHAR defaults to (1)
                if (length < 0 || length > 4000) length = 4000; // MAX is capped (no PLP support yet)
            }

            bool notNull = false, pk = false, identity = false;
            while (true)
            {
                if (AcceptKw("NOT")) { ExpectKw("NULL"); notNull = true; }
                else if (AcceptKw("NULL")) notNull = false;
                else if (AcceptKw("PRIMARY")) { ExpectKw("KEY"); pk = true; notNull = true; }
                else if (AcceptKw("IDENTITY"))
                {
                    identity = true;
                    if (AcceptSym("(")) { ParseNumberLiteral(); ExpectSym(","); ParseNumberLiteral(); ExpectSym(")"); }
                    notNull = true;
                }
                else if (AcceptKw("DEFAULT")) SkipColumnDefault(); // DEFAULT (getdate()) / DEFAULT 0 / DEFAULT NULL
                else if (AcceptKw("COLLATE")) Next();              // ignore the collation identifier
                else break;
            }
            if (identity && type is not (DataType.Int or DataType.BigInt))
                throw new SqlException(2749, $"Identity column '{name}' must be of data type int or bigint.");
            return new ColumnDef(name, type, length, notNull, pk, identity);
        }

        /// <summary>Consume and ignore a column DEFAULT expression (SMO DDL compatibility).</summary>
        private void SkipColumnDefault()
        {
            if (AcceptSym("("))
            {
                int depth = 1;
                while (depth > 0 && Peek.Kind != Tk.End)
                {
                    if (AcceptSym("(")) depth++;
                    else if (AcceptSym(")")) depth--;
                    else Next();
                }
                return;
            }
            if (Peek.IsSym("-") || Peek.IsSym("+")) Next(); // signed literal
            if (Peek.Kind != Tk.End) Next();                // number, string, NULL or identifier
        }

        private double ParseNumberLiteral()
        {
            bool neg = AcceptSym("-");
            if (Peek.Kind != Tk.Number) throw Syntax();
            double v = double.Parse(Next().Text, CultureInfo.InvariantCulture);
            return neg ? -v : v;
        }

        private Stmt ParseDrop()
        {
            if (AcceptKw("DATABASE")) return new DropDb(Identifier());
            ExpectKw("TABLE");
            return new DropTable(ParseObjectName());
        }

        private Stmt ParseInsert()
        {
            AcceptKw("INTO");
            var table = ParseObjectName();
            List<string>? cols = null;
            if (AcceptSym("("))
            {
                cols = new List<string>();
                do { cols.Add(Identifier()); } while (AcceptSym(","));
                ExpectSym(")");
            }
            ExpectKw("VALUES");
            var rows = new List<List<Expr>>();
            do
            {
                ExpectSym("(");
                var row = new List<Expr>();
                do { row.Add(ParseExpr()); } while (AcceptSym(","));
                ExpectSym(")");
                rows.Add(row);
            } while (AcceptSym(","));
            return new Insert(table, cols, rows);
        }

        private Stmt ParseUpdate()
        {
            var table = ParseObjectName();
            ExpectKw("SET");
            var sets = new List<(string, Expr)>();
            do
            {
                string col = Identifier();
                ExpectSym("=");
                sets.Add((col, ParseExpr()));
            } while (AcceptSym(","));
            Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;
            return new Update(table, sets, where);
        }

        private Stmt ParseDelete()
        {
            AcceptKw("FROM");
            var table = ParseObjectName();
            Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;
            return new Delete(table, where);
        }

        private Select ParseSelect()
        {
            bool distinct = AcceptKw("DISTINCT");
            int? top = null;
            if (AcceptKw("TOP"))
            {
                bool paren = AcceptSym("(");
                top = (int)ParseNumberLiteral();
                if (paren) ExpectSym(")");
            }

            var items = new List<SelectItem>();
            do { items.Add(ParseSelectItem()); } while (AcceptSym(","));

            TableRef? from = null;
            var joins = new List<JoinClause>();
            if (AcceptKw("FROM"))
            {
                from = ParseTableRef();
                while (true)
                {
                    bool left = false;
                    if (AcceptKw("INNER")) ExpectKw("JOIN");
                    else if (AcceptKw("LEFT")) { AcceptKw("OUTER"); ExpectKw("JOIN"); left = true; }
                    else if (!AcceptKw("JOIN")) break;
                    var tr = ParseTableRef();
                    ExpectKw("ON");
                    joins.Add(new JoinClause(tr, ParseExpr(), left));
                }
            }

            Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;

            var groupBy = new List<Expr>();
            if (AcceptKw("GROUP"))
            {
                ExpectKw("BY");
                do { groupBy.Add(ParseExpr()); } while (AcceptSym(","));
            }
            Expr? having = AcceptKw("HAVING") ? ParseExpr() : null;

            var order = new List<OrderItem>();
            if (AcceptKw("ORDER"))
            {
                ExpectKw("BY");
                do
                {
                    var e = ParseExpr();
                    bool desc = AcceptKw("DESC");
                    if (!desc) AcceptKw("ASC");
                    order.Add(new OrderItem(e, desc));
                } while (AcceptSym(","));
            }

            return new Select(top, distinct, items, from, joins, where, groupBy, having, order);
        }

        private TableRef ParseTableRef()
        {
            var name = ParseObjectName();
            string alias = name.Name;
            if (AcceptKw("AS")) alias = Identifier();
            else if (Peek.Kind == Tk.Ident && (Peek.Quoted || !Reserved.Contains(Peek.Text))) alias = Next().Text;
            return new TableRef(name, alias);
        }

        private SelectItem ParseSelectItem()
        {
            if (AcceptSym("*")) return new SelectItem(null, null, true, null);

            // alias.*
            if (Peek.Kind == Tk.Ident && t[p + 1].IsSym(".") && t[p + 2].IsSym("*"))
            {
                string tbl = Next().Text; p += 2;
                return new SelectItem(null, null, true, tbl);
            }

            // @variable = expr   (assignment into a scalar variable)
            if (Peek.Kind == Tk.Variable && !Peek.Text.StartsWith("@@") &&
                p + 1 < t.Count && t[p + 1].IsSym("="))
            {
                string varName = Next().Text;
                p++; // '='
                return new SelectItem(ParseExpr(), null, false, null, varName);
            }

            // alias = expr
            if (Peek.Kind == Tk.Ident && t[p + 1].IsSym("=") && !Reserved.Contains(Peek.Text))
            {
                string alias = Next().Text; p++;
                return new SelectItem(ParseExpr(), alias, false, null);
            }

            var e = ParseExpr();
            string? a = null;
            if (AcceptKw("AS")) a = Peek.Kind == Tk.String ? Next().Text : Identifier();
            else if (Peek.Kind == Tk.Ident && (Peek.Quoted || !Reserved.Contains(Peek.Text))) a = Next().Text;
            return new SelectItem(e, a, false, null);
        }

        // ------------------------------------------------------------ expressions

        private Expr ParseExpr() => ParseOr();

        private Expr ParseOr()
        {
            var l = ParseAnd();
            while (AcceptKw("OR")) l = new Bin("OR", l, ParseAnd());
            return l;
        }

        private Expr ParseAnd()
        {
            var l = ParseNot();
            while (AcceptKw("AND")) l = new Bin("AND", l, ParseNot());
            return l;
        }

        private Expr ParseNot() => AcceptKw("NOT") ? new Un("NOT", ParseNot()) : ParseComparison();

        private Expr ParseComparison()
        {
            var l = ParseAdditive();
            while (true)
            {
                if (Peek.Kind == Tk.Symbol && Peek.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
                {
                    string op = Next().Text;
                    l = new Bin(op, l, ParseAdditive());
                    continue;
                }
                if (Peek.Is("IS"))
                {
                    p++;
                    bool not = AcceptKw("NOT");
                    ExpectKw("NULL");
                    l = new IsNullExpr(l, not);
                    continue;
                }

                bool neg = false;
                int save = p;
                if (Peek.Is("NOT")) { p++; neg = true; }
                if (AcceptKw("LIKE")) { l = new LikeExpr(l, ParseAdditive(), neg); continue; }
                if (AcceptKw("IN"))
                {
                    ExpectSym("(");
                    var list = new List<Expr>();
                    do { list.Add(ParseExpr()); } while (AcceptSym(","));
                    ExpectSym(")");
                    l = new InExpr(l, list, neg);
                    continue;
                }
                if (AcceptKw("BETWEEN"))
                {
                    var lo = ParseAdditive();
                    ExpectKw("AND");
                    l = new BetweenExpr(l, lo, ParseAdditive(), neg);
                    continue;
                }
                p = save;
                return l;
            }
        }

        private Expr ParseAdditive()
        {
            var l = ParseMultiplicative();
            // T-SQL precedence: + - & ^ | share ONE level, left-associative
            // (so "user_access & 1 = 1" parses as "(user_access & 1) = 1").
            while (Peek.IsSym("+") || Peek.IsSym("-") || Peek.IsSym("&") || Peek.IsSym("^") || Peek.IsSym("|"))
                l = new Bin(Next().Text, l, ParseMultiplicative());
            return l;
        }

        private Expr ParseMultiplicative()
        {
            var l = ParseUnary();
            while (Peek.IsSym("*") || Peek.IsSym("/") || Peek.IsSym("%"))
                l = new Bin(Next().Text, l, ParseUnary());
            return l;
        }

        private Expr ParseUnary()
        {
            if (AcceptSym("-")) return new Un("-", ParseUnary());
            if (AcceptSym("+")) return ParseUnary();
            if (AcceptSym("~")) return new Un("~", ParseUnary()); // bitwise NOT
            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            var tok = Peek;
            switch (tok.Kind)
            {
                case Tk.Number:
                    p++;
                    if (long.TryParse(tok.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long l))
                        return new Lit(l);
                    return new Lit(double.Parse(tok.Text, CultureInfo.InvariantCulture));
                case Tk.String:
                    p++;
                    return new Lit(tok.Text);
                case Tk.Variable:
                    p++;
                    return new Var(tok.Text);
                case Tk.Symbol when tok.Text == "(":
                    p++;
                    var e = ParseExpr();
                    ExpectSym(")");
                    return e;
                case Tk.Ident:
                    if (tok.Is("NULL")) { p++; return new Lit(null); }
                    if (tok.Is("CASE")) return ParseCase();
                    if (tok.Is("CAST"))
                    {
                        p++;
                        ExpectSym("(");
                        var castExpr = ParseExpr();
                        ExpectKw("AS");
                        var castType = ParseTypeSpec();
                        ExpectSym(")");
                        return new Func("CAST",
                            new() { castExpr, new Lit(castType.Type.ToString()), new Lit((long)castType.Length) }, false);
                    }
                    if (tok.Is("CONVERT"))
                    {
                        p++;
                        ExpectSym("(");
                        var convType = ParseTypeSpec();
                        ExpectSym(",");
                        var convExpr = ParseExpr();
                        long style = 0;
                        if (AcceptSym(",")) style = (long)ParseNumberLiteral();
                        ExpectSym(")");
                        return new Func("CONVERT",
                            new() { new Lit(convType.Type.ToString()), new Lit((long)convType.Length), convExpr, new Lit(style) }, false);
                    }
                    if (tok.Is("EXISTS"))
                    {
                        p++;
                        ExpectSym("(");
                        ExpectKw("SELECT");
                        var sel = ParseSelect();
                        ExpectSym(")");
                        return new ExistsExpr(sel);
                    }
                    if (!tok.Quoted && Reserved.Contains(tok.Text)) throw Syntax();
                    p++;
                    if (AcceptSym("("))
                    {
                        string fn = tok.Text.ToUpperInvariant();
                        var args = new List<Expr>();
                        bool star = false;
                        if (AcceptSym("*")) star = true;
                        else if (!Peek.IsSym(")"))
                            do { args.Add(ParseExpr()); } while (AcceptSym(","));
                        ExpectSym(")");
                        return new Func(fn, args, star);
                    }
                    if (AcceptSym("."))
                    {
                        string second = Identifier();
                        if (AcceptSym("."))
                        {   // db.schema.col -> ignore the qualifiers we do not use
                            return new Col(second, Identifier());
                        }
                        return new Col(tok.Text, second);
                    }
                    return new Col(null, tok.Text);
                default:
                    throw Syntax();
            }
        }

        private Expr ParseCase()
        {
            p++; // CASE
            Expr? input = null;
            if (!Peek.Is("WHEN")) input = ParseExpr();

            var whens = new List<(Expr, Expr)>();
            while (AcceptKw("WHEN"))
            {
                var w = ParseExpr();
                ExpectKw("THEN");
                whens.Add((w, ParseExpr()));
            }
            if (whens.Count == 0) throw Syntax();

            Expr? els = AcceptKw("ELSE") ? ParseExpr() : null;
            ExpectKw("END");
            return new CaseExpr(input, whens, els);
        }
    }
}
