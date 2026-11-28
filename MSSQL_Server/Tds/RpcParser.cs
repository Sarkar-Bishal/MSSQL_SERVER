using System.Text;

namespace MSSQL_Server.Tds
{
    internal sealed record RpcParam(string Name, object? Value);
    internal sealed record RpcRequest(string? ProcName, int? ProcId, List<RpcParam> Params);

    /// <summary>Parses a TDS RPC request (packet type 3). Supports common scalar parameter types.</summary>
    internal static class RpcParser
    {
        public const int SpExecuteSql = 10;

        public static RpcRequest Parse(byte[] d)
        {
            int p = 0;
            Need(d, p, 4);
            int headers = (int)BitConverter.ToUInt32(d, 0);
            if (headers < 4 || headers > d.Length) throw new InvalidDataException("Bad RPC headers.");
            p = headers;

            Need(d, p, 2);
            int nameLen = BitConverter.ToUInt16(d, p); p += 2;
            string? name = null; int? id = null;
            if (nameLen == 0xFFFF) { Need(d, p, 2); id = BitConverter.ToUInt16(d, p); p += 2; }
            else { Need(d, p, nameLen * 2); name = Encoding.Unicode.GetString(d, p, nameLen * 2); p += nameLen * 2; }

            Need(d, p, 2); p += 2; // option flags

            var ps = new List<RpcParam>();
            while (p < d.Length)
            {
                if (d[p] == 0x80 || d[p] == 0xFF)
                    throw new NotSupportedException("Batched RPC requests are not supported.");

                int n = d[p++];
                Need(d, p, n * 2);
                string pname = Encoding.Unicode.GetString(d, p, n * 2); p += n * 2;
                Need(d, p, 1); p++; // status flags (by-ref / default)
                ps.Add(new RpcParam(pname, ReadValue(d, ref p)));
            }
            return new RpcRequest(name, id, ps);
        }

        private static void Need(byte[] d, int p, int n)
        {
            if (p < 0 || n < 0 || p + n > d.Length) throw new InvalidDataException("Truncated RPC request.");
        }

        private static object? ReadValue(byte[] d, ref int p)
        {
            Need(d, p, 1);
            byte type = d[p++];
            switch (type)
            {
                case 0x1F: return null;
                case 0x30: Need(d, p, 1); return (long)(sbyte)0 + d[p++];
                case 0x32: Need(d, p, 1); return d[p++] != 0;
                case 0x34: Need(d, p, 2); { long v = BitConverter.ToInt16(d, p); p += 2; return v; }
                case 0x38: Need(d, p, 4); { long v = BitConverter.ToInt32(d, p); p += 4; return v; }
                case 0x7F: Need(d, p, 8); { long v = BitConverter.ToInt64(d, p); p += 8; return v; }
                case 0x3B: Need(d, p, 4); { double v = BitConverter.ToSingle(d, p); p += 4; return v; }
                case 0x3E: Need(d, p, 8); { double v = BitConverter.ToDouble(d, p); p += 8; return v; }
                case 0x63:
                case 0x23: // NTEXT / TEXT: max length (4) + collation (5), then LONGLEN + data
                    {
                        Need(d, p, 9); p += 9;
                        Need(d, p, 4);
                        uint tlen = BitConverter.ToUInt32(d, p); p += 4;
                        if (tlen == 0xFFFFFFFF) return null;
                        if (tlen > (uint)(d.Length - p)) throw new InvalidDataException("Truncated RPC request.");
                        var tdata = d.AsSpan(p, (int)tlen).ToArray(); p += (int)tlen;
                        return (type == 0x63 ? Encoding.Unicode : Encoding.Latin1).GetString(tdata);
                    }
                case 0x26: // INTN
                {
                    Need(d, p, 2); p++; // max length
                    int len = d[p++]; Need(d, p, len);
                    object? v = len switch
                    {
                        0 => null,
                        1 => (long)d[p],
                        2 => (long)BitConverter.ToInt16(d, p),
                        4 => (long)BitConverter.ToInt32(d, p),
                        8 => BitConverter.ToInt64(d, p),
                        _ => throw new InvalidDataException("Bad INTN length.")
                    };
                    p += len; return v;
                }
                case 0x68: // BITN
                {
                    Need(d, p, 2); p++;
                    int len = d[p++]; Need(d, p, len);
                    object? v = len == 0 ? null : d[p] != 0;
                    p += len; return v;
                }
                case 0x6D: // FLTN
                {
                    Need(d, p, 2); p++;
                    int len = d[p++]; Need(d, p, len);
                    object? v = len switch
                    {
                        0 => null,
                        4 => (double)BitConverter.ToSingle(d, p),
                        8 => BitConverter.ToDouble(d, p),
                        _ => throw new InvalidDataException("Bad FLTN length.")
                    };
                    p += len; return v;
                }
                case 0x6A: case 0x6C: // DECIMALN / NUMERICN
                {
                    Need(d, p, 3); p++; p++; int scale = d[p++];
                    Need(d, p, 1); int len = d[p++];
                    if (len == 0) return null;
                    Need(d, p, len);
                    bool positive = d[p] != 0;
                    decimal mant = 0, mul = 1;
                    for (int i = 1; i < len; i++) { mant += d[p + i] * mul; if (i < len - 1) mul *= 256; }
                    p += len;
                    double val = (double)mant / Math.Pow(10, scale);
                    return positive ? val : -val;
                }
                case 0xE7: case 0xEF: case 0xA7: case 0xAF: // (N)VARCHAR / (N)CHAR
                {
                    Need(d, p, 7);
                    int maxLen = BitConverter.ToUInt16(d, p); p += 2 + 5; // length + collation
                    bool unicode = type is 0xE7 or 0xEF;
                    byte[]? data;
                    if (maxLen == 0xFFFF) data = ReadPlp(d, ref p);
                    else
                    {
                        Need(d, p, 2);
                        int len = BitConverter.ToUInt16(d, p); p += 2;
                        if (len == 0xFFFF) data = null;
                        else { Need(d, p, len); data = d.AsSpan(p, len).ToArray(); p += len; }
                    }
                    return data == null ? null : (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(data);
                }
                default:
                    throw new NotSupportedException($"Parameter type 0x{type:X2} is not supported yet.");
            }
        }

        private static byte[]? ReadPlp(byte[] d, ref int p)
        {
            Need(d, p, 8);
            ulong total = BitConverter.ToUInt64(d, p); p += 8;
            if (total == ulong.MaxValue) return null;
            using var ms = new MemoryStream();
            while (true)
            {
                Need(d, p, 4);
                int chunk = (int)BitConverter.ToUInt32(d, p); p += 4;
                if (chunk == 0) break;
                Need(d, p, chunk);
                ms.Write(d, p, chunk); p += chunk;
                if (ms.Length > 4 * 1024 * 1024) throw new InvalidDataException("Parameter too large.");
            }
            return ms.ToArray();
        }
    }
}
