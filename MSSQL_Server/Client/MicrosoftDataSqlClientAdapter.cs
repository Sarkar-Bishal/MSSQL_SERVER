// ============================================================================
//  Microsoft.Data.SqlClient adapter - one of exactly two files that know about
//  a SqlClient assembly (the other is SystemDataSqlClientAdapter.cs).
//
//  This file contains NO "using Microsoft.Data.SqlClient" and NO "extern alias".
//  Both driver packages declare identical namespaces and type names
//  (System.Data.SqlClient.SqlConnection also exists inside the
//  Microsoft.Data.SqlClient assembly), so a direct type reference would need
//  extern aliases (CS0430 / CS0234 whenever the build does not apply the alias
//  to the /reference option - the errors Visual Studio reported) or would cause
//  CS0433 ambiguity. Instead, the assembly is loaded BY NAME at runtime and
//  only System.Data.Common base classes (DbConnection / DbCommand) are used,
//  which both stacks implement.
// ============================================================================
using System.Data.Common;

namespace MSSQL_Server.Client
{
    internal sealed class MicrosoftDataSqlClientAdapter : SqlClientAdapter
    {
        public const string AssemblySimpleName = "Microsoft.Data.SqlClient";
        private const string NamespaceName = "Microsoft.Data.SqlClient";

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
