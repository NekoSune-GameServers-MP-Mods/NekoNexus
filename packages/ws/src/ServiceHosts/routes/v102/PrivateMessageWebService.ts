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

import { ApiVersion } from '@/utils/enums';
import { PrivateMessage, PublicProfile, UserAccount } from '@festivaldev/nekonexus-models';
import { MessageThreadView } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import { Int32Proxy, ListProxy, StringProxy } from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  MessageThreadViewProxy,
  PrivateMessageViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import { PrivateMessageViewProxy as V2PrivateMessageViewProxy } from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import V2PrivateMessageWebService from '../v2/PrivateMessageWebService';
import { bridge, callV2, sessionTokenFor } from './LegacyBridge';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';

export default class PrivateMessageWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'PrivateMessageWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IPrivateMessageWebServiceContract'; }

  static async GetAllMessageThreadsForUser_1(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Same as the paged variant, first page.
    return bridge(this, 'GetAllMessageThreadsForUser_1', data, outputStream, async (bytes, out) => {
      const cmid = Int32Proxy.Deserialize(bytes);
      const input: number[] = [];
      Int32Proxy.Serialize(input, cmid);
      Int32Proxy.Serialize(input, 1);
      out.push(...((await this.GetAllMessageThreadsForUser_2(input, [])) ?? []));
    });
  }

  static async GetAllMessageThreadsForUser_2(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const pageNumber = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetAllMessageThreadsForUser_2', cmid, pageNumber);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const messages = (
          await PrivateMessage.findAll({
            where: {
              [Op.or]: {
                FromCmid: userAccount.Cmid,
                ToCmid: userAccount.Cmid,
              },
            },
          })
        ).reduce((acc: any, curr: PrivateMessage) => {
          const threadId = [curr.FromCmid, curr.ToCmid].sort().join(',');

          if (!acc[threadId]) {
            acc[threadId] = [];
          }

          acc[threadId].push(curr);
          return acc;
        }, {});

        const threads: MessageThreadView[] = [];

        for (const messageGroup of Object.values(messages)) {
          const filteredMessages = (messageGroup as any).find(
            (_: PrivateMessage) =>
              (_.FromCmid === userAccount.Cmid && !_.IsDeletedBySender) ||
              (_.ToCmid === userAccount.Cmid && !_.IsDeletedByReceiver),
          );

          if (filteredMessages.length) {
            const message = filteredMessages[filteredMessages.length - 1];

            const otherCmid = message.FromCmid !== userAccount.Cmid ? message.FromCmid : message.ToCmid;
            const otherProfile = await PublicProfile.findOne({ where: { Cmid: otherCmid } });

            if (otherProfile) {
              threads.push(
                new MessageThreadView({
                  ThreadId: otherCmid,
                  ThreadName: otherProfile.Name,
                  MessageCount: filteredMessages.length,
                  LastMessagePreview: message.ContentText,
                  LastUpdate: message.DateSent,
                  HasNewMessages: filteredMessages.some(
                    (_: PrivateMessage) => _.ToCmid === userAccount.Cmid && !_.IsRead,
                  ),
                }),
              );
            }
          }
        }

        ListProxy.Serialize<MessageThreadView>(outputStream, threads, MessageThreadViewProxy.Serialize);
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllMessageThreadsForUser_2', e);
    }

    return null;
  }

  static async GetThreadMessages(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'GetThreadMessages', data, outputStream, async (bytes, out) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const otherCmid = Int32Proxy.Deserialize(bytes);
      const pageNumber = Int32Proxy.Deserialize(bytes);
      const v2 = await callV2(V2PrivateMessageWebService, 'GetThreadMessages', (i) => {
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, otherCmid);
        Int32Proxy.Serialize(i, pageNumber);
      });
      const messages = v2.length ? ListProxy.Deserialize([...v2], V2PrivateMessageViewProxy.Deserialize) : [];
      ListProxy.Serialize(out, messages, PrivateMessageViewProxy.Serialize);
    });
  }

  static async SendMessage(data: number[], outputStream: number[]): Promise<number[] | null> {
    // The 2.0 handler stores the mail and pings the receiver's client (PC and Android) to refresh.
    return bridge(this, 'SendMessage', data, outputStream, async (bytes, out) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const receiverCmid = Int32Proxy.Deserialize(bytes);
      const content = StringProxy.Deserialize(bytes);
      const v2 = await callV2(V2PrivateMessageWebService, 'SendMessage', (i) => {
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, receiverCmid);
        StringProxy.Serialize(i, content);
      });
      PrivateMessageViewProxy.Serialize(out, (v2.length ? V2PrivateMessageViewProxy.Deserialize([...v2]) : null) as any);
    });
  }

  static async GetMessageWithId(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'GetMessageWithId', data, outputStream, async (bytes, out) => {
      const messageId = Int32Proxy.Deserialize(bytes);
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const v2 = await callV2(V2PrivateMessageWebService, 'GetMessageWithIdForCmid', (i) => {
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, messageId);
      });
      PrivateMessageViewProxy.Serialize(out, (v2.length ? V2PrivateMessageViewProxy.Deserialize([...v2]) : null) as any);
    });
  }

  static async MarkThreadAsRead(data: number[], outputStream: number[]): Promise<number[] | null> {
    // 4.3.10 expects an empty reply.
    return bridge(this, 'MarkThreadAsRead', data, outputStream, async (bytes) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const otherCmid = Int32Proxy.Deserialize(bytes);
      await callV2(V2PrivateMessageWebService, 'MarkThreadAsRead', (i) => {
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, otherCmid);
      });
    });
  }

  static async DeleteThread(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'DeleteThread', data, outputStream, async (bytes) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const otherCmid = Int32Proxy.Deserialize(bytes);
      await callV2(V2PrivateMessageWebService, 'DeleteThread', (i) => {
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, otherCmid);
      });
    });
  }
}
