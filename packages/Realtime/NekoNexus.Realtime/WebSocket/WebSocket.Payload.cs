using log4net;
using Newtonsoft.Json;
using NekoNexus.Realtime.Server.Comm;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cmune.DataCenter.Common.Entities;
using UberStrike.Core.Models;
using UberStrike.Core.Serialization;

namespace NekoNexus {
	public partial class WebSocket {
		[Flags]
		public enum PayloadFlags {
			IsSerialized = 1 << 0,
			IsEncrypted = 1 << 1,
			IsOneWay = 1 << 2
		}

		public class Payload {
			protected static readonly ILog Log = LogManager.GetLogger(nameof(Payload));

			public PacketType Type;
			public string Data;
			public ServerType ServerType;
			public PayloadFlags Flags;
			public Guid ConversationId;

			[JsonIgnore]
			public bool IsSerialized {
				get {
					return Flags.HasFlag(PayloadFlags.IsSerialized);
				}

				private set {
					if (value) {
						Flags |= PayloadFlags.IsSerialized;
					} else {
						Flags &= ~PayloadFlags.IsSerialized;
					}
				}
			}

			[JsonIgnore]
			public bool IsEncrypted {
				get {
					return Flags.HasFlag(PayloadFlags.IsEncrypted);
				}

				private set {
					if (value) {
						Flags |= PayloadFlags.IsEncrypted;
					} else {
						Flags &= ~PayloadFlags.IsEncrypted;
					}
				}
			}

			[JsonIgnore]
			public bool IsOneWay {
				get {
					return Flags.HasFlag(PayloadFlags.IsOneWay);
				}

				private set {
					if (value) {
						Flags |= PayloadFlags.IsOneWay;
					} else {
						Flags &= ~PayloadFlags.IsOneWay;
					}
				}
			}

			public static byte[] Encode(PacketType type, object data, RijndaelManaged crypto, out Payload payloadObj, bool oneWay = false, Guid conversationId = default, ServerType serverType = ServerType.None) {
				if (conversationId == default) {
					conversationId = Guid.NewGuid();
				}

				payloadObj = new Payload {
					Type = type,
					ServerType = serverType,
					ConversationId = conversationId,
					IsOneWay = oneWay,
				};

				using (var outputStream = new MemoryStream())
				using (var bytes = new MemoryStream()) {
					switch (type) {
						case PacketType.MagicBytes:
						case PacketType.Ping:
						case PacketType.Pong:
							break;
						case PacketType.ClientInfo:
						case PacketType.ConnectionStatus:
							StringProxy.Serialize(bytes, JsonConvert.SerializeObject(data));
							break;
						case PacketType.Command:
						case PacketType.Error:
						case PacketType.ChatMessage:
							payloadObj.IsEncrypted = true;

							StringProxy.Serialize(bytes, JsonConvert.SerializeObject(data));
							break;
						case PacketType.RoomChatMessage: {
							payloadObj.IsEncrypted = true;

							var list = data as object[];
							StringProxy.Serialize(bytes, JsonConvert.SerializeObject(list[0]));
							GameRoomDataProxy.Serialize(bytes, (GameRoomData)list[1]);
							break;
						}
						case PacketType.CommandOutput:
							payloadObj.IsEncrypted = true;

							StringProxy.Serialize(bytes, (string)data);
							break;
						case PacketType.Monitoring:
						case PacketType.BanPlayer:
						case PacketType.MatchResult:
						case PacketType.LegacySession:
						case PacketType.ClanChatMessage:
							payloadObj.IsEncrypted = true;

							DictionaryProxy<string, object>.Serialize(bytes, (Dictionary<string, object>)data, StringProxy.Serialize, (stream, instance) => {
								StringProxy.Serialize(stream, JsonConvert.SerializeObject(instance));
							});
							break;
						case PacketType.PlayerJoined:
						case PacketType.PlayerLeft:
							payloadObj.IsEncrypted = true;

							var peer = (CommPeer)data;
							Int32Proxy.Serialize(bytes, peer.Actor.Cmid);
							StringProxy.Serialize(bytes, peer.RemoteIPAddress.ToString());
							Int32Proxy.Serialize(bytes, peer.RemotePort);
							EnumProxy<ChannelType>.Serialize(bytes, peer.Actor.ActorInfo.Channel);
							StringProxy.Serialize(bytes, peer.LocalIPAddress.ToString());
							Int32Proxy.Serialize(bytes, peer.LocalPort);
							break;
						case PacketType.RoomOpened:
						case PacketType.RoomClosed:
							payloadObj.IsEncrypted = true;

							GameRoomDataProxy.Serialize(bytes, (GameRoomData)data);
							break;
						case PacketType.PlayerJoinedRoom:
						case PacketType.PlayerLeftRoom: {
							payloadObj.IsEncrypted = true;

							var list = data as object[];
							GameActorInfoProxy.Serialize(bytes, (GameActorInfo)list[0]);
							GameRoomDataProxy.Serialize(bytes, (GameRoomData)list[1]);
							break;
						}
						case PacketType.RoundStarted: {
							payloadObj.IsEncrypted = true;

							GameRoomDataProxy.Serialize(bytes, (GameRoomData)data);
							break;
						}
						case PacketType.RoundEnded: {
							payloadObj.IsEncrypted = true;

							var list = data as object[];
							GameRoomDataProxy.Serialize(bytes, (GameRoomData)list[0]);
							EndOfMatchDataProxy.Serialize(bytes, (EndOfMatchData)list[1]);
							break;
						}
						case PacketType.OpenRoom:
							/// TODO
							break;
						case PacketType.CloseRoom:
							payloadObj.IsEncrypted = true;

							Int32Proxy.Serialize(bytes, (int)data);
							break;
						default:
							Log.Warn($"Rejecting to encode payload of type {type}: Unknown type.");
							return null;
					}

					if (crypto != null && payloadObj.IsEncrypted) {
						using (var memoryStream = new MemoryStream()) {
							using (var cryptoStream = new CryptoStream(memoryStream, crypto.CreateEncryptor(), CryptoStreamMode.Write)) {
								var _bytes = bytes.ToArray();
								cryptoStream.Write(_bytes, 0, _bytes.Length);
							}

							payloadObj.Data = Convert.ToBase64String(memoryStream.ToArray());
						}
					} else {
						payloadObj.Data = Convert.ToBase64String(bytes.ToArray());
					}
				}

				return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payloadObj));
			}

			public static T Decode<T>(string json, RijndaelManaged crypto, out Payload payloadObj) {
				if (string.IsNullOrWhiteSpace(json)) {
					payloadObj = default;
					return default;
				}

				try {
					payloadObj = JsonConvert.DeserializeObject<Payload>(json);
				} catch (Exception e) {
					Log.Info(json);
					Log.Error(e);

					payloadObj = default;
					return default;
				}

				var data = Convert.FromBase64String(payloadObj.Data);

				if (crypto != null && payloadObj.IsEncrypted) {

					using (var memoryStream = new MemoryStream()) {
						using (var cryptoStream = new CryptoStream(memoryStream, crypto.CreateDecryptor(), CryptoStreamMode.Write)) {
							cryptoStream.Write(data, 0, data.Length);
						}

						data = memoryStream.ToArray();
					}

				}

				using (var bytes = new MemoryStream(data)) {
					var result = default(object);

					switch (payloadObj.Type) {
						case PacketType.MagicBytes:
						case PacketType.Ping:
						case PacketType.Pong:
							break;
						case PacketType.ClientInfo:
							result = JsonConvert.DeserializeObject<SocketInfo>(StringProxy.Deserialize(bytes));
							break;
						case PacketType.ConnectionStatus:
							result = JsonConvert.DeserializeObject<SocketConnectionStatus>(StringProxy.Deserialize(bytes));
							break;
						case PacketType.Command:
							result = JsonConvert.DeserializeObject<SocketCommand>(StringProxy.Deserialize(bytes));
							break;
						case PacketType.CommandOutput:
							result = StringProxy.Deserialize(bytes);
							break;
						case PacketType.ChatMessage:
							result = JsonConvert.DeserializeObject<SocketChatMessage>(StringProxy.Deserialize(bytes));
							break;
						case PacketType.RoomChatMessage:
							result = new object[] {
								JsonConvert.DeserializeObject<SocketChatMessage>(StringProxy.Deserialize(bytes)),
								GameRoomDataProxy.Deserialize(bytes),
							};
							break;
						case PacketType.Error:
							result = JsonConvert.DeserializeObject<RealtimeError>(StringProxy.Deserialize(bytes));
							break;
						case PacketType.Monitoring:
						case PacketType.BanPlayer:
						// Realtime notifications pushed from the web service (mail/clan refresh, live
						// wallet, boost) - all simple { key: value } dicts, decoded like BanPlayer. These
						// were missing here, so the packets the ws sent never decoded and the handlers
						// (which cast e.Data to Dictionary<string,object>) silently failed.
						case PacketType.NotifyInboxMessage:
						case PacketType.NotifyInboxRequests:
						case PacketType.NotifyClanMembers:
						case PacketType.NotifyClanChat:
						case PacketType.NotifyWallet:
						case PacketType.NotifyStats:
						case PacketType.NotifyPrivateChat:
						case PacketType.SetBoost:
						case PacketType.SetBotsConfig:
						case PacketType.MatchResult:
						case PacketType.LegacySession:
							result = DictionaryProxy<string, object>.Deserialize(bytes, StringProxy.Deserialize, (stream) => {
								return JsonConvert.DeserializeObject<object>(StringProxy.Deserialize(stream));
							});
							break;
						case PacketType.PlayerJoined:
						case PacketType.PlayerLeft:
							result = CommActorInfoProxy.Deserialize(bytes);
							break;
						case PacketType.RoomOpened:
						case PacketType.RoomClosed:
							result = GameRoomDataProxy.Deserialize(bytes);
							break;
						case PacketType.PlayerJoinedRoom:
						case PacketType.PlayerLeftRoom:
							result = new object[] {
								GameActorInfoProxy.Deserialize(bytes),
								GameRoomDataProxy.Deserialize(bytes),
							};
							break;
						case PacketType.RoundStarted:
							result = GameRoomDataProxy.Deserialize(bytes);
							break;
						case PacketType.RoundEnded:
							result = new object[] {
								GameRoomDataProxy.Deserialize(bytes),
								EndOfMatchDataProxy.Deserialize(bytes)
							};
							break;
						case PacketType.OpenRoom:
							/// TODO
							break;
						case PacketType.CloseRoom:
							result = Int32Proxy.Deserialize(bytes);
							break;
						default:
							Log.Warn($"Rejecting to decode payload of type {payloadObj.Type}: Unknown type.");
							break;
					}

					return (T)result;
				}

			}
		}
	}
}
