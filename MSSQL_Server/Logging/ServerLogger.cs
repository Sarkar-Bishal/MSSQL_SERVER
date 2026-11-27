namespace MSSQL_Server.Logging
{
    public sealed class ServerLogger
    {
        private const long MaxFileBytes = 1_000_000;
        private readonly object gate = new();
        private readonly string path;

        public event Action<string>? LineWritten;

        public ServerLogger(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "server.log");
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z [{level}] {message}";
            try
            {
                lock (gate)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxFileBytes)
                        File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never crash the server.
            }
            LineWritten?.Invoke(line);
        }
    }
}
