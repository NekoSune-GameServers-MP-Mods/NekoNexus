using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using NekoNexus.Transport;

namespace ExitGames.Client.Photon {
	// Drop-in for ExitGames.Client.Photon.PhotonPeer, backed by LiteNetLib. Implements
	// exactly the surface UberStrike.Realtime.Client.BasePeer drives.
	public class PhotonPeer : INetEventListener {
		private readonly IPhotonPeerListener _listener;
		private readonly NetManager _net;
		private NetPeer _server;
		private PeerStateValue _state = PeerStateValue.Disconnected;
		private int _serverTimeOffsetMs;        // localTick + offset ≈ server time
		private int _timeSyncSentTick;          // local TickCount when the last time-sync went out
		private bool _timeSyncPending;
		private readonly NetworkSimulationSet _networkSimulationSettings = new NetworkSimulationSet();

		// Reserved transport-only operation code for clock sync. The game never sends this (its real
		// operation codes are small enum values), so it can't collide; the request/response are
		// handled entirely inside the shims and never surface to the game.
		private const byte TimeSyncOpCode = 254;

		public ConnectionProtocol UsedProtocol { get; private set; }
		public DebugLevel DebugOut { get; set; } = DebugLevel.ERROR;
		public string ServerAddress { get; private set; }

		// Same connection key the server's RealtimeHost expects to gate connections.
		public static string ConnectionKey = "NekoNexus";

		public PhotonPeer(IPhotonPeerListener listener, ConnectionProtocol protocolType) {
			_listener = listener ?? throw new ArgumentNullException(nameof(listener));
			UsedProtocol = protocolType;
			_net = new NetManager(this) {
				AutoRecycle = true,
				UnconnectedMessagesEnabled = false,
				ChannelsCount = 4,
				UpdateTime = 10,
				// Bind IPv4-only. Many players have IPv6 disabled/broken, where creating the
				// IPv6 socket throws "An address incompatible with the requested protocol was
				// used" and kills the connect. The realtime servers are IPv4, so we never need v6.
				IPv6Enabled = IPv6Mode.Disabled,
			};
		}

		// The Android 4.3.10 realtime SDK constructs PhotonPeer with either the
		// listener-only or legacy useTcp overload. Its multiplayer path is UDP.
		public PhotonPeer(IPhotonPeerListener listener) : this(listener, ConnectionProtocol.Udp) { }
		public PhotonPeer(IPhotonPeerListener listener, bool useTcp)
			: this(listener, useTcp ? ConnectionProtocol.Tcp : ConnectionProtocol.Udp) { }

		public PeerStateValue PeerState => _state;
		public int RoundTripTime => _server?.Ping * 2 ?? 0;
		public int RoundTripTimeVariance => 0;
		public long BytesIn => _net.Statistics.BytesReceived;
		public long BytesOut => _net.Statistics.BytesSent;
		public int TimePingInterval { get; set; }
		public int SentCountAllowance { get; set; }
		public int CommandBufferSize { get; set; }
		public int QueuedIncomingCommands => 0;
		public int QueuedOutgoingCommands => 0;
		public NetworkSimulationSet NetworkSimulationSettings => _networkSimulationSettings;

		// Server clock estimate used by the game for match timing. The offset is computed from a
		// round-trip time-sync (see SendTimeSync) so this reproduces the SERVER's Environment.TickCount
		// domain - the same clock the server stamps RoundEndTime in - which is what the match countdown
		// subtracts against. Without this the value would be the client's own uptime (a different
		// epoch) and the round clock would never count down.
		public int ServerTimeInMilliSeconds => Environment.TickCount + _serverTimeOffsetMs;

		public bool Connect(string serverAddress, string applicationName) {
			ServerAddress = serverAddress;
			if (!ParseEndpoint(serverAddress, out var host, out var port)) {
				_listener.DebugReturn(DebugLevel.ERROR, "Bad server address: " + serverAddress);
				return false;
			}

			if (!_net.IsRunning && !_net.Start()) {
				_listener.DebugReturn(DebugLevel.ERROR, "Failed to start client socket.");
				return false;
			}

			_state = PeerStateValue.Connecting;

			// appId (application version) rides the connect payload -> InitRequest.ApplicationId.
			var writer = new NetDataWriter();
			writer.Put(applicationName ?? string.Empty);
			_server = _net.Connect(host, port, writer);
			return _server != null;
		}

		public void Disconnect() {
			if (_server != null) { _net.DisconnectPeer(_server); _server = null; }
			_state = PeerStateValue.Disconnected;
			_net.Stop();
		}

		public void StopThread() => _net.Stop();

		// Pumps the transport; UberStrike calls this each frame.
		public void Service() => _net.PollEvents();

		public bool SendOutgoingCommands() { _net.PollEvents(); return true; }

		public bool SendAcksOnly() { /* LiteNetLib auto-acks */ return true; }

		public bool DispatchIncomingCommands() { _net.PollEvents(); return true; }

		// Photon uses this to (re)sync the server clock; the game calls it on connect and at match
		// start. We issue a round-trip time-sync so ServerTimeInMilliSeconds tracks the server's
		// Environment.TickCount (the clock RoundEndTime is expressed in). Safe to call any time.
		public void FetchServerTimestamp() => SendTimeSync();

		// Sends a transport-only time-sync request. The server replies (TimeSyncOpCode) with its
		// current Environment.TickCount; the response handler then computes the offset.
		private void SendTimeSync() {
			if (_server == null || _server.ConnectionState != ConnectionState.Connected) return;
			try {
				_timeSyncSentTick = Environment.TickCount;
				_timeSyncPending = true;
				var wire = WireMessage.Operation(TimeSyncOpCode, new Dictionary<byte, object> { { 0, _timeSyncSentTick } });
				_server.Send(WireCodec.Encode(wire), 0, DeliveryMethod.ReliableOrdered);
			} catch (Exception ex) {
				_listener.DebugReturn(DebugLevel.WARNING, "[LNL] time-sync send failed: " + ex.Message);
			}
		}

		// The UberStrike RemoteProcedureCall delegate target.
		public bool OpCustom(byte customOpCode, Dictionary<byte, object> customOpParameters, bool sendReliable, byte channelId, bool encrypt) {
			if (_server == null || _server.ConnectionState != ConnectionState.Connected) {
				_listener.DebugReturn(DebugLevel.WARNING, $"[LNL] op {customOpCode} dropped: not connected (state {_server?.ConnectionState})");
				return false;
			}
			var wire = WireMessage.Operation(customOpCode, customOpParameters);
			byte[] bytes = WireCodec.Encode(wire);
			try {
				_server.Send(bytes, sendReliable ? (byte)0 : (byte)1, sendReliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
				_listener.DebugReturn(DebugLevel.INFO, $"[LNL] -> op {customOpCode} ({bytes.Length}b, {(sendReliable ? "rel" : "unrel")})");
				return true;
			} catch (Exception ex) {
				_listener.DebugReturn(DebugLevel.ERROR, "[LNL] send failed: " + ex.Message);
				_listener.OnStatusChanged(StatusCode.SendError);
				return false;
			}
		}

		public bool OpCustom(byte customOpCode, Dictionary<byte, object> customOpParameters, bool sendReliable)
			=> OpCustom(customOpCode, customOpParameters, sendReliable, 0, false);

		public bool OpCustom(byte customOpCode, Dictionary<byte, object> customOpParameters, bool sendReliable, byte channelId)
			=> OpCustom(customOpCode, customOpParameters, sendReliable, channelId, false);

		// ---- INetEventListener ----------------------------------------------------

		public void OnPeerConnected(NetPeer peer) {
			_server = peer;
			_state = PeerStateValue.Connected;
			_listener.DebugReturn(DebugLevel.INFO, "[LNL] connected to " + ServerAddress);
			// Sync the server clock immediately so the match countdown is correct from the first match.
			SendTimeSync();
			_listener.OnStatusChanged(StatusCode.Connect);
		}

		public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
			_state = PeerStateValue.Disconnected;
			_server = null;
			_listener.OnStatusChanged(MapStatus(disconnectInfo.Reason));
		}

		public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod) {
			byte[] data = reader.GetRemainingBytes();
			WireMessage msg;
			try { msg = WireCodec.Decode(data); }
			catch (Exception ex) { _listener.DebugReturn(DebugLevel.ERROR, "bad wire message: " + ex.Message); return; }

			_listener.DebugReturn(DebugLevel.INFO, $"[LNL] <- {msg.Type} code {msg.Code} ({data.Length}b)");

			switch (msg.Type) {
				case WireMessageType.Event:
					_listener.OnEvent(new EventData { Code = msg.Code, Parameters = msg.Parameters });
					break;
				case WireMessageType.OperationResponse:
					// Transport-only clock sync: compute the server-time offset and swallow the reply
					// so the game never sees this internal operation.
					if (msg.Code == TimeSyncOpCode) {
						if (_timeSyncPending && msg.Parameters.TryGetValue(0, out var st) && st is int serverTick) {
							int now = Environment.TickCount;
							// Estimate the server's tick "now" as serverTick + half the round-trip, so the
							// offset maps our local clock onto the server's: offset = serverTick - (T0+T1)/2.
							int midpoint = _timeSyncSentTick + ((now - _timeSyncSentTick) / 2);
							_serverTimeOffsetMs = serverTick - midpoint;
							_timeSyncPending = false;
							_listener.DebugReturn(DebugLevel.INFO, $"[LNL] server clock synced (offset {_serverTimeOffsetMs}ms).");
						}
						break;
					}
					_listener.OnOperationResponse(new OperationResponse {
						OperationCode = msg.Code,
						ReturnCode = msg.ReturnCode,
						DebugMessage = msg.DebugMessage,
						Parameters = msg.Parameters,
					});
					break;
			}
		}

		public void OnConnectionRequest(ConnectionRequest request) => request.Reject();   // client never accepts
		public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) {
			_listener.OnStatusChanged(StatusCode.Exception);
		}
		public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
		public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

		// ---- helpers --------------------------------------------------------------

		private static StatusCode MapStatus(DisconnectReason reason) {
			switch (reason) {
				case DisconnectReason.Timeout:
				case DisconnectReason.ConnectionFailed:
					return StatusCode.TimeoutDisconnect;
				case DisconnectReason.ConnectionRejected:
					return StatusCode.DisconnectByServerLogic;
				case DisconnectReason.RemoteConnectionClose:
					return StatusCode.DisconnectByServer;
				default:
					return StatusCode.Disconnect;
			}
		}

		private static bool ParseEndpoint(string address, out string host, out int port) {
			host = null; port = 0;
			if (string.IsNullOrEmpty(address)) return false;
			// Accept "host:port", "host", or "udp://host:port".
			address = address.Replace("udp://", string.Empty).Trim();
			int colon = address.LastIndexOf(':');
			if (colon < 0) { host = address; port = 5055; return true; }
			host = address.Substring(0, colon);
			return int.TryParse(address.Substring(colon + 1), out port);
		}
	}
}
