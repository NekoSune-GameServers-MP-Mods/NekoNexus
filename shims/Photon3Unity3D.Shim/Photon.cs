using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NekoNexus.Transport;

namespace ExitGames.Client.Photon {
	public enum ConnectionProtocol : byte { Udp = 0, Tcp = 1, Http = 2 }

	// Values MUST match the original Photon3Unity3D enum exactly: the game's compiled code
	// embeds these numeric constants in its switch/compare logic.
	public enum PeerStateValue : byte {
		Disconnected = 0,
		Connecting = 1,
		Connected = 3,
		Disconnecting = 4,
		InitializingApplication = 10,
	}

	public enum DebugLevel { OFF = 0, ERROR = 1, WARNING = 2, INFO = 3, ALL = 5 }

	// Exact values from the original Photon3Unity3D.dll (verified via Mono.Cecil). OK is a
	// NekoNexus-mod-only extension (0 is unused by Photon), the rest must match the originals.
	public enum StatusCode {
		OK = 0,
		SecurityExceptionOnConnect = 1022,
		ExceptionOnConnect = 1023,
		Connect = 1024,
		Disconnect = 1025,
		Exception = 1026,
		QueueOutgoingReliableWarning = 1027,
		QueueOutgoingUnreliableWarning = 1029,
		SendError = 1030,
		QueueOutgoingAcksWarning = 1031,
		QueueIncomingReliableWarning = 1033,
		QueueIncomingUnreliableWarning = 1035,
		QueueSentWarning = 1037,
		ExceptionOnReceive = 1039,
		InternalReceiveException = 1039,
		TimeoutDisconnect = 1040,
		DisconnectByServer = 1041,
		DisconnectByServerUserLimit = 1042,
		DisconnectByServerLogic = 1043,
		EncryptionEstablished = 1048,
		EncryptionFailedToEstablish = 1049,
	}

	public class EventData {
		public byte Code;
		public Dictionary<byte, object> Parameters = new Dictionary<byte, object>();

		public object this[byte key] {
			get { Parameters.TryGetValue(key, out var v); return v; }
			set { Parameters[key] = value; }
		}

		public string ToStringFull() {
			var sb = new StringBuilder();
			sb.Append("Event ").Append(Code).Append(": {");
			foreach (var kv in Parameters) {
				sb.Append(kv.Key).Append('=');
				sb.Append(kv.Value is byte[] b ? ("byte[" + b.Length + "]") : (kv.Value ?? "null").ToString());
				sb.Append(' ');
			}
			sb.Append('}');
			return sb.ToString();
		}

		public override string ToString() => "Event " + Code;
	}

	public class OperationResponse {
		public byte OperationCode;
		public short ReturnCode;
		public string DebugMessage;
		public Dictionary<byte, object> Parameters = new Dictionary<byte, object>();

		public object this[byte key] {
			get { Parameters.TryGetValue(key, out var v); return v; }
			set { Parameters[key] = value; }
		}

		public override string ToString() => "OperationResponse " + OperationCode + " (return " + ReturnCode + ")";
	}

	// The 4.3.10 Android realtime SDK retains Photon request objects for diagnostics,
	// even though the NekoNexus transport sends the compact operation fields directly.
	public class OperationRequest {
		public byte OperationCode;
		public Dictionary<byte, object> Parameters = new Dictionary<byte, object>();
	}

	// Compatibility surface used by the legacy SDK's network settings panel. Simulation
	// is intentionally disabled in the LiteNetLib transport, but the settings object must
	// exist so the old assembly can load and read/write its properties.
	public class NetworkSimulationSet {
		public bool IsSimulationEnabled { get; set; }
		public int OutgoingLag { get; set; }
		public int OutgoingJitter { get; set; }
		public int OutgoingLossPercentage { get; set; }
		public int IncomingLag { get; set; }
		public int IncomingJitter { get; set; }
		public int IncomingLossPercentage { get; set; }
		public int LostPackagesOut { get; set; }
		public int LostPackagesIn { get; set; }
	}

	public interface IPhotonPeerListener {
		void OnEvent(EventData eventData);
		void OnOperationResponse(OperationResponse operationResponse);
		void OnStatusChanged(StatusCode statusCode);
		void DebugReturn(DebugLevel level, string message);
	}

	// Compatibility helpers ExitGames exposes; UberStrike only uses CallInBackground.
	public static class SupportClass {
		// Signatures match the original exactly (void, with these two overloads).
		// Runs myThread repeatedly on a background thread until it returns false.
		public static void CallInBackground(Func<bool> myThread) => CallInBackground(myThread, 100);

		public static void CallInBackground(Func<bool> myThread, int millisecondsInterval) {
			var t = new Thread(() => {
				try { while (myThread()) Thread.Sleep(millisecondsInterval); } catch { }
			}) { IsBackground = true, Name = "PhotonBg" };
			t.Start();
		}

		public static int GetTickCount() => Environment.TickCount;
	}
}
