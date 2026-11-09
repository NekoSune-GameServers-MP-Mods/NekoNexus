using log4net;
using System;
using System.Net.Sockets;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UberStrike.Core.Serialization;
using WebSocketSharp;

namespace NekoNexus {
	public partial class WebSocket {
		public class SocketClient {
			readonly byte[] MAGIC_BYTES = new byte[] { 0x65, 0x53, 0x69, 0x44, 0x61, 0x52, 0x61, 0x50 /* 0x50 */ };

			public const int CONNECT_TIMEOUT = 3;
			public const int RECONNECT_INTERVAL = 1;
			public const int MAX_RECONNECT = 10;
			public const int SEND_TIMEOUT = 3;
			public const int RECEIVE_TIMEOUT = 3;

			protected static readonly ILog Log = LogManager.GetLogger(nameof(SocketClient));

			private IPEndPoint RemoteEndPoint;
			private WebSocketSharp.WebSocket SocketConnection;
			private readonly ManualResetEvent ConnectionWaitHandle = new ManualResetEvent(false);
			private readonly ManualResetEvent ConnectionRejectedHandle = new ManualResetEvent(false);
			public DateTime LastResponseTime;

			private Task<int> sendTask;
			private Task<object> receiveTask;
			private readonly Dictionary<Guid, TaskCompletionSource<object>> receiveTasks = new Dictionary<Guid, TaskCompletionSource<object>>();

			private SocketInfo ClientInfo;
			private readonly RijndaelManaged CryptoProvider;

			public event EventHandler<SocketConnectedEventArgs> Connected;
			public event EventHandler<SocketDisconnectedEventArgs> Disconnected;
			public event EventHandler<SocketDataReceivedEventArgs> DataReceived;
			public event EventHandler<SocketConnectionRejectedEventArgs> ConnectionRejected;

			// The master pings every 10 s. A host that crashed or dropped off the network never closes the
			// TCP connection, so the socket would look open forever; after this much silence it is
			// treated as lost and the reconnect loop takes over.
			private const int SILENCE_TIMEOUT_SECONDS = 45;
			private Timer watchdog;

			public SocketClient(Guid serverId, ServerType serverType, int photonId, string encryptionPassphrase) {
				watchdog = new Timer(_ => CheckSilence(), null, 15000, 15000);
				try {
					ClientInfo = new SocketInfo {
						SocketId = serverId,
						PhotonId = photonId,
						Type = serverType
					};

					if (!string.IsNullOrEmpty(encryptionPassphrase)) {
						CryptoProvider = new RijndaelManaged {
							KeySize = 256,
							Key = new Rfc2898DeriveBytes(encryptionPassphrase, ClientInfo.SocketId.ToByteArray()).GetBytes(32),
							IV = ClientInfo.SocketId.ToByteArray()
						};
					}
				} catch (Exception e) {
					Log.Error(e);
				}
			}

			// ---- connection to the master (web service) ------------------------------------------
			// The connection is retried for as long as it takes: if the web service is down for an
			// hour, the realtime server keeps trying (backing off to every MAX_BACKOFF_SECONDS) and
			// re-registers as soon as it is back - no manual restart. The hostname is resolved again
			// on every attempt, so a DNS / IP change is picked up too.
			public const int MAX_BACKOFF_SECONDS = 30;

			private string remoteHost;
			private int remotePort;
			private int connectLoopRunning;
			private volatile bool shuttingDown;
			private DateTime lastRejectedUtc = DateTime.MinValue;

			public bool IsConnected => SocketConnection != null && SocketConnection.ReadyState == WebSocketState.Open;

			public void Connect(IPAddress ip, int port, int maxAttempts = 0) => Connect(ip.ToString(), port, maxAttempts);

			/// <summary>Connect to the master; maxAttempts 0 = retry until connected.</summary>
			public void Connect(string host, int port, int maxAttempts = 0) {
				remoteHost = host;
				remotePort = port;
				StartConnectLoop(maxAttempts);
			}

			public void Reconnect(int maxAttempts = 0) {
				if (remoteHost == null || shuttingDown) return;
				StartConnectLoop(maxAttempts);
			}

			public void Shutdown() {
				shuttingDown = true;
				CloseQuietly(SocketConnection);
			}

			private void StartConnectLoop(int maxAttempts) {
				if (Interlocked.CompareExchange(ref connectLoopRunning, 1, 0) != 0) return; // a loop is already trying
				Task.Run(async () => {
					try {
						await ConnectLoop(maxAttempts);
					} catch (Exception e) {
						Log.Error("Master connection loop failed", e);
					} finally {
						Interlocked.Exchange(ref connectLoopRunning, 0);
					}
				});
			}

			private async Task ConnectLoop(int maxAttempts) {
				// Just rejected by the master (e.g. a stale session still registered): don't hammer it.
				if ((DateTime.UtcNow - lastRejectedUtc).TotalSeconds < MAX_BACKOFF_SECONDS) {
					await Task.Delay(TimeSpan.FromSeconds(MAX_BACKOFF_SECONDS));
				}

				var attempt = 0;
				var delay = RECONNECT_INTERVAL;
				while (!shuttingDown && (maxAttempts == 0 || attempt < maxAttempts)) {
					attempt++;
					try {
						var address = ResolveIPv4(remoteHost);
						RemoteEndPoint = new IPEndPoint(address, remotePort);
						var ws = CreateSocket(RemoteEndPoint);

						ConnectionWaitHandle.Reset();
						ConnectionRejectedHandle.Reset();
						ws.ConnectAsync();

						if (ConnectionWaitHandle.WaitOne(TimeSpan.FromSeconds(CONNECT_TIMEOUT))) {
							if (attempt > 1) Log.Info($"Reconnected to master {remoteHost}:{remotePort} after {attempt} attempts.");
							return;
						}
						CloseQuietly(ws);
					} catch (Exception e) {
						if (attempt == 1) Log.Warn($"Master {remoteHost}:{remotePort}: {e.Message}");
					}

					if (attempt == 1 || attempt % 10 == 0) {
						Log.Warn($"Master {remoteHost}:{remotePort} not reachable (attempt {attempt}) - retrying every {Math.Min(MAX_BACKOFF_SECONDS, delay)}s until it is back.");
					}
					await Task.Delay(TimeSpan.FromSeconds(delay));
					delay = Math.Min(MAX_BACKOFF_SECONDS, delay * 2);
				}
			}

			private void CheckSilence() {
				try {
					if (shuttingDown || !IsConnected) return;
					if ((DateTime.UtcNow - LastResponseTime).TotalSeconds < SILENCE_TIMEOUT_SECONDS) return;
					Log.Warn($"No ping from master {remoteHost}:{remotePort} for {SILENCE_TIMEOUT_SECONDS}s - reconnecting.");
					var dead = SocketConnection;
					SocketConnection = null;   // its late OnClose is then ignored as a replaced socket
					CloseQuietly(dead);
					Disconnected?.Invoke(this, new SocketDisconnectedEventArgs { Socket = dead, Info = ClientInfo, Reason = "ping timeout" });
				} catch (Exception e) {
					Log.Error("Master watchdog failed", e);
				}
			}

			private static IPAddress ResolveIPv4(string host) {
				if (IPAddress.TryParse(host, out var ip)) return ip;
				var address = Dns.GetHostAddresses(host).FirstOrDefault(_ => _.AddressFamily == AddressFamily.InterNetwork);
				return address ?? throw new SocketException((int)SocketError.HostNotFound);
			}

			private static void CloseQuietly(WebSocketSharp.WebSocket ws) {
				if (ws == null) return;
				try {
					if (ws.ReadyState == WebSocketState.Open || ws.ReadyState == WebSocketState.Connecting) ws.CloseAsync();
				} catch { }
			}

			/// <summary>A fresh socket per attempt: a closed WebSocketSharp client can't be reused.</summary>
			private WebSocketSharp.WebSocket CreateSocket(IPEndPoint endPoint) {
				var uriBuilder = new UriBuilder {
					Scheme = "ws",
					Host = endPoint.Address.ToString(),
					Port = endPoint.Port
				};

				var ws = new WebSocketSharp.WebSocket(uriBuilder.ToString());
				// WebSocketSharp logs every refused attempt as Fatal with a stack trace; the loop above
				// logs one readable line instead.
				ws.Log.Output = (data, file) => { };
				SocketConnection = ws;

				ws.OnOpen += (sender, e) => {
					LastResponseTime = DateTime.UtcNow;
					ConnectionWaitHandle.Set();
				};

				ws.OnClose += (sender, e) => {
					if (!ReferenceEquals(sender, SocketConnection)) return; // an older, replaced socket
					if ((e.Code == (ushort)CloseStatusCode.Abnormal && ConnectionWaitHandle.WaitOne(0)) || e.Code != (ushort)CloseStatusCode.Abnormal) {
						Disconnected?.Invoke(this, new SocketDisconnectedEventArgs {
							Socket = ws,
							Info = ClientInfo,
							Code = e.Code,
							Reason = e.Reason
						});
					}
				};

				ws.OnMessage += (sender, e) => {
					using (var inputStream = new MemoryStream(e.RawData))
					using (var outputStream = new MemoryStream()) {
						var payloadType = Int32Proxy.Deserialize(inputStream);

						if (payloadType == 0x42) { // Packet/Raw Data
							var packetType = EnumProxy<PacketType>.Deserialize(inputStream);

							switch (packetType) {
								case PacketType.MagicBytes:
									Int32Proxy.Serialize(outputStream, 0x42);
									EnumProxy<PacketType>.Serialize(outputStream, PacketType.MagicBytes);
									ArrayProxy<byte>.Serialize(outputStream, MAGIC_BYTES, ByteProxy.Serialize);

									SendBytesSync(outputStream.ToArray());
									break;
								case PacketType.ClientInfo:
									SendSync(PacketType.ClientInfo, ClientInfo, serverType: ClientInfo.Type);
									break;
								case PacketType.Ping:
									LastResponseTime = DateTime.UtcNow;
									SendPacketSync(PacketType.Pong);
									break;
								default:
									break;
							}
						} else { // JSON Object
							var bytes = ArrayProxy<byte>.Deserialize(inputStream, ByteProxy.Deserialize);
							var payload = Payload.Decode<object>(Encoding.UTF8.GetString(bytes), CryptoProvider, out var payloadObj);

							switch (payloadObj.Type) {
								case PacketType.ConnectionStatus:
									var status = (SocketConnectionStatus)payload;

									if (status.Connected) {
										Connected?.Invoke(this, new SocketConnectedEventArgs {
											Socket = SocketConnection
										});
									} else if (status.Rejected) {
										ConnectionRejectedHandle.Set();
										lastRejectedUtc = DateTime.UtcNow;

										ConnectionRejected?.Invoke(this, new SocketConnectionRejectedEventArgs {
											Info = ClientInfo,
											Socket = SocketConnection,
											Reason = status.DisconnectReason
										});

										SocketConnection.Close();
									}
									break;
								default:
									DataReceived?.Invoke(this, new SocketDataReceivedEventArgs {
										Socket = SocketConnection,
										Payload = payloadObj,
										Data = payload
									});

									if (receiveTasks.ContainsKey(payloadObj.ConversationId)) {
										receiveTasks[payloadObj.ConversationId].SetResult(payload);
										receiveTasks.Remove(payloadObj.ConversationId);
									}

									break;
							}
						}
					}
				};

				return ws;
			}

			#region Send
			public async Task SendBytes(byte[] bytes) {
				await Task.Run(() => {
					SendBytesSync(bytes);
				});
			}

			public void SendBytesSync(byte[] bytes) {
				if (!IsConnected) return; // master offline: the reconnect loop re-syncs state when back
				SocketConnection.Send(bytes);
			}

			public async Task SendPacket(PacketType type) {
				await Task.Run(() => {
					SendPacketSync(type);
				});
			}

			public void SendPacketSync(PacketType type) {
				using (var stream = new MemoryStream()) {
					Int32Proxy.Serialize(stream, 0x42);
					EnumProxy<PacketType>.Serialize(stream, type);

					if (IsConnected) SocketConnection.Send(stream.ToArray());
				}
			}

			public async Task<object> Send(PacketType type, object payload, bool oneWay = true, Guid conversationId = default, ServerType serverType = ServerType.None) {
				Payload payloadObj = default;
				await Task.Run(() => {
					if (!IsConnected) return;
					SocketConnection.Send(Payload.Encode(type, payload, CryptoProvider, out payloadObj, oneWay, conversationId, serverType));
				});

				if (oneWay)
					return null;

				receiveTasks[payloadObj.ConversationId] = new TaskCompletionSource<object>();
				receiveTask = receiveTasks[payloadObj.ConversationId].Task;

				var timeout = Task.Delay(TimeSpan.FromSeconds(RECEIVE_TIMEOUT));

				//Console.WriteLine("awaiting receive or timeout");
				if (await Task.WhenAny(receiveTask, timeout) == timeout) {
					Log.Error($"Failed to receive data within {RECEIVE_TIMEOUT} second(s).");
					receiveTask = null;
					return null;
				}

				var taskData = await receiveTask;
				//Console.WriteLine("awaited response");
				receiveTask = null;

				return taskData;
			}

			public object SendSync(PacketType type, object payload, bool oneWay = true, Guid conversationId = default, ServerType serverType = ServerType.None) {
				return Task.Run(async () => {
					return await Send(type, payload, oneWay, conversationId, serverType);
				}).Result;
			}
			#endregion
		}
	}
}
