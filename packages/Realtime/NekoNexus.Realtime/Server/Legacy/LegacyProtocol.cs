using System;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// Identifies UberStrike 4.3.10 (Android/iOS) realtime clients.
	/// The 4.3.10 SDK connects with <c>PhotonPeer.Connect(server, cmid.ToString())</c>, so its
	/// application name (InitRequest.ApplicationId) is the player's numeric CMID, while the 4.7.1
	/// PC client sends its version string. A purely numeric id therefore means "legacy client".
	/// </summary>
	public static class LegacyClient {
		public static bool IsLegacy(string applicationId) {
			if (string.IsNullOrEmpty(applicationId)) return false;
			foreach (var c in applicationId) {
				if (c < '0' || c > '9') return false;
			}
			return true;
		}
	}

	/// <summary>Photon operation codes used by the 4.3.10 SDK (CmuneOperationCodes).</summary>
	public static class LegacyOperationCode {
		public const byte MessageToApplication = 0x42;
		public const byte MessageToPlayer = 0x50;
		public const byte MessageToAll = 0x51;
		public const byte MessageToServer = 0x52;
		public const byte MessageToOthers = 0x53;
		public const byte PhotonGameJoin = 0x58;
		public const byte PhotonGameLeave = 0x59;
		public const byte GameListUpdate = 0x5A;
		public const byte GameListRemoval = 0x5B;
		public const byte RegisterGameServer = 0x5C;
	}

	/// <summary>
	/// Event codes the 4.3.10 PhotonPeerListener.OnEvent understands. Code 0 is a remote call:
	/// {101: network id (short), 100: method id (byte), 103: args}. 3/4/5 are lobby room-list
	/// update / refresh / removal with a Hashtable in key 42. Anything else is logged as unhandled.
	/// </summary>
	public static class LegacyEventCode {
		public const byte NetworkMessage = 0;
		public const byte RoomListUpdate = 3;
		public const byte RoomListRefresh = 4;
		public const byte RoomListRemoval = 5;
	}

	/// <summary>Photon parameter keys used by the 4.3.10 SDK (ParameterKeys).</summary>
	public static class LegacyParameterKey {
		/// <summary>PhotonGameJoin reply: CmuneRoomID bytes of the joined room.</summary>
		public const byte GameId = 4;
		public const byte ActorNr = 9;
		public const byte Actors = 11;
		/// <summary>Serialized reply objects (RealtimeSerialization) of a MessageToApplication response.</summary>
		public const byte Data = 42;
		/// <summary>short: matches a MessageToApplication response to the request that asked for it.</summary>
		public const byte InvocationId = 61;
		public const byte MethodId = 100;
		public const byte InstanceId = 101;
		public const byte ActorId = 102;
		/// <summary>Serialized call arguments (RealtimeSerialization).</summary>
		public const byte Bytes = 103;
		public const byte RoomId = 120;
		public const byte LobbyRoomUpdate = 122;
		public const byte LobbyRoomDelete = 123;
		/// <summary>PhotonGameJoin reply, bool: true = BuildLevel, false = ClearLevel.</summary>
		public const byte InitRoom = 200;
		public const byte ServerTicks = 201;
		public const byte AccessLevel = 205;
		public const byte Cmid = 206;
	}

	/// <summary>
	/// Server application methods called through MessageToApplication (0x42).
	/// Ids recovered from the 4.3.10 client: it calls 1 right after connecting
	/// (PhotonPeerListener.OnStatusChanged) and 2 to poll server load (ServerLoadRequest).
	/// </summary>
	public static class LegacyApplicationMethod {
		/// <summary>Args: (byte peerType, string clientVersion). Sent on every connect; no reply expected.</summary>
		public const byte Handshake = 1;
		/// <summary>Args: none. Reply: [ServerLoadData].</summary>
		public const byte ServerLoad = 2;
	}

	/// <summary>PeerType sent as the first handshake argument.</summary>
	public enum LegacyPeerType : byte {
		None = 0,
		GamePeer = 1,
		CommServer = 2,
		GamerServer = 3,
		LobbyServer = 4,
	}

	/// <summary>The 4.3.10 UberStrike.Realtime.UnitySdk.ServerLoadData struct (CmuneDataType 0x2F).</summary>
	public struct LegacyServerLoadData {
		public int PlayersConnected;
		public int PeersConnected;
		public int RoomsCreated;
		public float MaxPlayerCount;

		public override string ToString() =>
			$"players={PlayersConnected} peers={PeersConnected} rooms={RoomsCreated} max={MaxPlayerCount}";
	}

	public class LegacyProtocolException : Exception {
		public LegacyProtocolException(string message) : base(message) { }
	}
}
