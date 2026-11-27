namespace MSSQL_Server.Database
{
    public sealed class DatabaseManager
    {
        public string RootDirectory { get; }
        public string DatabaseDirectory { get; }

        public DatabaseManager(string applicationDataDirectory)
        {
            RootDirectory = Path.Combine(applicationDataDirectory, "MSSQL_SERVER");
            DatabaseDirectory = Path.Combine(RootDirectory, "Databases");
            Directory.CreateDirectory(DatabaseDirectory);
        }
    }
}
