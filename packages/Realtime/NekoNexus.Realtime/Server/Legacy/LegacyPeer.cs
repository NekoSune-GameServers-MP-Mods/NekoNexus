using log4net;
using Photon.SocketServer;
using PhotonHostRuntimeInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// Connection from an UberStrike 4.3.10 (Android) client. These clients speak Cmune's older
	/// RPC protocol instead of the 4.7.1 operation-handler protocol <see cref="BasePeer"/>
	/// implements, so they get their own peer type. The servers, rooms and counters behind it are
	/// the same ones PC players use, which is what makes the two clients share servers.
	///
	/// MessageToApplication (0x42) requests carry {100: methodId (byte), 61: invocationId (short),
	/// 103: args (RealtimeSerialization)} and are answered with an OperationResponse 0x42 holding
	/// {61: invocationId, 42: reply objects}. Other legacy opcodes are logged until implemented.
	/// </summary>
	public abstract class LegacyPeer : ClientPeer {
		protected static readonly ILog Log = LogManager.GetLogger("LegacyPeer");

		private readonly HashSet<string> unseenLegacyMessages = new HashSet<string>();

		public int Cmid { get; }
		public string ClientVersion { get; private set; } = "4.3.10";
		public LegacyPeerType DeclaredPeerType { get; private set; }

		protected LegacyPeer(InitRequest initRequest) : base(initRequest) {
			int.TryParse(initRequest.ApplicationId, out var cmid);
			Cmid = cmid;
			Log.Info($"[{Name}] 4.3.10 client connected: cmid {Cmid} from {RemoteIP}:{RemotePort}");
		}

		protected abstract string Name { get; }

		/// <summary>Handle an application method. Return false if the method is not implemented.</summary>
		protected abstract bool OnApplicationMethod(byte methodId, object[] args, out object[] reply);

		/// <summary>Opcodes other than MessageToApplication. Return false if not implemented.</summary>
		protected virtual bool OnLegacyOperation(OperationRequest request) => false;

		protected override void OnOperationRequest(OperationRequest request, SendParameters sendParameters) {
			try {
				if (request.OperationCode == LegacyOperationCode.MessageToApplication) {
					HandleApplicationMessage(request);
				} else if (!OnLegacyOperation(request)) {
					WarnUnsupportedOnce($"opcode:0x{request.OperationCode:X2}", $"legacy opcode 0x{request.OperationCode:X2} not supported yet (params: {DescribeKeys(request)})");
				}
			} catch (Exception ex) {
				Log.Error($"[{Name}] cmid {Cmid}: error handling legacy opcode 0x{request.OperationCode:X2}", ex);
			}
		}

		private void HandleApplicationMessage(OperationRequest request) {
			if (!request.Parameters.TryGetValue(LegacyParameterKey.MethodId, out var methodObj) || !(methodObj is byte methodId)) {
				Log.Warn($"[{Name}] cmid {Cmid}: MessageToApplication without a method id ({DescribeKeys(request)})");
				return;
			}

			short? invocationId = null;
			if (request.Parameters.TryGetValue(LegacyParameterKey.InvocationId, out var inv) && inv is short s) invocationId = s;

			object[] args;
			try {
				request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var raw);
				args = LegacySerialization.ToObjects(raw as byte[]);
			} catch (LegacyProtocolException ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: could not decode args of method {methodId}: {ex.Message}");
				return;
			}

			if (!OnApplicationMethod(methodId, args, out var reply)) {
				WarnUnsupportedOnce($"method:{methodId}", $"application method {methodId} not supported yet (args: {DescribeArgs(args)})");
				return;
			}

			// Reply only when the client is waiting for one (it always sends an invocation id).
			if (invocationId.HasValue) SendApplicationReply(invocationId.Value, reply);
		}

		protected void SendApplicationReply(short invocationId, object[] reply, short returnCode = 0) {
			var response = new OperationResponse(LegacyOperationCode.MessageToApplication) { ReturnCode = returnCode };
			response.Parameters[LegacyParameterKey.InvocationId] = invocationId;
			response.Parameters[LegacyParameterKey.Data] = LegacySerialization.ToBytes(reply ?? new object[0]);
			SendOperationResponse(response, new SendParameters());
		}

		/// <summary>Method 1, sent by every 4.3.10 client right after connecting.</summary>
		protected void HandleHandshake(object[] args) {
			if (args.Length > 0 && args[0] is byte peerType) DeclaredPeerType = (LegacyPeerType)peerType;
			if (args.Length > 1 && args[1] is string version && version.Length > 0) ClientVersion = version;
			Log.Info($"[{Name}] cmid {Cmid}: handshake as {DeclaredPeerType}, client {ClientVersion}");
		}

		protected override void OnDisconnect(DisconnectReason reasonCode, string reasonDetail) {
			Log.Info($"[{Name}] 4.3.10 client cmid {Cmid} disconnected ({reasonCode})");
		}

		// A single warning per unknown legacy method/opcode per connection makes
		// Android's missing lobby/chat/gameplay mappings visible in release logs
		// without flooding the server's log on every frame or leaking chat text.
		private void WarnUnsupportedOnce(string key, string message) {
			if (unseenLegacyMessages.Add(key)) {
				Log.Warn($"[{Name}] cmid {Cmid}: {message}");
			}
		}

		private static string DescribeKeys(OperationRequest request) =>
			string.Join(", ", request.Parameters.Select(p => $"{p.Key}:{p.Value?.GetType().Name ?? "null"}"));

		private static string DescribeArgs(object[] args) =>
			string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"));
	}
}
