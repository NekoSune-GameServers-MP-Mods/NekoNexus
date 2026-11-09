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

import { Log } from '@/utils';
import {
  ArrayProxy,
  ByteProxy,
  CommActorInfoProxy,
  DictionaryProxy,
  EndOfMatchDataProxy,
  EnumProxy,
  GameActorInfoProxy,
  GameRoomDataProxy,
  Int32Proxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import type { ChannelType } from 'discord.js';
import WebSocketPacketType from './PacketType';
import type RijndaelCryptoProvider from './RijndaelCryptoProvider';
import { ServerType } from './WebSocket';

enum PayloadFlags {
  IsSerialized = 1 << 0,
  IsEncrypted = 1 << 1,
  IsOneWay = 1 << 2,
}

export default class WebSocketPayload {
  Type: WebSocketPacketType;
  Data: string;
  ServerType: ServerType;
  Flags: PayloadFlags;
  ConversationId: string;

  get IsSerialized(): boolean {
    return (this.Flags & PayloadFlags.IsSerialized) === PayloadFlags.IsSerialized;
  }
  private set IsSerialized(value: boolean) {
    if (value) {
      this.Flags |= PayloadFlags.IsSerialized;
    } else {
      this.Flags &= ~PayloadFlags.IsSerialized;
    }
  }

  get IsEncrypted(): boolean {
    return (this.Flags & PayloadFlags.IsEncrypted) === PayloadFlags.IsEncrypted;
  }
  private set IsEncrypted(value: boolean) {
    if (value) {
      this.Flags |= PayloadFlags.IsEncrypted;
    } else {
      this.Flags &= ~PayloadFlags.IsEncrypted;
    }
  }

  get IsOneWay(): boolean {
    return (this.Flags & PayloadFlags.IsOneWay) === PayloadFlags.IsOneWay;
  }
  private set IsOneWay(value: boolean) {
    if (value) {
      this.Flags |= PayloadFlags.IsOneWay;
    } else {
      this.Flags &= ~PayloadFlags.IsOneWay;
    }
  }

  constructor(params: Partial<WebSocketPayload> = {}) {
    Object.assign(this, params);
  }

  static Encode(
    type: WebSocketPacketType,
    data: any,
    cryptoProvider: RijndaelCryptoProvider | null,
    oneWay: boolean = false,
    conversationId: string | null = null,
    serverType: ServerType = ServerType.None,
  ): [number[] | null, WebSocketPayload | null] {
    if (!conversationId) {
      conversationId = crypto.randomUUID();
    }

    const payloadObj = new WebSocketPayload({
      Type: type,
      ServerType: serverType,
      ConversationId: conversationId,
      IsOneWay: oneWay,
    });

    const outputBytes: any[] = [];
    const bytes: any[] = [];

    switch (type) {
      case WebSocketPacketType.MagicBytes:
      case WebSocketPacketType.Ping:
      case WebSocketPacketType.Pong:
        break;
      case WebSocketPacketType.ClientInfo:
      case WebSocketPacketType.ConnectionStatus:
        StringProxy.Serialize(bytes, JSON.stringify(data));
        break;
      case WebSocketPacketType.Command:
      case WebSocketPacketType.Error:
      case WebSocketPacketType.ChatMessage:
        payloadObj.IsEncrypted = true;

        StringProxy.Serialize(bytes, JSON.stringify(data));
        break;
      case WebSocketPacketType.RoomChatMessage: {
        payloadObj.IsEncrypted = true;

        const list = data as any[];
        StringProxy.Serialize(bytes, JSON.stringify(data));
        GameRoomDataProxy.Serialize(bytes, list[0]);
        break;
      }
      case WebSocketPacketType.CommandOutput:
        payloadObj.IsEncrypted = true;

        StringProxy.Serialize(bytes, data);
        break;
      case WebSocketPacketType.Monitoring:
      case WebSocketPacketType.BanPlayer:
      // Realtime notifications (ws -> Comm/Game server): all carry a simple { key: value } dict,
      // encoded the same way as BanPlayer. These were missing from the switch, so every realtime
      // push (mail/clan refresh, live wallet, boost) silently fell through to "Unknown type" and
      // never sent. Encode them here.
      case WebSocketPacketType.NotifyInboxMessage:
      case WebSocketPacketType.NotifyInboxRequests:
      case WebSocketPacketType.NotifyClanMembers:
      case WebSocketPacketType.NotifyClanChat:
      case WebSocketPacketType.NotifyWallet:
      case WebSocketPacketType.NotifyStats:
      case WebSocketPacketType.NotifyPrivateChat:
      case WebSocketPacketType.SetBoost:
      case WebSocketPacketType.SetBotsConfig:
      case WebSocketPacketType.MatchResult:
      case WebSocketPacketType.LegacySession:
        payloadObj.IsEncrypted = true;

        DictionaryProxy.Serialize<string, object>(bytes, data, StringProxy.Serialize, (stream, instance) => {
          StringProxy.Serialize(stream, JSON.stringify(instance));
        });
        break;
      case WebSocketPacketType.PlayerJoined:
      case WebSocketPacketType.PlayerLeft:
        payloadObj.IsEncrypted = true;

        CommActorInfoProxy.Serialize(bytes, data);
        break;
      case WebSocketPacketType.RoomOpened:
      case WebSocketPacketType.RoomClosed:
        payloadObj.IsEncrypted = true;

        GameRoomDataProxy.Serialize(bytes, data);
        break;
      case WebSocketPacketType.PlayerJoinedRoom:
      case WebSocketPacketType.PlayerLeftRoom: {
        payloadObj.IsEncrypted = true;

        const list = data as any[];
        GameActorInfoProxy.Serialize(bytes, list[0]);
        GameRoomDataProxy.Serialize(bytes, list[1]);
        break;
      }
      case WebSocketPacketType.RoundStarted: {
        payloadObj.IsEncrypted = true;

        GameRoomDataProxy.Serialize(bytes, data);
        break;
      }
      case WebSocketPacketType.RoundEnded: {
        payloadObj.IsEncrypted = true;

        const list = data as any[];
        GameRoomDataProxy.Serialize(bytes, list[0]);
        EndOfMatchDataProxy.Serialize(bytes, list[1]);
        break;
      }
      case WebSocketPacketType.OpenRoom:
        /// TODO
        break;
      case WebSocketPacketType.CloseRoom:
        payloadObj.IsEncrypted = true;

        Int32Proxy.Serialize(bytes, data);
        break;
      default:
        Log.warn(`Rejecting to encode payload of type ${WebSocketPacketType[type]}(${type}): Unknown type.`);
        return [null, null];
    }

    if (cryptoProvider != null && payloadObj.IsEncrypted) {
      payloadObj.Data = cryptoProvider.encrypt(Buffer.from(bytes)).toString('base64');
    } else {
      payloadObj.Data = Buffer.from(bytes).toString('base64');
    }

    Int32Proxy.Serialize(outputBytes, 0x21);
    ArrayProxy.Serialize<number>(outputBytes, [...Buffer.from(JSON.stringify(payloadObj))], ByteProxy.Serialize);

    return [outputBytes, payloadObj];
  }

  static Decode<T>(json: string, crypto: RijndaelCryptoProvider | null): [T | null, WebSocketPayload | null] {
    if (!json.trim().length) return [null, null];

    let payloadObj;
    try {
      payloadObj = new WebSocketPayload(JSON.parse(json));
    } catch (e: any) {
      Log.info(json);
      Log.error(e);

      return [null, null];
    }

    if (!payloadObj) return [null, null];

    let data = Buffer.from(payloadObj.Data, 'base64');
    if (crypto != null && payloadObj.IsEncrypted) {
      try {
        data = crypto.decrypt(data);
      } catch (e: any) {
        // A failed decrypt almost always means the connecting server's passphrase
        // doesn't match this server's ServerCredentials entry for its GUID. Drop the
        // packet instead of letting the exception crash the whole web services process.
        Log.error(
          `Failed to decrypt ${WebSocketPacketType[payloadObj.Type]}(${payloadObj.Type}) payload. ` +
            `This usually indicates a server passphrase mismatch.`,
        );
        Log.error(e);

        return [null, null];
      }
    }

    const bytes = [...data];
    let result: any;
    switch (payloadObj.Type) {
      case WebSocketPacketType.MagicBytes:
      case WebSocketPacketType.Ping:
      case WebSocketPacketType.Pong:
        break;
      case WebSocketPacketType.ClientInfo:
      case WebSocketPacketType.ConnectionStatus:
      case WebSocketPacketType.Command:
      case WebSocketPacketType.Error:
      case WebSocketPacketType.ChatMessage:
        result = JSON.parse(StringProxy.Deserialize(bytes));
        break;
      case WebSocketPacketType.CommandOutput:
        result = StringProxy.Deserialize(bytes);
        break;
      case WebSocketPacketType.RoomChatMessage:
        result = [JSON.parse(StringProxy.Deserialize(bytes)), GameRoomDataProxy.Deserialize(bytes)];

        break;
      case WebSocketPacketType.Monitoring:
      case WebSocketPacketType.BanPlayer:
      case WebSocketPacketType.NotifyInboxMessage:
      case WebSocketPacketType.NotifyInboxRequests:
      case WebSocketPacketType.NotifyClanMembers:
      case WebSocketPacketType.NotifyClanChat:
      case WebSocketPacketType.NotifyWallet:
      case WebSocketPacketType.NotifyStats:
      case WebSocketPacketType.ClanChatMessage:
      case WebSocketPacketType.SetBoost:
      case WebSocketPacketType.SetBotsConfig:
      case WebSocketPacketType.MatchResult:
      case WebSocketPacketType.LegacySession:
        result = DictionaryProxy.Deserialize<string, object>(bytes, StringProxy.Deserialize, (stream) =>
          JSON.parse(StringProxy.Deserialize(stream)),
        );
        break;
      case WebSocketPacketType.PlayerJoined:
      case WebSocketPacketType.PlayerLeft:
        result = {
          Cmid: Int32Proxy.Deserialize(bytes),
          RemoteIP: StringProxy.Deserialize(bytes),
          RemotePort: Int32Proxy.Deserialize(bytes),
          Channel: EnumProxy.Deserialize<ChannelType>(bytes),
          LocalIP: StringProxy.Deserialize(bytes),
          LocalPort: Int32Proxy.Deserialize(bytes),
        };
        break;
      case WebSocketPacketType.RoomOpened:
      case WebSocketPacketType.RoomClosed:
        result = GameRoomDataProxy.Deserialize(bytes);
        break;
      case WebSocketPacketType.PlayerJoinedRoom:
      case WebSocketPacketType.PlayerLeftRoom:
        result = [GameActorInfoProxy.Deserialize(bytes), GameRoomDataProxy.Deserialize(bytes)];
        break;
      case WebSocketPacketType.RoundStarted:
        result = GameRoomDataProxy.Deserialize(bytes);
        break;
      case WebSocketPacketType.RoundEnded:
        result = [GameRoomDataProxy.Deserialize(bytes), EndOfMatchDataProxy.Deserialize(bytes)];
        break;
      case WebSocketPacketType.OpenRoom:
        /// TODO
        break;
      case WebSocketPacketType.CloseRoom:
        result = Int32Proxy.Deserialize(bytes);
        break;
      default:
        Log.warn(
          `Rejecting to decode payload of type ${WebSocketPacketType[payloadObj.Type]}(${payloadObj.Type}): Unknown type.`,
        );
        break;
    }

    return [result as T, payloadObj];
  }
}
