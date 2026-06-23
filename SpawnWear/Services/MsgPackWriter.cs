using System;
using System.Text;

namespace SpawnWear.Services
{
    /// <summary>
    /// Minimal MessagePack ENCODER for nanoFramework. The full MessagePack-CSharp library can't run
    /// here (it needs Span, source generators, and reflection the nanoFramework BCL lacks), but the
    /// MessagePack wire format is simple for the subset app payloads need: maps, strings, ints, floats,
    /// bools, nil. The Companion (.NET) and PWA (JS wrapper) decode with the full libraries.
    ///
    /// <para>This is the encoder app channels use to put typed, self-describing, evolvable messages on
    /// the transport bus - the template for the AI Assistant message layer. System channels keep their
    /// compact fixed binary schemas; app channels default to MessagePack.</para>
    ///
    /// <para>MessagePack is BIG-endian (network byte order); ESP32 is little-endian, so multi-byte
    /// values are written most-significant-byte first by hand.</para>
    /// </summary>
    public class MsgPackWriter
    {
        byte[] _buf;
        int _pos;

        public MsgPackWriter(int capacity = 128)
        {
            _buf = new byte[capacity < 8 ? 8 : capacity];
        }

        void Ensure(int n)
        {
            if (_pos + n <= _buf.Length) return;
            int cap = _buf.Length * 2;
            if (cap < _pos + n) cap = _pos + n;
            byte[] grown = new byte[cap];
            Array.Copy(_buf, grown, _pos);
            _buf = grown;
        }

        void Put(byte b)
        {
            Ensure(1);
            _buf[_pos++] = b;
        }

        /// <summary>Begin a map with <paramref name="count"/> key/value pairs. Write that many
        /// key then value items after this.</summary>
        public void WriteMapHeader(int count)
        {
            if (count < 16)
            {
                Put((byte)(0x80 | count));         // fixmap
            }
            else
            {
                Put(0xde);                          // map16
                Put((byte)((count >> 8) & 0xFF));
                Put((byte)(count & 0xFF));
            }
        }

        /// <summary>Begin an array with <paramref name="count"/> elements.</summary>
        public void WriteArrayHeader(int count)
        {
            if (count < 16)
            {
                Put((byte)(0x90 | count));          // fixarray
            }
            else
            {
                Put(0xdc);                          // array16
                Put((byte)((count >> 8) & 0xFF));
                Put((byte)(count & 0xFF));
            }
        }

        public void WriteString(string s)
        {
            if (s == null) { WriteNil(); return; }
            byte[] u = Encoding.UTF8.GetBytes(s);
            int len = u.Length;
            if (len < 32)
            {
                Put((byte)(0xa0 | len));            // fixstr
            }
            else if (len < 256)
            {
                Put(0xd9);                          // str8
                Put((byte)len);
            }
            else
            {
                Put(0xda);                          // str16
                Put((byte)((len >> 8) & 0xFF));
                Put((byte)(len & 0xFF));
            }
            Ensure(len);
            Array.Copy(u, 0, _buf, _pos, len);
            _pos += len;
        }

        public void WriteInt(int v)
        {
            if (v >= 0 && v <= 0x7f)
            {
                Put((byte)v);                       // positive fixint
            }
            else if (v < 0 && v >= -32)
            {
                Put((byte)(0xe0 | (v & 0x1f)));     // negative fixint
            }
            else
            {
                Put(0xd2);                          // int32 (big-endian)
                Put((byte)((v >> 24) & 0xFF));
                Put((byte)((v >> 16) & 0xFF));
                Put((byte)((v >> 8) & 0xFF));
                Put((byte)(v & 0xFF));
            }
        }

        public void WriteFloat(float f)
        {
            Put(0xca);                              // float32 (big-endian)
            // nanoFramework BitConverter.GetBytes(float) yields little-endian bytes; reverse for MP.
            byte[] le = BitConverter.GetBytes(f);
            Put(le[3]);
            Put(le[2]);
            Put(le[1]);
            Put(le[0]);
        }

        public void WriteBool(bool b) => Put(b ? (byte)0xc3 : (byte)0xc2);

        public void WriteNil() => Put(0xc0);

        /// <summary>The encoded MessagePack bytes (exact length).</summary>
        public byte[] ToArray()
        {
            byte[] r = new byte[_pos];
            Array.Copy(_buf, r, _pos);
            return r;
        }
    }
}
