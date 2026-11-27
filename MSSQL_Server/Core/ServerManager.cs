using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MSSQL_Server.Configuration;
using MSSQL_Server.Engine;
using MSSQL_Server.Logging;
using MSSQL_Server.Security;
using MSSQL_Server.Tds;

namespace MSSQL_Server.Core
{
    internal sealed class ServerManager : IDisposable
    {
        private readonly ServerConfiguration config;
        private readonly ServerLogger log;
        private readonly SqlEngine engine;
        private readonly LoginThrottle throttle = new();
        private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
        private readonly object stateLock = new();

        private TcpListener? listener;
        private CancellationTokenSource? cts;
        private Task? acceptLoop;

        public ServerState State { get; private set; } = ServerState.Stopped;
        public event Action<ServerState>? StateChanged;

        public ServerManager(ServerConfiguration config, ServerLogger log, SqlEngine engine)
        {
            this.engine = engine;
            this.config = config;
            this.log = log;
        }

        private void SetState(ServerState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (State is ServerState.Running or ServerState.Starting) return;
                SetState(ServerState.Starting);

                try
                {
                    if (!config.HasAdminPassword)
                        throw new InvalidOperationException("Set an 'sa' password before starting the server.");
                    if (!IPAddress.TryParse(config.Host, out var address))
                        throw new InvalidOperationException($"'{config.Host}' is not a valid IP address.");

                    cts = new CancellationTokenSource();
                    listener = new TcpListener(address, config.Port);
                    listener.Start();

                    var token = cts.Token;
                    acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));

                    log.Info($"Listening on {config.Host}:{config.Port} (TDS, no TLS).");
                    SetState(ServerState.Running);
                }
                catch (Exception ex)
                {
                    log.Error($"Start failed: {ex.Message}");
                    CleanupListener();
                    SetState(ServerState.Error);
                }
            }
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (stateLock)
            {
                if (State is not (ServerState.Running or ServerState.Starting or ServerState.Error))
                    return;
                SetState(ServerState.Stopping);
                cts?.Cancel();
                CleanupListener();
                foreach (var c in clients.Keys) c.Close();
                loop = acceptLoop;
            }

            try
            {
                var pending = clients.Values.Append(loop ?? Task.CompletedTask).ToArray();
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Connections were force-closed; errors here are expected.
            }

            lock (stateLock)
            {
                cts?.Dispose();
                cts = null;
                SetState(ServerState.Stopped);
            }
            log.Info("Server stopped.");
        }

        private void CleanupListener()
        {
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;
        }

        private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await l.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    if (ct.IsCancellationRequested) return;
                    log.Error($"Accept failed: {ex.Message}");
                    await Task.Delay(500, CancellationToken.None);
                    continue;
                }

                string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                if (clients.Count >= config.MaxConnections)
                {
                    log.Warn($"{remote}: rejected, connection limit ({config.MaxConnections}) reached.");
                    client.Close();
                    continue;
                }

                var task = Task.Run(() => HandleClientAsync(client, remote, ct), CancellationToken.None);
                clients[client] = task;
            }
        }

        private async Task HandleClientAsync(TcpClient client, string remote, CancellationToken stopToken)
        {
            log.Info($"{remote}: connected ({clients.Count} active).");
            var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
            var localEp = client.Client.LocalEndPoint as IPEndPoint;
            string ip = remoteEp?.Address.ToString() ?? remote;
            int clientPort = remoteEp?.Port ?? 0;
            string localIp = localEp?.Address.ToString() ?? "0.0.0.0";
            int localPort = localEp?.Port ?? config.Port;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, config.IdleTimeoutSeconds));

            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var session = new TdsSession(stream, config, log, throttle, remote, ip,
                    () => { idle.CancelAfter(timeout); return idle.Token; }, engine,
                    localIp, localPort, clientPort);
                await session.RunAsync(stopToken);
            }
            catch (OperationCanceledException)
            {
                log.Info($"{remote}: closed (idle timeout or shutdown).");
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException)
            {
                log.Warn($"{remote}: connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                log.Error($"{remote}: unexpected error: {ex}");
            }
            finally
            {
                client.Close();
                clients.TryRemove(client, out _);
                log.Info($"{remote}: disconnected.");
            }
        }

        public void Dispose()
        {
            try { StopAsync().Wait(TimeSpan.FromSeconds(6)); } catch (Exception) { }
        }
    }
}
