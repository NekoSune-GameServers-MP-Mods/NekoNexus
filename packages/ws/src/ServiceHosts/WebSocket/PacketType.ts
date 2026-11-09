/*
 * Copyright (C) 2017, 2021-2024 Team FESTIVAL
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

enum WebSocketPacketType {
  MagicBytes = 1,
  ClientInfo,
  ConnectionStatus,

  Ping,
  Pong,

  Command,
  CommandOutput,
  Monitoring,
  Error,

  ChatMessage = 1 << 10,
  RoomChatMessage = (1 << 10) + 1,
  PlayerList = (1 << 10) + 2,
  PlayerJoined = (1 << 10) + 3,
  PlayerLeft = (1 << 10) + 4,
  RoomOpened = (1 << 10) + 5,
  RoomClosed = (1 << 10) + 6,
  PlayerJoinedRoom = (1 << 10) + 7,
  PlayerLeftRoom = (1 << 10) + 8,
  RoundStarted = (1 << 10) + 9,
  RoundEnded = (1 << 10) + 10,

  OpenRoom = (1 << 10) + 11,
  CloseRoom = (1 << 10) + 12,
  BanPlayer = (1 << 10) + 13,

  // Web service -> Comm server realtime notifications (must match WebSocket.PacketType.cs).
  NotifyInboxMessage = (1 << 10) + 14,
  NotifyInboxRequests = (1 << 10) + 15,
  NotifyClanMembers = (1 << 10) + 16,
  NotifyClanChat = (1 << 10) + 17,

  // Web service -> Comm server: wallet changed -> push new balance to the online lobby peer.
  NotifyWallet = (1 << 10) + 18,
  // Web service -> Game servers (broadcast): set/clear the global coin+xp boost event.
  SetBoost = (1 << 10) + 19,
  // Game server -> web service: a player's finished-match result (for match history).
  MatchResult = (1 << 10) + 20,
  // Web service -> Comm server: a player's stats changed -> push new xp/points to their lobby client.
  NotifyStats = (1 << 10) + 21,
  // Comm server -> web service: an in-game CLAN chat line, forwarded so the website's clan channel
  // mirrors in-game clan chat. { Cmid, Name, Message }.
  ClanChatMessage = (1 << 10) + 22,
  // Web service -> Comm server: deliver a website friend DM as an in-game private (whisper) chat
  // message to the online target. { TargetCmid, Cmid, Name, Message }.
  NotifyPrivateChat = (1 << 10) + 23,
  // Web service -> Game servers (broadcast): admin-controlled AI fill-bots toggle (BETA, off by
  // default). { Enabled, FillTarget, MaxBots }.
  SetBotsConfig = (1 << 10) + 24,
  // Game server -> web service (request/reply): live session of an Android (4.3.10) player,
  // who reaches realtime with only a CMID. { Cmid } -> { SessionId } (must match WebSocket.PacketType.cs).
  LegacySession = (1 << 10) + 25,
  // Comm -> ws -> Game servers: Android in-match chat. { Cmid, Message, Context }.
  LegacyRoomChat = (1 << 10) + 26,
  // Game -> ws -> Comm: in-match chat for an Android phone. { TargetCmid, Cmid, ActorId, Name, Message, AccessLevel, Context }.
  LegacyChatDeliver = (1 << 10) + 27,
}

export default WebSocketPacketType;
