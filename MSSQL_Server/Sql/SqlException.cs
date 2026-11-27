namespace MSSQL_Server.Sql
{
    public sealed class SqlException : Exception
    {
        public int Number { get; }
        public byte Severity { get; }

        public SqlException(int number, string message, byte severity = 16)
            : base(message)
        {
            Number = number;
            Severity = severity;
        }
    }
}
