using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using NekoNexus.Transport;
using PhotonHostRuntimeInterfaces;
// Disambiguate from LiteNetLib.DisconnectReason: in this namespace, "DisconnectReason"
// always means Photon's (the one NekoNexus.Realtime overrides against).
using DisconnectReason = PhotonHostRuntimeInterfaces.DisconnectReason;

namespace Photon.SocketServer {
	// ---- Result / parameter types -------------------------------------------------

	public enum SendResult {
		Ok = 0,
		Disconnected = 1,
		SendBufferFull = 2,
		Failed = 3,
		InvalidContentLength = 4,
		MessageTooBig = 5,
		InvalidChannel = 6,
	}

	// Photon's SendParameters is a struct with several flags; NekoNexus only sets Unreliable
	// (and occasionally ChannelId). The rest are accepted and ignored.
	public struct SendParameters {
		public bool Unreliable;
		public byte ChannelId;
		public bool Encrypted;
		public bool Flush;
		public bool Unbuffered;
	}

	// ---- Operation / event envelopes ---------------------------------------------

	public class OperationRequest {
		public byte OperationCode;
		public Dictionary<byte, object> Parameters;

		public OperationRequest() { Parameters = new Dictionary<byte, object>(); }
		public OperationRequest(byte operationCode) { OperationCode = operationCode; Parameters = new Dictionary<byte, object>(); }
		public OperationRequest(byte operationCode, Dictionary<byte, object> parameters) { OperationCode = operationCode; Parameters = parameters ?? new Dictionary<byte, object>(); }
	}

	public class OperationResponse {
		public byte OperationCode;
		public short ReturnCode;
		public string DebugMessage;
		public Dictionary<byte, object> Parameters;

		public OperationResponse() { Parameters = new Dictionary<byte, object>(); }
		public OperationResponse(byte operationCode) { OperationCode = operationCode; Parameters = new Dictionary<byte, object>(); }
		public OperationResponse(byte operationCode, Dictionary<byte, object> parameters) { OperationCode = operationCode; Parameters = parameters ?? new Dictionary<byte, object>(); }
	}

	public class EventData {
		public byte Code;
		public Dictionary<byte, object> Parameters;

		public EventData() { Parameters = new Dictionary<byte, object>(); }
		public EventData(byte code) { Code = code; Parameters = new Dictionary<byte, object>(); }
		public EventData(byte code, Dictionary<byte, object> parameters) { Code = code; Parameters = parameters ?? new Dictionary<byte, object>(); }
	}

	// ---- Connection setup --------------------------------------------------------

	public class InitRequest {
		public string ApplicationId { get; internal set; }
		public object UserData { get; set; }
		public string RemoteIP { get; internal set; }
		public int RemotePort { get; internal set; }
		public IPAddress RemoteIPAddress { get; internal set; }
		public IPAddress LocalIPAddress { get; internal set; }
		public int LocalPort { get; internal set; }

		internal InitRequest() { }

		// Public factory for peers that never go through the real host (e.g. server-side AI
		// bots): there's no live connection to describe, just the ApplicationId check and
		// UserData that BasePeer's constructor needs.
		public InitRequest(string applicationId, object userData) {
			ApplicationId = applicationId;
			UserData = userData;
		}
	}

	// ---- Peers -------------------------------------------------------------------

	// Base type used as the return value of ApplicationBase.CreatePeer.
	public abstract class PeerBase : System.IDisposable {
		internal NetPeer Transport;          // assigned by the host right after creation
		internal RealtimeHost Host;          // owning host (for send/disconnect)
		internal bool DisconnectRequested;   // set if Disconnect() is called before Transport is ready

		public string RemoteIP { get; internal set; }
		public int RemotePort { get; internal set; }
		public IPAddress RemoteIPAddress { get; internal set; }
		public IPAddress LocalIPAddress { get; internal set; }
		public int LocalPort { get; internal set; }

		// Live round-trip time in milliseconds (Photon exposes this on the peer).
		public int RoundTripTime => Transport != null ? Transport.Ping * 2 : 0;
		public int RoundTripTimeVariance => 0;

		// Photon's PeerBase is IDisposable; NekoNexus disposes peers to drop the connection.
		public virtual void Dispose() {
			DisconnectRequested = true;
			Host?.Disconnect(this);
		}

		internal abstract void DispatchOperationRequest(OperationRequest request, SendParameters sendParameters);
		internal abstract void DispatchDisconnect(DisconnectReason reasonCode, string reasonDetail);
	}

	public abstract class ClientPeer : PeerBase {
		protected ClientPeer(InitRequest initRequest) {
			if (initRequest != null) {
				RemoteIP = initRequest.RemoteIP;
				RemotePort = initRequest.RemotePort;
				RemoteIPAddress = initRequest.RemoteIPAddress;
				LocalIPAddress = initRequest.LocalIPAddress;
				LocalPort = initRequest.LocalPort;
			}
		}

		// Overridden by NekoNexus's BasePeer.
		protected virtual void OnOperationRequest(OperationRequest operationRequest, SendParameters sendParameters) { }
		protected virtual void OnDisconnect(DisconnectReason reasonCode, string reasonDetail) { }

		// Virtual so a server-side stand-in peer (Android players in PC rooms) can capture its events.
		public virtual SendResult SendEvent(EventData eventData, SendParameters sendParameters) {
			if (Transport == null || Host == null) return SendResult.Disconnected;
			var wire = WireMessage.Event(eventData.Code, eventData.Parameters);
			return Host.Send(this, WireCodec.Encode(wire), sendParameters.Unreliable, sendParameters.ChannelId);
		}

		public SendResult SendOperationResponse(OperationResponse operationResponse, SendParameters sendParameters) {
			if (Transport == null || Host == null) return SendResult.Disconnected;
			var wire = WireMessage.Response(operationResponse.OperationCode, operationResponse.ReturnCode, operationResponse.DebugMessage, operationResponse.Parameters);
			return Host.Send(this, WireCodec.Encode(wire), sendParameters.Unreliable, sendParameters.ChannelId);
		}

		public void Disconnect() {
			DisconnectRequested = true;
			Host?.Disconnect(this);
		}

		internal sealed override void DispatchOperationRequest(OperationRequest request, SendParameters sendParameters) => OnOperationRequest(request, sendParameters);
		internal sealed override void DispatchDisconnect(DisconnectReason reasonCode, string reasonDetail) => OnDisconnect(reasonCode, reasonDetail);
	}

	// ---- Application -------------------------------------------------------------

	// Replaces Photon's ApplicationBase. The host process calls RunHost() to bind the
	// UDP listener and pump the LiteNetLib event loop; Setup()/CreatePeer()/TearDown()
	// keep the exact protected-override shape NekoNexus's BaseRealtimeApplication expects.
	public abstract class ApplicationBase {
		public static ApplicationBase Instance { get; private set; }

		public string ApplicationPath { get; private set; }
		public string BinaryPath { get; private set; }

		private RealtimeHost _host;

		protected internal abstract void Setup();
		protected internal abstract void TearDown();
		protected internal abstract PeerBase CreatePeer(InitRequest initRequest);

		// Entry point used by the launcher (NekoNexus.Realtime.Host).
		public void RunHost(RealtimeHostOptions options) {
			Instance = this;
			BinaryPath = string.IsNullOrEmpty(options.BinaryPath) ? System.AppContext.BaseDirectory : options.BinaryPath;
			ApplicationPath = string.IsNullOrEmpty(options.ApplicationPath) ? BinaryPath : options.ApplicationPath;

			Setup();

			_host = new RealtimeHost(this, options);
			_host.RunBlocking();   // blocks pumping events until Stop() is called
		}

		public void StopHost() {
			_host?.Stop();
			TearDown();
		}

		// Lets the host invoke the protected factory.
		internal PeerBase CreatePeerInternal(InitRequest initRequest) => CreatePeer(initRequest);
	}
}
