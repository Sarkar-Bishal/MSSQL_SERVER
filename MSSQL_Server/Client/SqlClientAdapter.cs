using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;

namespace MSSQL_Server.Client
{
    /// <summary>Which SqlClient assembly the CLIENT tab should use for a connection.</summary>
    public enum ClientDriver
    {
        MicrosoftDataSqlClient = 0,  // Microsoft.Data.SqlClient (modern, recommended)
        SystemDataSqlClient = 1,     // System.Data.SqlClient (classic 4.8.x stack)
        Auto = 2                     // try Microsoft.Data.SqlClient, fall back to System.Data.SqlClient
    }

    /// <summary>
    /// Lets the app drive BOTH SqlClient stacks through System.Data.Common abstractions.
    ///
    /// The two NuGet packages (Microsoft.Data.SqlClient and System.Data.SqlClient) declare the
    /// SAME namespace and the SAME type names ("SqlConnection" exists in both assemblies), so
    /// referencing their types directly at compile time would need extern aliases (errors
    /// CS0430 / CS0234 whenever the build does not apply the alias to the /reference option,
    /// as seen in Visual Studio) or would raise CS0433 ambiguity errors.
    ///
    /// The adapters therefore NEVER name a SqlClient type in source: each one loads its
    /// assembly BY NAME at runtime (System.Reflection.Assembly.Load via TryLoadType) and works
    /// only with the System.Data.Common base classes (DbConnection / DbCommand), which both
    /// stacks implement. No alias, no csproj switch, compiles identically on every machine.
    /// </summary>
    internal abstract class SqlClientAdapter
    {
        public abstract string DisplayName { get; }

        public abstract DbConnection CreateConnection(string connectionString);

        public abstract DbCommand CreateCommand(string commandText, DbConnection connection);

        /// <summary>SSMS-style one-line error text, e.g. "Msg 18456, Level 14, State 1: Login failed...".</summary>
        public abstract string DescribeException(Exception ex);

        public static IEnumerable<(ClientDriver Driver, SqlClientAdapter Adapter)>
            ResolveOrder(ClientDriver driver)
        {
            if (driver == ClientDriver.MicrosoftDataSqlClient)
            {
                yield return (ClientDriver.MicrosoftDataSqlClient, new MicrosoftDataSqlClientAdapter());
            }
            else if (driver == ClientDriver.SystemDataSqlClient)
            {
                yield return (ClientDriver.SystemDataSqlClient, new SystemDataSqlClientAdapter());
            }
            else
            {
                // Auto: try the modern stack first, fall back to the classic one.
                yield return (ClientDriver.MicrosoftDataSqlClient, new MicrosoftDataSqlClientAdapter());
                yield return (ClientDriver.SystemDataSqlClient, new SystemDataSqlClientAdapter());
            }
        }

        // ------------- shared runtime plumbing (reflection, alias-free) -------------

        /// <summary>
        /// Loads a type at runtime from a named assembly, e.g. assembly "System.Data.SqlClient"
        /// plus full type name "System.Data.SqlClient.SqlConnection".
        /// Returns null when the assembly or the type is missing (package not shipped,
        /// linker removed it, ...) - callers turn that into a descriptive exception,
        /// and the Auto driver mode simply falls back to the other stack.
        /// </summary>
        protected static Type? TryLoadType(string assemblySimpleName, string fullTypeName)
        {
            try
            {
                Assembly assembly = Assembly.Load(assemblySimpleName);
                return assembly.GetType(fullTypeName, throwOnError: false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// SSMS-style description that recognises a SqlException from EITHER driver stack by
        /// its shape (int Number plus Class and State properties) instead of by compile-time
        /// type, because no SqlException type is ever referenced in source.
        /// </summary>
        protected static string DescribeSqlExceptionLike(Exception ex, string displayName)
        {
            if (TryDescribeSqlExceptionShape(ex, displayName, out string sqlText))
                return sqlText;

            if (ex.InnerException != null)
                return $"{ex.Message} -> {DescribeSqlExceptionLike(ex.InnerException, displayName)}";

            return $"{ex.GetType().Name}: {ex.Message} [{displayName}]";
        }

        private static bool TryDescribeSqlExceptionShape(Exception ex, string displayName, out string text)
        {
            text = string.Empty;

            PropertyInfo? number = ex.GetType().GetProperty("Number");
            PropertyInfo? level = ex.GetType().GetProperty("Class");
            PropertyInfo? state = ex.GetType().GetProperty("State");

            // A real SqlException (either stack) has exactly this shape. Everything else
            // (SocketException, IOException, InvalidOperationException, ...) falls through
            // to the generic formatting above.
            if (number == null || level == null || state == null || number.PropertyType != typeof(int))
                return false;

            try
            {
                int msg = (int)number.GetValue(ex)!;
                int lvl = Convert.ToInt32(level.GetValue(ex));
                int st = Convert.ToInt32(state.GetValue(ex));
                text = $"Msg {msg}, Level {lvl}, State {st}: {ex.Message} [{displayName}]";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
