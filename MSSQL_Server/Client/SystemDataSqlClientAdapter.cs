// ============================================================================
//  System.Data.SqlClient adapter (classic 4.8.x stack) - one of exactly two
//  files that know about a SqlClient assembly (the other is
//  MicrosoftDataSqlClientAdapter.cs).
//
//  BUILD FIX for CS0430 + CS0234:
//  An earlier revision of this file started with
//      extern alias sds;
//      using Sds = sds::System.Data.SqlClient;
//  which requires the compiler option /reference:sds=System.Data.SqlClient.dll.
//  That option only exists when the build applies the csproj "Aliases" metadata
//  to the package reference; when it does not, Visual Studio reports
//      CS0430  The extern alias 'sds' was not specified in a /reference option
//      CS0234  The type or namespace name 'System' does not exist in 'sds'
//  The alias is no longer needed at all: this file loads the assembly BY NAME
//  at runtime (System.Reflection.Assembly.Load via SqlClientAdapter.TryLoadType)
//  and only uses System.Data.Common base classes (DbConnection / DbCommand),
//  which both driver stacks implement. No /reference option, no csproj switch.
// ============================================================================
using System.Data.Common;

namespace MSSQL_Server.Client
{
    internal sealed class SystemDataSqlClientAdapter : SqlClientAdapter
    {
        public const string AssemblySimpleName = "System.Data.SqlClient";
        private const string NamespaceName = "System.Data.SqlClient";

        private Type? _sqlConnectionType;

        public override string DisplayName => AssemblySimpleName;

        private Type SqlConnectionType =>
            _sqlConnectionType ??= (SqlClientAdapter.TryLoadType(AssemblySimpleName, NamespaceName + ".SqlConnection")
                ?? throw new InvalidOperationException(
                    $"The '{AssemblySimpleName}' package assembly could not be loaded in this build "
                    + "(missing package or removed by the linker)."));

        public override DbConnection CreateConnection(string connectionString)
        {
            object instance = Activator.CreateInstance(SqlConnectionType, connectionString)
                ?? throw new InvalidOperationException(
                    $"Could not construct {NamespaceName}.SqlConnection in this build.");
            return (DbConnection)instance;
        }

        public override DbCommand CreateCommand(string commandText, DbConnection connection)
        {
            // DbConnection.CreateCommand() returns the driver's own DbCommand, already
            // bound to this connection - no cast to a provider type needed.
            DbCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            return command;
        }

        public override string DescribeException(Exception ex) =>
            DescribeSqlExceptionLike(ex, DisplayName);
    }
}
