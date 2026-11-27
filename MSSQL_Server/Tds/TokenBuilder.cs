using System.Text;

namespace MSSQL_Server.Tds
{
    /// <summary>Little-endian byte builder for TDS token streams.</summary>
    internal sealed class TokenBuilder
    {
        private readonly MemoryStream ms = new();

        public int Length => (int)ms.Length;

        public TokenBuilder Byte(byte b) { ms.WriteByte(b); return this; }

        public TokenBuilder U16(int v)
        {
            ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); return this;
        }

        public TokenBuilder U16BigEndian(int v)
        {
            ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); return this;
        }

        public TokenBuilder U32(uint v)
        {
            for (int i = 0; i < 4; i++) ms.WriteByte((byte)(v >> (8 * i)));
            return this;
        }

        public TokenBuilder U32BigEndian(uint v)
        {
            for (int i = 3; i >= 0; i--) ms.WriteByte((byte)(v >> (8 * i)));
            return this;
        }

        public TokenBuilder U64(ulong v)
        {
            for (int i = 0; i < 8; i++) ms.WriteByte((byte)(v >> (8 * i)));
            return this;
        }

        public TokenBuilder Bytes(byte[] b) { ms.Write(b, 0, b.Length); return this; }

        /// <summary>1-byte character count followed by UTF-16LE text.</summary>
        public TokenBuilder BVarChar(string s)
        {
            Byte((byte)s.Length);
            return Bytes(Encoding.Unicode.GetBytes(s));
        }

        /// <summary>2-byte character count followed by UTF-16LE text.</summary>
        public TokenBuilder UsVarChar(string s)
        {
            U16(s.Length);
            return Bytes(Encoding.Unicode.GetBytes(s));
        }

        public byte[] ToArray() => ms.ToArray();
    }
}
