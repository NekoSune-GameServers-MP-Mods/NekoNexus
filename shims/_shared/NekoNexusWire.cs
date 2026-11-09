using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NekoNexus.Transport {
	// Message kinds that cross the wire. Mirrors the Photon concepts the game uses.
	public enum WireMessageType : byte {
		// client -> server
		Operation = 1,
		// server -> client
		Event = 2,
		// server -> client (reply to an Operation)
		OperationResponse = 3,
	}

	// Value type tags for the Dictionary<byte, object> payload. UberStrike 4.7.1 only ever
	// puts byte[] across the wire (its own serialized message blobs), plus string/int
	// for operation-response error reporting. The 4.3.10 (Android) client also receives a
	// System.Collections.Hashtable in its room-list events (key 42), hence the Hashtable tag.
	// Old peers that do not know a tag fail to decode that one message only.
	internal enum WireTag : byte {
		Null = 0, ByteArray = 1, String = 2, Int = 3, Short = 4, Byte = 5, Bool = 6, Long = 7, Float = 8, Double = 9,
		Hashtable = 10,
	}

	// A decoded envelope. This is the single, transport-agnostic representation shared by
	// the server (Photon.SocketServer shim) and client (Photon3Unity3D shim) shims.
	public sealed class WireMessage {
		public WireMessageType Type;
		public byte Code;
		public short ReturnCode;              // OperationResponse only
		public string DebugMessage;           // OperationResponse only
		public Dictionary<byte, object> Parameters = new Dictionary<byte, object>();

		public static WireMessage Operation(byte code, Dictionary<byte, object> parameters)
			=> new WireMessage { Type = WireMessageType.Operation, Code = code, Parameters = parameters ?? new Dictionary<byte, object>() };

		public static WireMessage Event(byte code, Dictionary<byte, object> parameters)
			=> new WireMessage { Type = WireMessageType.Event, Code = code, Parameters = parameters ?? new Dictionary<byte, object>() };

		public static WireMessage Response(byte code, short returnCode, string debugMessage, Dictionary<byte, object> parameters)
			=> new WireMessage { Type = WireMessageType.OperationResponse, Code = code, ReturnCode = returnCode, DebugMessage = debugMessage, Parameters = parameters ?? new Dictionary<byte, object>() };
	}

	// Deterministic, allocation-light binary codec for WireMessage. Identical code runs on
	// both ends (compiled into both shims from this one source file), so the two are always
	// in lock-step regardless of platform/runtime. net35-clean: no Span, no nullable refs.
	public static class WireCodec {
		public static byte[] Encode(WireMessage msg) {
			using (var ms = new MemoryStream(64))
			using (var w = new BinaryWriter(ms)) {
				w.Write((byte)msg.Type);
				w.Write(msg.Code);

				if (msg.Type == WireMessageType.OperationResponse) {
					w.Write(msg.ReturnCode);
					WriteString(w, msg.DebugMessage);
				}

				var p = msg.Parameters;
				if (p == null || p.Count > 255) {
					if (p != null && p.Count > 255) throw new InvalidOperationException("Too many parameters (max 255).");
					w.Write((byte)0);
				} else {
					w.Write((byte)p.Count);
					foreach (var kv in p) {
						w.Write(kv.Key);
						WriteValue(w, kv.Value);
					}
				}

				w.Flush();
				return ms.ToArray();
			}
		}

		public static WireMessage Decode(byte[] data, int offset, int length) {
			using (var ms = new MemoryStream(data, offset, length, false))
			using (var r = new BinaryReader(ms)) {
				var msg = new WireMessage {
					Type = (WireMessageType)r.ReadByte(),
					Code = r.ReadByte(),
				};

				if (msg.Type == WireMessageType.OperationResponse) {
					msg.ReturnCode = r.ReadInt16();
					msg.DebugMessage = ReadString(r);
				}

				int count = r.ReadByte();
				for (int i = 0; i < count; i++) {
					byte key = r.ReadByte();
					msg.Parameters[key] = ReadValue(r);
				}

				return msg;
			}
		}

		public static WireMessage Decode(byte[] data) => Decode(data, 0, data.Length);

		private static void WriteValue(BinaryWriter w, object value) {
			switch (value) {
				case null: w.Write((byte)WireTag.Null); break;
				case byte[] b: w.Write((byte)WireTag.ByteArray); w.Write(b.Length); w.Write(b); break;
				case string s: w.Write((byte)WireTag.String); WriteString(w, s); break;
				case int i: w.Write((byte)WireTag.Int); w.Write(i); break;
				case short sh: w.Write((byte)WireTag.Short); w.Write(sh); break;
				case byte by: w.Write((byte)WireTag.Byte); w.Write(by); break;
				case bool bo: w.Write((byte)WireTag.Bool); w.Write(bo); break;
				case long l: w.Write((byte)WireTag.Long); w.Write(l); break;
				case float f: w.Write((byte)WireTag.Float); w.Write(f); break;
				case double d: w.Write((byte)WireTag.Double); w.Write(d); break;
				case System.Collections.Hashtable h:
					w.Write((byte)WireTag.Hashtable);
					w.Write(h.Count);
					foreach (System.Collections.DictionaryEntry e in h) {
						WriteValue(w, e.Key);
						WriteValue(w, e.Value);
					}
					break;
				default: throw new NotSupportedException("Unsupported wire value type: " + value.GetType().FullName);
			}
		}

		private static object ReadValue(BinaryReader r) {
			var tag = (WireTag)r.ReadByte();
			switch (tag) {
				case WireTag.Null: return null;
				case WireTag.ByteArray: { int n = r.ReadInt32(); return r.ReadBytes(n); }
				case WireTag.String: return ReadString(r);
				case WireTag.Int: return r.ReadInt32();
				case WireTag.Short: return r.ReadInt16();
				case WireTag.Byte: return r.ReadByte();
				case WireTag.Bool: return r.ReadBoolean();
				case WireTag.Long: return r.ReadInt64();
				case WireTag.Float: return r.ReadSingle();
				case WireTag.Double: return r.ReadDouble();
				case WireTag.Hashtable: {
					int n = r.ReadInt32();
					if (n < 0 || n > 65536) throw new NotSupportedException("Bad hashtable size: " + n);
					var h = new System.Collections.Hashtable(n);
					for (int k = 0; k < n; k++) {
						var key = ReadValue(r);
						var value = ReadValue(r);
						if (key != null) h[key] = value;
					}
					return h;
				}
				default: throw new NotSupportedException("Unknown wire tag: " + tag);
			}
		}

		private static void WriteString(BinaryWriter w, string s) {
			if (s == null) { w.Write(-1); return; }
			var bytes = Encoding.UTF8.GetBytes(s);
			w.Write(bytes.Length);
			w.Write(bytes);
		}

		private static string ReadString(BinaryReader r) {
			int n = r.ReadInt32();
			if (n < 0) return null;
			return Encoding.UTF8.GetString(r.ReadBytes(n));
		}
	}

	// Connection-time handshake payload (the app id / version the client sends on connect,
	// equivalent to PhotonPeer.Connect(addr, appId) -> InitRequest.ApplicationId on the server).
	public static class WireConnect {
		public static byte[] EncodeAppId(string appId) => Encoding.UTF8.GetBytes(appId ?? string.Empty);
		public static string DecodeAppId(byte[] data) => data == null ? string.Empty : Encoding.UTF8.GetString(data);
	}
}
