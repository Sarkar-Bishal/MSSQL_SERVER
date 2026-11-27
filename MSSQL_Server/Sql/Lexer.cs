using System.Text;

namespace MSSQL_Server.Sql
{
    internal enum Tk { Ident, Number, String, Symbol, Variable, End }

    internal sealed record Token(Tk Kind, string Text, bool Quoted = false)
    {
        public bool Is(string keyword) =>
            Kind == Tk.Ident && !Quoted && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
        public bool IsSym(string s) => Kind == Tk.Symbol && Text == s;
    }

    internal static class Lexer
    {
        public static List<Token> Tokenize(string sql)
        {
            var list = new List<Token>();
            int i = 0, n = sql.Length;

            while (i < n)
            {
                char c = sql[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '-' && i + 1 < n && sql[i + 1] == '-')
                {
                    while (i < n && sql[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new SqlException(113, "Missing end comment mark '*/'.", 15);
                    i = end + 2;
                    continue;
                }

                // String literal (optionally N'...')
                if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < n && sql[i + 1] == '\''))
                {
                    if (c != '\'') i++;
                    i++;
                    var sb = new StringBuilder();
                    while (true)
                    {
                        if (i >= n) throw new SqlException(105, "Unclosed quotation mark after the character string.", 15);
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < n && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(sql[i++]);
                    }
                    list.Add(new Token(Tk.String, sb.ToString()));
                    continue;
                }

                // Quoted identifiers: [name] or "name"
                if (c == '[' || c == '"')
                {
                    char close = c == '[' ? ']' : '"';
                    int end = sql.IndexOf(close, i + 1);
                    if (end < 0) throw new SqlException(105, "Unclosed quotation mark.", 15);
                    list.Add(new Token(Tk.Ident, sql.Substring(i + 1, end - i - 1), true));
                    i = end + 1;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
                {
                    int s = i;
                    while (i < n && char.IsDigit(sql[i])) i++;
                    if (i < n && sql[i] == '.') { i++; while (i < n && char.IsDigit(sql[i])) i++; }
                    if (i < n && (sql[i] == 'e' || sql[i] == 'E'))
                    {
                        int save = i; i++;
                        if (i < n && (sql[i] == '+' || sql[i] == '-')) i++;
                        if (i < n && char.IsDigit(sql[i])) { while (i < n && char.IsDigit(sql[i])) i++; }
                        else i = save;
                    }
                    list.Add(new Token(Tk.Number, sql.Substring(s, i - s)));
                    continue;
                }

                if (c == '@')
                {
                    int s = i++;
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@')) i++;
                    list.Add(new Token(Tk.Variable, sql.Substring(s, i - s)));
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '#')
                {
                    int s = i++;
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '#' || sql[i] == '$')) i++;
                    list.Add(new Token(Tk.Ident, sql.Substring(s, i - s)));
                    continue;
                }

                if (i + 1 < n)
                {
                    string two = sql.Substring(i, 2);
                    if (two is "<=" or ">=" or "<>" or "!=")
                    {
                        list.Add(new Token(Tk.Symbol, two == "!=" ? "<>" : two));
                        i += 2;
                        continue;
                    }
                }

                // & | ^ ~ = bitwise operators (SMO catalog queries use them, e.g. user_access & 1)
                if ("(),;.*+-/%=<>".IndexOf(c) >= 0)
                {
                    list.Add(new Token(Tk.Symbol, c.ToString()));
                    i++;
                    continue;
                }
                if ("&|^~".IndexOf(c) >= 0)
                {
                    list.Add(new Token(Tk.Symbol, c.ToString()));
                    i++;
                    continue;
                }

                throw new SqlException(102, $"Incorrect syntax near '{c}'.", 15);
            }

            list.Add(new Token(Tk.End, ""));
            return list;
        }
    }
}
