using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSSQL_Server.Configuration
{
    public sealed class ServerConfiguration
    {
        public string ServerName { get; set; } = "MSSQLServer";
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 1433;
        public int MaxConnections { get; set; } = 50;
        public int IdleTimeoutSeconds { get; set; } = 300;

        public string AdminLogin { get; set; } = "sa";
        public string AdminPasswordSalt { get; set; } = string.Empty;
        public string AdminPasswordHash { get; set; } = string.Empty;

        [JsonIgnore]
        public bool HasAdminPassword =>
            AdminPasswordSalt.Length > 0 && AdminPasswordHash.Length > 0;

        public static ServerConfiguration Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize(
                        File.ReadAllText(path),
                        ConfigJsonContext.Default.ServerConfiguration);
                    if (cfg != null) return cfg;
                }
            }
            catch (Exception)
            {
                // Corrupt file: fall back to defaults (password must be set again).
            }
            return new ServerConfiguration();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(this, ConfigJsonContext.Default.ServerConfiguration));
            File.Move(tmp, path, overwrite: true);
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ServerConfiguration))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
