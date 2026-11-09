using System;
using System.Collections.Generic;
using System.Text;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// Port of the 4.3.10 client's RealtimeSerialization / DefaultByteConverter for the types the
	/// server needs. Every value is written as [CmuneDataType byte][payload]. Numbers are
	/// BIG-endian (the client reverses BitConverter output on little-endian CPUs); strings are
	/// [int16 BE char count][UTF-16LE chars]; byte arrays are [int32 BE count][bytes].
	/// Also handles the lobby types the comm server needs: CmuneRoomID, RoomMetaData, SyncObject
	/// (CommActorInfo) and their lists. Game-specific types (GameData, CmuneTransform ...) are not
	/// handled yet and raise <see cref="LegacyProtocolException"/> so callers can log them.
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
			public const byte ListInt16 = 0x12;
			public const byte ListInt32 = 0x14;
			public const byte ListString = 0x1A;
			public const byte Vector3 = 0x1E;
			public const byte Quaternion = 0x1F;
			public const byte RoomMetaData = 0x28;
			public const byte RoomId = 0x2D;
			/// <summary>A SyncObject that the client turns into a CommActorInfo.</summary>
			public const byte CommActorInfo = 0x2E;
			public const byte SyncObject = 0x30;
			public const byte ListSyncObject = 0x35;
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
				case LegacyRoomId room: bytes.Add(Type.RoomId); room.Write(bytes); break;
				case LegacySyncObject sync: bytes.Add(sync.AsCommActorInfo ? Type.CommActorInfo : Type.SyncObject); sync.Write(bytes); break;
				case List<LegacySyncObject> list:
					bytes.Add(Type.ListSyncObject);
					WriteBigEndian(bytes, BitConverter.GetBytes((short)list.Count));
					foreach (var item in list) item.Write(bytes);
					break;
				case List<int> ints:
					bytes.Add(Type.ListInt32);
					WriteBigEndian(bytes, BitConverter.GetBytes((short)ints.Count));
					foreach (var item in ints) WriteInt(bytes, item);
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
				case Type.ListInt16: {
					var n = ReadCount(bytes, ref i);
					var list = new List<short>(n);
					for (var k = 0; k < n; k++) list.Add(BitConverter.ToInt16(ReadBigEndian(bytes, ref i, 2), 0));
					return list;
				}
				case Type.ListInt32: {
					var n = ReadCount(bytes, ref i);
					var list = new List<int>(n);
					for (var k = 0; k < n; k++) list.Add(ReadInt(bytes, ref i));
					return list;
				}
				case Type.ListString: {
					var n = ReadCount(bytes, ref i);
					var list = new List<string>(n);
					for (var k = 0; k < n; k++) list.Add(ReadString(bytes, ref i));
					return list;
				}
				case Type.Vector3: return new[] { ReadFloat(bytes, ref i), ReadFloat(bytes, ref i), ReadFloat(bytes, ref i) };
				case Type.Quaternion: return new[] { ReadFloat(bytes, ref i), ReadFloat(bytes, ref i), ReadFloat(bytes, ref i), ReadFloat(bytes, ref i) };
				case Type.RoomMetaData: return LegacyRoomMetaData.Read(bytes, ref i);
				case Type.RoomId: return LegacyRoomId.Read(bytes, ref i);
				case Type.CommActorInfo: {
					var sync = LegacySyncObject.Read(bytes, ref i);
					sync.AsCommActorInfo = true;
					return sync;
				}
				case Type.SyncObject: return LegacySyncObject.Read(bytes, ref i);
				case Type.ListSyncObject: {
					var n = ReadCount(bytes, ref i);
					var list = new List<LegacySyncObject>(n);
					for (var k = 0; k < n; k++) list.Add(LegacySyncObject.Read(bytes, ref i));
					return list;
				}
				default:
					throw new LegacyProtocolException($"unsupported 4.3.10 data type 0x{type:X2} at offset {i - 1}");
			}
		}

		public static int ReadInt(byte[] bytes, ref int i) => BitConverter.ToInt32(ReadBigEndian(bytes, ref i, 4), 0);
		public static float ReadFloat(byte[] bytes, ref int i) => BitConverter.ToSingle(ReadBigEndian(bytes, ref i, 4), 0);
		public static ushort ReadUShort(byte[] bytes, ref int i) => BitConverter.ToUInt16(ReadBigEndian(bytes, ref i, 2), 0);

		private static int ReadCount(byte[] bytes, ref int i) {
			var n = BitConverter.ToInt16(ReadBigEndian(bytes, ref i, 2), 0);
			if (n < 0 || n > 4096) throw new LegacyProtocolException($"invalid collection size {n}");
			return n;
		}

		/// <summary>DefaultByteConverter "small" string: [byte char count][UTF-16LE], max 127 chars.</summary>
		public static string ReadSmallString(byte[] bytes, ref int i) {
			var length = ReadByte(bytes, ref i);
			if (length == 0) return string.Empty;
			Require(bytes, i, length * 2);
			var s = Encoding.Unicode.GetString(bytes, i, length * 2);
			i += length * 2;
			return s;
		}

		public static void WriteSmallString(List<byte> bytes, string value) {
			if (string.IsNullOrEmpty(value)) {
				bytes.Add(0);
				return;
			}
			if (value.Length > 127) value = value.Substring(0, 127);
			bytes.Add((byte)value.Length);
			bytes.AddRange(Encoding.Unicode.GetBytes(value));
		}

		public static byte ReadRawByte(byte[] bytes, ref int i) => ReadByte(bytes, ref i);

		public static byte[] ReadRaw(byte[] bytes, ref int i, int count) {
			Require(bytes, i, count);
			var value = new byte[count];
			Buffer.BlockCopy(bytes, i, value, 0, count);
			i += count;
			return value;
		}

		public static void WriteRawShortBigEndian(List<byte> bytes, short value) => WriteBigEndian(bytes, BitConverter.GetBytes(value));

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

	/// <summary>
	/// UberStrike.Realtime.UnitySdk.CmuneRoomID: [int32 BE room number][ConnectionAddress: 4 IP bytes,
	/// uint16 BE port]. Kept as raw bytes so ids from the client are echoed back unchanged.
	/// </summary>
	public sealed class LegacyRoomId {
		public const int Size = 10;
		public int Number { get; set; }
		public byte[] Address { get; set; } = new byte[6];

		public static LegacyRoomId Read(byte[] bytes, ref int i) {
			var number = LegacySerialization.ReadInt(bytes, ref i);
			return new LegacyRoomId { Number = number, Address = LegacySerialization.ReadRaw(bytes, ref i, 6) };
		}

		public void Write(List<byte> bytes) {
			LegacySerialization.WriteInt(bytes, Number);
			var address = Address ?? new byte[6];
			if (address.Length != 6) throw new LegacyProtocolException("CmuneRoomID address must be 6 bytes");
			bytes.AddRange(address);
		}

		public byte[] ToBytes() {
			var bytes = new List<byte>(Size);
			Write(bytes);
			return bytes.ToArray();
		}

		public string Server => Address == null || Address.Length != 6 ? "" :
			$"{Address[0]}.{Address[1]}.{Address[2]}.{Address[3]}:{(Address[4] << 8) | Address[5]}";

		public override string ToString() => $"{Number}@{Server}";
	}

	/// <summary>UberStrike.Realtime.UnitySdk.RoomMetaData as sent with PhotonGameJoin (0x58).</summary>
	public sealed class LegacyRoomMetaData {
		public string Name { get; set; } = "";
		public string Password { get; set; } = "";
		public byte MaxPlayers { get; set; }
		public byte InPlayers { get; set; }
		public byte Tag { get; set; }
		public LegacyRoomId RoomId { get; set; } = new LegacyRoomId();

		public static LegacyRoomMetaData Read(byte[] bytes, ref int i) => new LegacyRoomMetaData {
			Name = LegacySerialization.ReadSmallString(bytes, ref i),
			Password = LegacySerialization.ReadSmallString(bytes, ref i),
			MaxPlayers = LegacySerialization.ReadRawByte(bytes, ref i),
			InPlayers = LegacySerialization.ReadRawByte(bytes, ref i),
			Tag = LegacySerialization.ReadRawByte(bytes, ref i),
			RoomId = LegacyRoomId.Read(bytes, ref i),
		};

		public override string ToString() => $"'{Name}' {RoomId} ({InPlayers}/{MaxPlayers})";
	}

	/// <summary>
	/// UberStrike.Realtime.UnitySdk.SyncObject: [int32 BE delta mask] and, when the mask is not 0,
	/// [int32 BE id] followed by one type-prefixed value per set bit. The client walks bits 0..32
	/// with <c>1 &lt;&lt; (i &amp; 31)</c>, so bit 0 is visited twice (i = 0 and i = 32); this
	/// class reproduces that exactly, otherwise lists of SyncObjects go out of step.
	/// </summary>
	public sealed class LegacySyncObject {
		public int Id { get; set; }
		public SortedDictionary<int, object> Fields { get; } = new SortedDictionary<int, object>();
		/// <summary>Write with CmuneDataType 0x2E (CommActorInfo) instead of 0x30 (SyncObject).</summary>
		public bool AsCommActorInfo { get; set; }

		public int DeltaCode {
			get {
				var code = 0;
				foreach (var key in Fields.Keys) code |= key;
				return code;
			}
		}

		public object this[int tag] {
			get => Fields.TryGetValue(tag, out var v) ? v : null;
			set { if (value == null) Fields.Remove(tag); else Fields[tag] = value; }
		}

		public static LegacySyncObject Read(byte[] bytes, ref int i) {
			var sync = new LegacySyncObject();
			var delta = LegacySerialization.ReadInt(bytes, ref i);
			if (delta == 0) return sync;
			sync.Id = LegacySerialization.ReadInt(bytes, ref i);
			for (var bit = 0; bit <= 32 && i < bytes.Length; bit++) {
				var mask = 1 << (bit & 31);
				if ((delta & mask) == 0) continue;
				sync.Fields[mask] = LegacySerialization.ReadObject(bytes, ref i);
			}
			return sync;
		}

		public void Write(List<byte> bytes) {
			var delta = DeltaCode;
			LegacySerialization.WriteInt(bytes, delta);
			if (delta == 0) return;
			LegacySerialization.WriteInt(bytes, Id);
			for (var bit = 0; bit <= 32; bit++) {
				if (Fields.TryGetValue(1 << (bit & 31), out var value)) LegacySerialization.WriteObject(bytes, value);
			}
		}
	}

	/// <summary>Field bits of UberStrike.Realtime.UnitySdk.ActorInfo / CommActorInfo (FieldTag).</summary>
	public static class LegacyActorField {
		public const int Cmid = 0x01;
		public const int PlayerName = 0x02;
		public const int Channel = 0x04;
		public const int CurrentRoom = 0x08;
		public const int Ping = 0x10;
		public const int ClanTag = 0x20;
		public const int AccessLevel = 0x40;
		public const int ModInformation = 0x200;
		public const int ModerationFlag = 0x400;
	}
}
