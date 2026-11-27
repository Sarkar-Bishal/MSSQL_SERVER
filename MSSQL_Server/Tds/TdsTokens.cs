namespace MSSQL_Server.Tds
{
    internal static class TdsTokens
    {
        public const byte ColMetaData = 0x81;
        public const byte Error = 0xAA;
        public const byte LoginAck = 0xAD;
        public const byte Row = 0xD1;
        public const byte EnvChange = 0xE3;
        public const byte Done = 0xFD;
        public const byte DoneProc = 0xFE;
        public const byte DoneInProc = 0xFF;
        public const byte ReturnStatus = 0x79;

        public const ushort DoneFinal = 0x0000;
        public const ushort DoneError = 0x0002;
        public const ushort DoneCount = 0x0010;
        public const ushort DoneAttention = 0x0020;

        public const byte TypeIntN = 0x26;
        public const byte TypeBitN = 0x68;
        public const byte TypeFloatN = 0x6D;
        public const ushort DoneMore = 0x0001;
        public const byte TypeNVarChar = 0xE7;

        private static readonly byte[] DefaultCollation = { 0x09, 0x04, 0xD0, 0x00, 0x34 };

        public static byte[] DoneToken(ushort status, ushort curCmd, ulong rowCount, byte token = Done) =>
            new TokenBuilder().Byte(token).U16(status).U16(curCmd).U64(rowCount).ToArray();

        public static byte[] ErrorToken(int number, byte state, byte severity, string message, string server)
        {
            var body = new TokenBuilder()
                .U32((uint)number).Byte(state).Byte(severity)
                .UsVarChar(message).BVarChar(server).BVarChar(string.Empty).U32(1)
                .ToArray();
            return new TokenBuilder().Byte(Error).U16(body.Length).Bytes(body).ToArray();
        }

        public static byte[] EnvChangeToken(byte type, string newValue, string oldValue)
        {
            var body = new TokenBuilder().Byte(type).BVarChar(newValue).BVarChar(oldValue).ToArray();
            return new TokenBuilder().Byte(EnvChange).U16(body.Length).Bytes(body).ToArray();
        }

        public static byte[] LoginAckToken(uint tdsVersion, string programName)
        {
            var body = new TokenBuilder()
                .Byte(1)                       // SQL_TSQL interface
                .U32BigEndian(tdsVersion)
                .BVarChar(programName)
                .Byte(16).Byte(0).Byte(0x0F).Byte(0xA0) // major, minor, build hi/lo
                .ToArray();
            return new TokenBuilder().Byte(LoginAck).U16(body.Length).Bytes(body).ToArray();
        }

        public static byte[] ReturnStatusToken(int value) =>
            new TokenBuilder().Byte(ReturnStatus).U32((uint)value).ToArray();

        public static byte[] ResultSetTokens(MSSQL_Server.Engine.ResultSet rs)
        {
            var tb = new TokenBuilder();
            tb.Byte(ColMetaData).U16(rs.Columns.Count);
            foreach (var c in rs.Columns)
            {
                tb.U32(0).U16(0x0001); // user type, flags: nullable
                switch (c.Type)
                {
                    case MSSQL_Server.Sql.DataType.Int: tb.Byte(TypeIntN).Byte(4); break;
                    case MSSQL_Server.Sql.DataType.BigInt: tb.Byte(TypeIntN).Byte(8); break;
                    case MSSQL_Server.Sql.DataType.Bit: tb.Byte(TypeBitN).Byte(1); break;
                    case MSSQL_Server.Sql.DataType.Float: tb.Byte(TypeFloatN).Byte(8); break;
                    default:
                        tb.Byte(TypeNVarChar).U16(Math.Clamp(c.MaxChars * 2, 2, 8000)).Bytes(DefaultCollation);
                        break;
                }
                tb.BVarChar(c.Name);
            }

            foreach (var row in rs.Rows)
            {
                tb.Byte(Row);
                for (int i = 0; i < rs.Columns.Count; i++)
                {
                    var v = row[i];
                    switch (rs.Columns[i].Type)
                    {
                        case MSSQL_Server.Sql.DataType.Int:
                            if (v == null) tb.Byte(0); else tb.Byte(4).U32((uint)(int)(long)v);
                            break;
                        case MSSQL_Server.Sql.DataType.BigInt:
                            if (v == null) tb.Byte(0); else tb.Byte(8).U64((ulong)(long)v);
                            break;
                        case MSSQL_Server.Sql.DataType.Bit:
                            if (v == null) tb.Byte(0); else tb.Byte(1).Byte((bool)v ? (byte)1 : (byte)0);
                            break;
                        case MSSQL_Server.Sql.DataType.Float:
                            if (v == null) tb.Byte(0); else tb.Byte(8).U64((ulong)BitConverter.DoubleToInt64Bits((double)v));
                            break;
                        default:
                            if (v == null) tb.U16(0xFFFF);
                            else
                            {
                                var data = System.Text.Encoding.Unicode.GetBytes((string)v);
                                tb.U16(data.Length).Bytes(data);
                            }
                            break;
                    }
                }
            }
            return tb.ToArray();
        }

        public static byte[] Concat(params byte[][] parts)
        {
            var tb = new TokenBuilder();
            foreach (var p in parts) tb.Bytes(p);
            return tb.ToArray();
        }
    }
}
