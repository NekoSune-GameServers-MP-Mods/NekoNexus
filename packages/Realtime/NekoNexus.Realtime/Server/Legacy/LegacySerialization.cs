using System;
using System.Collections.Generic;
using System.Text;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// Port of the 4.3.10 client's RealtimeSerialization / DefaultByteConverter for the types the
	/// server needs. Every value is written as [CmuneDataType byte][payload]. Numbers are
	/// BIG-endian (the client reverses BitConverter output on little-endian CPUs); strings are
	/// [int16 BE char count][UTF-16LE chars]; byte arrays are [int32 BE count][bytes].
	/// Game-specific types (ActorInfo, GameData, Vector3 ...) are not handled yet and raise
	/// <see cref="LegacyProtocolException"/> so callers can log them.
	/// </summary>
	public static class LegacySerialization {
		public static class Type {
			public const byte None = 0x00;
			public const byte Byte = 0x01;
			public const byte SByte = 0x02;
			public const byte Bool = 0x03;
			public const byte Int16 = 0x04;
			public const byte UInt16 = 0x05;
			public const byte Int32 = 0x06;
			public const byte Long = 0x08;
			public const byte Float = 0x0A;
			public const byte String = 0x0C;
			public const byte ArrayByte = 0x0F;
			/// <summary>Named PerformanceData in CmuneDataType; carries ServerLoadData.</summary>
			public const byte ServerLoadData = 0x2F;
		}

		// ---- encode ----------------------------------------------------------------

		public static byte[] ToBytes(params object[] args) {
			var bytes = new List<byte>();
			if (args != null) {
				foreach (var arg in args) WriteObject(bytes, arg);
			}
			return bytes.ToArray();
		}

		public static void WriteObject(List<byte> bytes, object value) {
			switch (value) {
				case byte b: bytes.Add(Type.Byte); bytes.Add(b); break;
				case sbyte sb: bytes.Add(Type.SByte); bytes.Add(unchecked((byte)sb)); break;
				case bool bo: bytes.Add(Type.Bool); bytes.Add(bo ? (byte)1 : (byte)0); break;
				case short s: bytes.Add(Type.Int16); WriteBigEndian(bytes, BitConverter.GetBytes(s)); break;
				case ushort us: bytes.Add(Type.UInt16); WriteBigEndian(bytes, BitConverter.GetBytes(us)); break;
				case int i: bytes.Add(Type.Int32); WriteInt(bytes, i); break;
				case long l: bytes.Add(Type.Long); WriteBigEndian(bytes, BitConverter.GetBytes(l)); break;
				case float f: bytes.Add(Type.Float); WriteFloat(bytes, f); break;
				case string str: bytes.Add(Type.String); WriteString(bytes, str); break;
				case byte[] arr: bytes.Add(Type.ArrayByte); WriteInt(bytes, arr.Length); bytes.AddRange(arr); break;
				case LegacyServerLoadData load:
					bytes.Add(Type.ServerLoadData);
					WriteInt(bytes, load.PlayersConnected);
					WriteInt(bytes, load.PeersConnected);
					WriteInt(bytes, load.RoomsCreated);
					WriteFloat(bytes, load.MaxPlayerCount);
					break;
				default:
					throw new LegacyProtocolException($"cannot serialize {value?.GetType().Name ?? "null"} for a 4.3.10 client");
			}
		}

		public static void WriteInt(List<byte> bytes, int value) => WriteBigEndian(bytes, BitConverter.GetBytes(value));
		public static void WriteFloat(List<byte> bytes, float value) => WriteBigEndian(bytes, BitConverter.GetBytes(value));

		public static void WriteString(List<byte> bytes, string value) {
			if (string.IsNullOrEmpty(value)) {
				bytes.Add(0);
				bytes.Add(0);
				return;
			}
			if (value.Length > 16383) value = value.Substring(0, 16383); // client limit
			WriteBigEndian(bytes, BitConverter.GetBytes((short)value.Length));
			bytes.AddRange(Encoding.Unicode.GetBytes(value));
		}

		private static void WriteBigEndian(List<byte> bytes, byte[] value) {
			if (BitConverter.IsLittleEndian) Array.Reverse(value);
			bytes.AddRange(value);
		}

		// ---- decode ----------------------------------------------------------------

		public static object[] ToObjects(byte[] bytes) {
			var result = new List<object>();
			if (bytes == null) return result.ToArray();
			var i = 0;
			while (i < bytes.Length) result.Add(ReadObject(bytes, ref i));
			return result.ToArray();
		}

		public static object ReadObject(byte[] bytes, ref int i) {
			var type = ReadByte(bytes, ref i);
			switch (type) {
				case Type.None: return null;
				case Type.Byte: return ReadByte(bytes, ref i);
				case Type.SByte: return unchecked((sbyte)ReadByte(bytes, ref i));
				case Type.Bool: return ReadByte(bytes, ref i) != 0;
				case Type.Int16: return BitConverter.ToInt16(ReadBigEndian(bytes, ref i, 2), 0);
				case Type.UInt16: return BitConverter.ToUInt16(ReadBigEndian(bytes, ref i, 2), 0);
				case Type.Int32: return ReadInt(bytes, ref i);
				case Type.Long: return BitConverter.ToInt64(ReadBigEndian(bytes, ref i, 8), 0);
				case Type.Float: return BitConverter.ToSingle(ReadBigEndian(bytes, ref i, 4), 0);
				case Type.String: return ReadString(bytes, ref i);
				case Type.ArrayByte: {
					var n = ReadInt(bytes, ref i);
					Require(bytes, i, n);
					var arr = new byte[n];
					Buffer.BlockCopy(bytes, i, arr, 0, n);
					i += n;
					return arr;
				}
				case Type.ServerLoadData:
					return new LegacyServerLoadData {
						PlayersConnected = ReadInt(bytes, ref i),
						PeersConnected = ReadInt(bytes, ref i),
						RoomsCreated = ReadInt(bytes, ref i),
						MaxPlayerCount = BitConverter.ToSingle(ReadBigEndian(bytes, ref i, 4), 0),
					};
				default:
					throw new LegacyProtocolException($"unsupported 4.3.10 data type 0x{type:X2} at offset {i - 1}");
			}
		}

		public static int ReadInt(byte[] bytes, ref int i) => BitConverter.ToInt32(ReadBigEndian(bytes, ref i, 4), 0);

		public static string ReadString(byte[] bytes, ref int i) {
			var length = BitConverter.ToInt16(ReadBigEndian(bytes, ref i, 2), 0);
			if (length <= 0) return string.Empty;
			Require(bytes, i, length * 2);
			var s = Encoding.Unicode.GetString(bytes, i, length * 2);
			i += length * 2;
			return s;
		}

		private static byte ReadByte(byte[] bytes, ref int i) {
			Require(bytes, i, 1);
			return bytes[i++];
		}

		private static byte[] ReadBigEndian(byte[] bytes, ref int i, int count) {
			Require(bytes, i, count);
			var value = new byte[count];
			Buffer.BlockCopy(bytes, i, value, 0, count);
			i += count;
			if (BitConverter.IsLittleEndian) Array.Reverse(value);
			return value;
		}

		private static void Require(byte[] bytes, int offset, int count) {
			if (count < 0 || offset + count > bytes.Length)
				throw new LegacyProtocolException($"truncated 4.3.10 payload (need {count} bytes at {offset}, have {bytes.Length})");
		}
	}
}
