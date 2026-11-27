namespace MSSQL_Server.Tds
{
    internal static class TdsType
    {
        public const byte SqlBatch = 0x01;
        public const byte Rpc = 0x03;
        public const byte Reply = 0x04;
        public const byte Attention = 0x06;
        public const byte Login7 = 0x10;
        public const byte PreLogin = 0x12;
    }

    internal sealed record TdsMessage(byte Type, byte[] Payload);

    internal static class TdsTransport
    {
        public const int ServerPacketSize = 4096;
        private const int MaxIncomingPacket = 32768;
        private const int MaxMessageSize = 4 * 1024 * 1024;

        /// <summary>Reads one logical message (all packets up to EOM). Null on clean disconnect.</summary>
        public static async Task<TdsMessage?> ReadMessageAsync(Stream s, CancellationToken ct)
        {
            var header = new byte[8];
            using var ms = new MemoryStream();
            byte type = 0;
            bool first = true;

            while (true)
            {
                int got = await ReadExactAsync(s, header, 8, ct);
                if (got == 0 && first) return null;
                if (got < 8) throw new EndOfStreamException("Truncated TDS header.");

                if (first) { type = header[0]; first = false; }
                else if (header[0] != type)
                    throw new InvalidDataException("TDS packet type changed mid-message.");

                int length = (header[2] << 8) | header[3];
                if (length < 8 || length > MaxIncomingPacket)
                    throw new InvalidDataException("Invalid TDS packet length.");

                int bodyLen = length - 8;
                if (ms.Length + bodyLen > MaxMessageSize)
                    throw new InvalidDataException("TDS message too large.");

                var body = new byte[bodyLen];
                if (await ReadExactAsync(s, body, bodyLen, ct) < bodyLen)
                    throw new EndOfStreamException("Truncated TDS packet.");
                ms.Write(body, 0, bodyLen);

                if ((header[1] & 0x01) != 0) break; // EOM
            }

            return new TdsMessage(type, ms.ToArray());
        }

        public static async Task WriteMessageAsync(
            Stream s, byte type, byte[] payload, CancellationToken ct)
        {
            int max = ServerPacketSize - 8;
            int offset = 0;
            byte packetId = 1;

            do
            {
                int n = Math.Min(max, payload.Length - offset);
                bool last = offset + n >= payload.Length;
                var pkt = new byte[8 + n];
                pkt[0] = type;
                pkt[1] = last ? (byte)0x01 : (byte)0x00;
                pkt[2] = (byte)((8 + n) >> 8);
                pkt[3] = (byte)(8 + n);
                pkt[6] = packetId++;
                Buffer.BlockCopy(payload, offset, pkt, 8, n);
                await s.WriteAsync(pkt, ct);
                offset += n;
            }
            while (offset < payload.Length);

            await s.FlushAsync(ct);
        }

        private static async Task<int> ReadExactAsync(
            Stream s, byte[] buffer, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }
}
