using System;
using System.Collections.Generic;
using System.Linq;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>A value written as [CmuneDataType byte][payload] by <see cref="LegacySerialization"/>.</summary>
	public interface ILegacyTyped {
		void WriteTyped(List<byte> bytes);
	}

	/// <summary>
	/// UberStrike 4.3.10 in-match data types (UberStrike.Realtime.UnitySdk, UberStrikeByteConverter),
	/// recovered from the 4.3.10 client. Every number is little-endian like the rest of the wire.
	/// </summary>
	public static class LegacyGameTypes {
		public static bool TryRead(byte type, byte[] bytes, ref int i, out object value) {
			switch (type) {
				case LegacySerialization.Type.Color: value = LegacyColor.ReadRaw(bytes, ref i); return true;
				case LegacySerialization.Type.ListVector3: {
					var n = LegacySerialization.ReadRawByte(bytes, ref i);
					var list = new List<LegacyVector3>(n);
					for (var k = 0; k < n; k++) list.Add(LegacyVector3.ReadRaw(bytes, ref i));
					value = list;
					return true;
				}
				case LegacySerialization.Type.CharacterInfo: value = new LegacyCharacterInfo(LegacySyncObject.Read(bytes, ref i)); return true;
				case LegacySerialization.Type.ShortVector3: value = LegacyShortVector3.ReadRaw(bytes, ref i); return true;
				case LegacySerialization.Type.ArmorInfo: value = LegacyArmorInfo.ReadRaw(bytes, ref i); return true;
				case LegacySerialization.Type.WeaponInfo: value = LegacyWeaponInfo.ReadRaw(bytes, ref i); return true;
				case LegacySerialization.Type.StatsInfo: value = LegacyStatsInfo.ReadRaw(bytes, ref i); return true;
				case LegacySerialization.Type.DamageEvent: value = LegacyDamageEvent.ReadRaw(bytes, ref i); return true;
				default: value = null; return false;
			}
		}
	}

	public struct LegacyVector3 : ILegacyTyped {
		public float X, Y, Z;
		public LegacyVector3(float x, float y, float z) { X = x; Y = y; Z = z; }

		public void WriteRaw(List<byte> bytes) {
			LegacySerialization.WriteFloat(bytes, X);
			LegacySerialization.WriteFloat(bytes, Y);
			LegacySerialization.WriteFloat(bytes, Z);
		}

		public void WriteTyped(List<byte> bytes) { bytes.Add(LegacySerialization.Type.Vector3); WriteRaw(bytes); }

		public static LegacyVector3 ReadRaw(byte[] bytes, ref int i) =>
			new LegacyVector3(LegacySerialization.ReadFloat(bytes, ref i), LegacySerialization.ReadFloat(bytes, ref i), LegacySerialization.ReadFloat(bytes, ref i));

		public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
	}

	/// <summary>UnityEngine.Color as 3 floats (r, g, b) - alpha is not sent.</summary>
	public struct LegacyColor : ILegacyTyped {
		public float R, G, B;
		public LegacyColor(float r, float g, float b) { R = r; G = g; B = b; }

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.Color);
			LegacySerialization.WriteFloat(bytes, R);
			LegacySerialization.WriteFloat(bytes, G);
			LegacySerialization.WriteFloat(bytes, B);
		}

		public static LegacyColor ReadRaw(byte[] bytes, ref int i) =>
			new LegacyColor(LegacySerialization.ReadFloat(bytes, ref i), LegacySerialization.ReadFloat(bytes, ref i), LegacySerialization.ReadFloat(bytes, ref i));
	}

	/// <summary>ShortVector3: shorts of x*10, y*100, z*10.</summary>
	public struct LegacyShortVector3 : ILegacyTyped {
		public LegacyVector3 Value;
		public LegacyShortVector3(LegacyVector3 value) { Value = value; }

		private static short Clamp(float v) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v)));

		public void WriteRaw(List<byte> bytes) {
			LegacySerialization.WriteShort(bytes, Clamp(Value.X * 10f));
			LegacySerialization.WriteShort(bytes, Clamp(Value.Y * 100f));
			LegacySerialization.WriteShort(bytes, Clamp(Value.Z * 10f));
		}

		public void WriteTyped(List<byte> bytes) { bytes.Add(LegacySerialization.Type.ShortVector3); WriteRaw(bytes); }

		public static LegacyShortVector3 ReadRaw(byte[] bytes, ref int i) {
			var x = LegacySerialization.ReadShort(bytes, ref i);
			var y = LegacySerialization.ReadShort(bytes, ref i);
			var z = LegacySerialization.ReadShort(bytes, ref i);
			return new LegacyShortVector3(new LegacyVector3(x / 10f, y / 100f, z / 10f));
		}
	}

	/// <summary>ArmorInfo: [armor points][capacity][bonus defense] bytes.</summary>
	public sealed class LegacyArmorInfo : ILegacyTyped {
		public byte ArmorPoints, ArmorPointCapacity, BonusDefense;

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.ArmorInfo);
			bytes.Add(ArmorPoints);
			bytes.Add(ArmorPointCapacity);
			bytes.Add(BonusDefense);
		}

		public static LegacyArmorInfo ReadRaw(byte[] bytes, ref int i) => new LegacyArmorInfo {
			ArmorPoints = LegacySerialization.ReadRawByte(bytes, ref i),
			ArmorPointCapacity = LegacySerialization.ReadRawByte(bytes, ref i),
			BonusDefense = LegacySerialization.ReadRawByte(bytes, ref i),
		};
	}

	/// <summary>StatsInfo: [short kills][short deaths][ushort xp][ushort points].</summary>
	public sealed class LegacyStatsInfo : ILegacyTyped {
		public short Kills, Deaths;
		public ushort Xp, Points;

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.StatsInfo);
			LegacySerialization.WriteShort(bytes, Kills);
			LegacySerialization.WriteShort(bytes, Deaths);
			LegacySerialization.WriteUShort(bytes, Xp);
			LegacySerialization.WriteUShort(bytes, Points);
		}

		public static LegacyStatsInfo ReadRaw(byte[] bytes, ref int i) => new LegacyStatsInfo {
			Kills = LegacySerialization.ReadShort(bytes, ref i),
			Deaths = LegacySerialization.ReadShort(bytes, ref i),
			Xp = LegacySerialization.ReadUShort(bytes, ref i),
			Points = LegacySerialization.ReadUShort(bytes, ref i),
		};
	}

	/// <summary>WeaponInfo: [int collection item ids][byte collection item classes], 5 slots each.</summary>
	public sealed class LegacyWeaponInfo : ILegacyTyped {
		public List<int> ItemIds = new List<int> { 0, 0, 0, 0, 0 };
		public List<byte> Categories = new List<byte> { 0, 0, 0, 0, 0 };

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.WeaponInfo);
			LegacySerialization.WriteShort(bytes, (short)ItemIds.Count);   // FromIntCollection: [short count]
			foreach (var id in ItemIds) LegacySerialization.WriteInt(bytes, id);
			LegacySerialization.WriteInt(bytes, Categories.Count);         // FromByteCollection: [int count]
			bytes.AddRange(Categories);
		}

		public static LegacyWeaponInfo ReadRaw(byte[] bytes, ref int i) {
			var info = new LegacyWeaponInfo { ItemIds = new List<int>(), Categories = new List<byte>() };
			var n = LegacySerialization.ReadShort(bytes, ref i);
			for (var k = 0; k < n; k++) info.ItemIds.Add(LegacySerialization.ReadInt(bytes, ref i));
			var m = LegacySerialization.ReadInt(bytes, ref i);
			info.Categories.AddRange(LegacySerialization.ReadRaw(bytes, ref i, m));
			return info;
		}
	}

	/// <summary>DamageEvent: [int effect flag][float effect value][byte body part][byte n][n angles][n damages].</summary>
	public sealed class LegacyDamageEvent : ILegacyTyped {
		public int DamageEffectFlag;
		public float DamageEffectValue;
		public byte BodyPartFlag;
		public Dictionary<byte, byte> Damage = new Dictionary<byte, byte>();

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.DamageEvent);
			LegacySerialization.WriteInt(bytes, DamageEffectFlag);
			LegacySerialization.WriteFloat(bytes, DamageEffectValue);
			bytes.Add(BodyPartFlag);
			var pairs = Damage.Take(255).ToList();
			bytes.Add((byte)pairs.Count);
			foreach (var p in pairs) bytes.Add(p.Key);
			foreach (var p in pairs) bytes.Add(p.Value);
		}

		public static LegacyDamageEvent ReadRaw(byte[] bytes, ref int i) {
			var ev = new LegacyDamageEvent {
				DamageEffectFlag = LegacySerialization.ReadInt(bytes, ref i),
				DamageEffectValue = LegacySerialization.ReadFloat(bytes, ref i),
				BodyPartFlag = LegacySerialization.ReadRawByte(bytes, ref i),
			};
			var n = LegacySerialization.ReadRawByte(bytes, ref i);
			var keys = LegacySerialization.ReadRaw(bytes, ref i, n);
			var values = LegacySerialization.ReadRaw(bytes, ref i, n);
			for (var k = 0; k < n; k++) ev.Damage[keys[k]] = values[k];
			return ev;
		}
	}

	/// <summary>
	/// CharacterInfo (type 101): a full SyncObject of the player's fields (see <see cref="LegacyCharField"/>).
	/// </summary>
	public sealed class LegacyCharacterInfo : ILegacyTyped {
		public LegacySyncObject Sync;
		public LegacyCharacterInfo(LegacySyncObject sync) { Sync = sync ?? new LegacySyncObject(); }

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(LegacySerialization.Type.CharacterInfo);
			Sync.Write(bytes);
		}
	}

	/// <summary>Pre-encoded payload with its type byte (EndOfMatchData, StatsCollection ...).</summary>
	public sealed class LegacyRawValue : ILegacyTyped {
		public byte Type;
		public List<byte> Payload = new List<byte>();

		public void WriteTyped(List<byte> bytes) {
			bytes.Add(Type);
			bytes.AddRange(Payload);
		}
	}

	/// <summary>
	/// CMUNESYNC field tags of UberStrike.Realtime.UnitySdk.CharacterInfo (ActorInfo fields included).
	/// Types must match exactly: the client copies them into the fields with reflection.
	/// </summary>
	public static class LegacyCharField {
		public const int Cmid = 0x1;                     // int
		public const int PlayerName = 0x2;               // string
		public const int Channel = 0x4;                  // byte
		public const int CurrentRoom = 0x8;              // CmuneRoomID
		public const int Ping = 0x10;                    // ushort
		public const int ClanTag = 0x20;                 // string
		public const int AccessLevel = 0x40;             // byte
		public const int IsFiring = 0x80;                // bool
		public const int MoveState = 0x100;              // ushort (4.3.10 PlayerStates bits)
		public const int HorizontalRotation = 0x200;     // byte
		public const int VerticalRotation = 0x400;       // byte
		public const int QuickItems = 0x1000;            // List<int>
		public const int Stats = 0x4000;                 // StatsInfo
		public const int FunctionalItems = 0x10000;      // List<int>
		public const int Level = 0x20000;                // byte
		public const int TeamID = 0x40000;               // byte
		public const int SurfaceSound = 0x80000;         // byte
		public const int Keys = 0x100000;                // byte (KeyState)
		public const int Health = 0x200000;              // short
		public const int SkinColor = 0x400000;           // Color
		public const int Weapons = 0x800000;             // WeaponInfo
		public const int CurrentWeaponSlot = 0x1000000;  // byte
		public const int CurrentFiringMode = 0x2000000;  // byte
		public const int Armor = 0x4000000;              // ArmorInfo
		public const int Gear = 0x8000000;               // List<int>
		public const int IsReadyForGame = 0x10000000;    // bool
		public const int PlayerNumber = 0x40000000;      // byte
	}

	/// <summary>4.3.10 PlayerStates (CharacterInfo.MoveState bits).</summary>
	[Flags]
	public enum LegacyPlayerStates : ushort {
		Idle = 0,
		Grounded = 1,
		Jumping = 2,
		Flying = 8,
		Ducked = 0x10,
		Dead = 0x20,
		Paused = 0x40,
		Spectator = 0x80,
		Wading = 0x100,
		Swimming = 0x200,
		Diving = 0x400,
	}
}
