using System.Text;
using MSSQL_Server.Configuration;
using MSSQL_Server.Engine;
using MSSQL_Server.Logging;
using MSSQL_Server.Security;

namespace MSSQL_Server.Tds
{
    /// <summary>One client connection: PRELOGIN -> LOGIN7 -> SQL batches. No TLS yet.</summary>
    internal sealed class TdsSession
    {
        private enum Phase { PreLogin, Login, Ready }

        private readonly Stream stream;
        private readonly ServerConfiguration config;
        private readonly ServerLogger log;
        private readonly LoginThrottle throttle;
        private readonly string remote;
        private readonly string remoteIp;
        private readonly Func<CancellationToken> armReadTimeout;
        private readonly SqlEngine engine;
        private readonly SqlSession sqlSession;
        private static int nextSpid = 50;

        public TdsSession(
            Stream stream, ServerConfiguration config, ServerLogger log,
            LoginThrottle throttle, string remote, string remoteIp,
            Func<CancellationToken> armReadTimeout, SqlEngine engine,
            string localIp, int localPort, int clientPort)
        {
            this.stream = stream;
            this.config = config;
            this.log = log;
            this.throttle = throttle;
            this.remote = remote;
            this.remoteIp = remoteIp;
            this.armReadTimeout = armReadTimeout;
            this.engine = engine;
            sqlSession = new SqlSession
            {
                SessionId = Interlocked.Increment(ref nextSpid),
                // endpoints for CONNECTIONPROPERTY (queried by SSMS/SMO during connect)
                ClientAddress = remoteIp,
                ClientPort = clientPort,
                LocalAddress = localIp,
                LocalPort = localPort,
            };
        }

        public async Task RunAsync(CancellationToken stopToken)
        {
            try { await RunCoreAsync(stopToken); }
            finally { engine.EndSession(sqlSession); }
        }

        private async Task RunCoreAsync(CancellationToken stopToken)
        {
            var phase = Phase.PreLogin;

            while (!stopToken.IsCancellationRequested)
            {
                var msg = await TdsTransport.ReadMessageAsync(stream, armReadTimeout());
                if (msg == null) return;

                switch (phase)
                {
                    case Phase.PreLogin:
                        if (msg.Type != TdsType.PreLogin)
                        {
                            log.Warn($"{remote}: expected PRELOGIN, got packet type {msg.Type}.");
                            return;
                        }
                        await Send(BuildPreLoginResponse(), stopToken);
                        phase = Phase.Login;
                        break;

                    case Phase.Login:
                        if (msg.Type != TdsType.Login7)
                        {
                            log.Warn($"{remote}: expected LOGIN7, got packet type {msg.Type}.");
                            return;
                        }
                        if (!await HandleLogin(msg.Payload, stopToken)) return;
                        phase = Phase.Ready;
                        break;

                    case Phase.Ready:
                        await HandleRequest(msg, stopToken);
                        break;
                }
            }
        }

        private Task Send(byte[] payload, CancellationToken ct) =>
            TdsTransport.WriteMessageAsync(stream, TdsType.Reply, payload, ct);

        // ---------------------------------------------------------------- PRELOGIN

        private static byte[] BuildPreLoginResponse()
        {
            // Option table: VERSION, ENCRYPTION, INSTOPT, MARS, terminator = 4*5 + 1 bytes.
            var tb = new TokenBuilder();
            int offset = 21;
            void Option(byte token, int length)
            {
                tb.Byte(token).U16BigEndian(offset).U16BigEndian(length);
                offset += length;
            }
            Option(0x00, 6);
            Option(0x01, 1);
            Option(0x02, 1);
            Option(0x04, 1);
            tb.Byte(0xFF);

            tb.Bytes(new byte[] { 16, 0, 0x0F, 0xA0, 0, 0 }); // version
            tb.Byte(0x02);                                    // ENCRYPT_NOT_SUP
            tb.Byte(0x00);                                    // instance ok
            tb.Byte(0x00);                                    // MARS off
            return tb.ToArray();
        }

        // ---------------------------------------------------------------- LOGIN7

        private async Task<bool> HandleLogin(byte[] d, CancellationToken ct)
        {
            if (throttle.IsLocked(remoteIp))
            {
                log.Warn($"{remote}: login rejected, address temporarily locked.");
                await SendLoginFailure("(locked)", ct);
                return false;
            }

            if (!TryParseLogin(d, out string user, out string password, out bool integrated))
            {
                log.Warn($"{remote}: malformed LOGIN7.");
                return false;
            }

            bool ok = !integrated
                && config.HasAdminPassword
                && string.Equals(user, config.AdminLogin, StringComparison.OrdinalIgnoreCase)
                && PasswordHasher.Verify(password, config.AdminPasswordSalt, config.AdminPasswordHash);

            if (!ok)
            {
                throttle.Failure(remoteIp);
                log.Warn($"{remote}: login failed for '{Sanitize(user)}'" +
                         (integrated ? " (Windows authentication is not supported)." : "."));
                await SendLoginFailure(user, ct);
                return false;
            }

            throttle.Success(remoteIp);
            log.Info($"{remote}: login succeeded for '{Sanitize(user)}'.");

            // Echo the client's TDS version (e.g. 0x74000004 = 7.4), capped at 7.4.
            uint tdsVersion = BitConverter.ToUInt32(d, 4);
            uint ack = Math.Min(tdsVersion, 0x74000004u);

            await Send(TdsTokens.Concat(
                TdsTokens.EnvChangeToken(1, "master", string.Empty),
                TdsTokens.EnvChangeToken(2, "us_english", string.Empty),   // language
                TdsTokens.EnvChangeCollationToken(),                        // SQL collation (type 7)
                TdsTokens.EnvChangeToken(4, TdsTransport.ServerPacketSize.ToString(), "4096"),
                TdsTokens.LoginAckToken(ack, "MSSQL Server (unofficial)"),
                TdsTokens.DoneToken(TdsTokens.DoneFinal, 0, 0)), ct);
            return true;
        }

        private async Task SendLoginFailure(string user, CancellationToken ct) =>
            await Send(TdsTokens.Concat(
                TdsTokens.ErrorToken(18456, 1, 14, $"Login failed for user '{Sanitize(user)}'.", config.ServerName),
                TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0)), ct);

        private static bool TryParseLogin(byte[] d, out string user, out string password, out bool integrated)
        {
            user = password = string.Empty;
            integrated = false;
            if (d.Length < 94) return false;

            integrated = (d[25] & 0x80) != 0; // OptionFlags2.fIntSecurity
            try
            {
                user = ReadUcs2(d, 40);
                password = DecodePassword(d, 44);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                return false;
            }
        }

        private static string ReadUcs2(byte[] d, int fieldOffset)
        {
            int ib = BitConverter.ToUInt16(d, fieldOffset);
            int cch = BitConverter.ToUInt16(d, fieldOffset + 2);
            if (ib + cch * 2 > d.Length) throw new InvalidDataException();
            return Encoding.Unicode.GetString(d, ib, cch * 2);
        }

        private static string DecodePassword(byte[] d, int fieldOffset)
        {
            int ib = BitConverter.ToUInt16(d, fieldOffset);
            int cch = BitConverter.ToUInt16(d, fieldOffset + 2);
            if (ib + cch * 2 > d.Length) throw new InvalidDataException();

            var raw = new byte[cch * 2];
            for (int i = 0; i < raw.Length; i++)
            {
                byte b = (byte)(d[ib + i] ^ 0xA5);
                raw[i] = (byte)(((b >> 4) & 0x0F) | ((b & 0x0F) << 4));
            }
            return Encoding.Unicode.GetString(raw);
        }

        private static string Sanitize(string s)
        {
            var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length > 64 ? clean[..64] : clean;
        }

        // ---------------------------------------------------------------- requests

        private async Task HandleRequest(TdsMessage msg, CancellationToken ct)
        {
            switch (msg.Type)
            {
                case TdsType.SqlBatch:
                    await Send(ExecuteBatch(msg.Payload), ct);
                    break;

                case TdsType.Rpc:
                    await Send(ExecuteRpc(msg.Payload), ct);
                    break;

                case TdsType.Attention:
                    await Send(TdsTokens.DoneToken(TdsTokens.DoneAttention, 0, 0), ct);
                    break;

                default:
                    await Send(TdsTokens.Concat(
                        TdsTokens.ErrorToken(40000, 1, 16,
                            "This request type is not supported yet by this server.", config.ServerName),
                        TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0)), ct);
                    break;
            }
        }

        private byte[] ErrorReply(int number, byte severity, string message, bool rpc) =>
            TdsTokens.Concat(
                TdsTokens.ErrorToken(number, 1, severity, message, config.ServerName),
                rpc ? TdsTokens.Concat(TdsTokens.ReturnStatusToken(-6),
                                       TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0, TdsTokens.DoneProc))
                    : TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0));

        private byte[] ExecuteRpc(byte[] payload)
        {
            RpcRequest rpc;
            try { rpc = RpcParser.Parse(payload); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                log.Warn($"{remote}: RPC rejected: {ex.Message}");
                return ErrorReply(8114, 16, ex.Message, rpc: true);
            }

            bool isExecuteSql = rpc.ProcId == RpcParser.SpExecuteSql ||
                string.Equals(rpc.ProcName, "sp_executesql", StringComparison.OrdinalIgnoreCase);
            if (!isExecuteSql)
            {
                log.Warn($"{remote}: RPC to unsupported procedure '{rpc.ProcName ?? "#" + rpc.ProcId}'.");
                return ErrorReply(2812, 16, $"Could not find stored procedure '{rpc.ProcName ?? "#" + rpc.ProcId}'.", rpc: true);
            }

            if (rpc.Params.Count == 0 || rpc.Params[0].Value is not string sql)
                return ErrorReply(201, 16, "Procedure or function 'sp_executesql' expects parameter '@stmt', which was not supplied.", rpc: true);

            sqlSession.Parameters.Clear();
            for (int i = 2; i < rpc.Params.Count; i++)
            {
                string name = rpc.Params[i].Name;
                if (name.Length > 0 && name[0] != '@') name = "@" + name;
                if (name.Length > 1) sqlSession.Parameters[name] = rpc.Params[i].Value;
            }
            try { return BatchExecutor.Execute(sql, engine, sqlSession, config, rpc: true, log: log); }
            finally { sqlSession.Parameters.Clear(); }
        }

        private byte[] ExecuteBatch(byte[] payload)
        {
            // TDS 7.2+: payload starts with ALL_HEADERS (4-byte total length, includes itself).
            if (payload.Length < 4)
                return TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0);

            int headers = (int)BitConverter.ToUInt32(payload, 0);
            if (headers < 4 || headers > payload.Length)
                return TdsTokens.Concat(
                    TdsTokens.ErrorToken(40001, 1, 16, "Malformed SQL batch.", config.ServerName),
                    TdsTokens.DoneToken(TdsTokens.DoneError, 0, 0));

            string sql = Encoding.Unicode.GetString(payload, headers, (payload.Length - headers) & ~1);
            return BatchExecutor.Execute(sql, engine, sqlSession, config, log: log);
        }
    }
}