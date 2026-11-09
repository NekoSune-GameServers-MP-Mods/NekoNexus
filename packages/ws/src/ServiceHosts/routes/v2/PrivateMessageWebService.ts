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

import NekoNexusService from '@/NekoNexusService';
import { RealtimeNotify } from '@/utils/RealtimeNotify';
import { ApiVersion } from '@/utils/enums';
import { PrivateMessage, PublicProfile } from '@festivaldev/nekonexus-models';
import { MessageThreadView, PrivateMessageView } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  BooleanProxy,
  Int32Proxy,
  ListProxy,
  MessageThreadViewProxy,
  PrivateMessageViewProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';

export default class PrivateMessageWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'PrivateMessageWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Current;
  }
  // protected static get ServiceInterface(): string { return 'IPrivateMessageWebServiceContract'; }

  static async DeleteThread(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const otherCmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('DeleteThread', authToken, otherCmid);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const messages = await PrivateMessage.findAll({
            where: {
              [Op.or]: [
                {
                  FromCmid: steamMember.Cmid,
                  ToCmid: otherCmid,
                },
                {
                  FromCmid: otherCmid,
                  ToCmid: steamMember.Cmid,
                },
              ],
            },
          });

          for (const message of messages) {
            if (message.FromCmid === steamMember.Cmid) {
              await message.update({ IsDeletedBySender: true });
            } else {
              await message.update({ IsDeletedByReceiver: true });
            }
          }

          BooleanProxy.Serialize(outputStream, true);
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('DeleteThread', error);
    }

    return null;
  }

  static async GetAllMessageThreadsForUser(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const pageNumber = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetAllMessageThreadsForUser', authToken, pageNumber);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const messages = (
            await PrivateMessage.findAll({
              where: {
                [Op.or]: {
                  FromCmid: steamMember.Cmid,
                  ToCmid: steamMember.Cmid,
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
            // .filter (NOT .find) - we need ALL non-deleted messages in the thread, not just one.
            const filteredMessages = (messageGroup as any).filter(
              (_: PrivateMessage) =>
                (_.FromCmid === steamMember.Cmid && !_.IsDeletedBySender) ||
                (_.ToCmid === steamMember.Cmid && !_.IsDeletedByReceiver),
            );

            if (filteredMessages.length) {
              const message = filteredMessages[filteredMessages.length - 1];

              const otherCmid = message.FromCmid !== steamMember.Cmid ? message.FromCmid : message.ToCmid;
              const otherProfile = await PublicProfile.findOne({ where: { Cmid: otherCmid } });

              // Always show the thread. System mail (FromCmid 0) has no profile, and some accounts
              // have an empty name - fall back to a safe name so the client never gets null/empty
              // (which crashed the mailbox when opening such a thread).
              const threadName =
                (otherProfile?.Name && otherProfile.Name.trim()) ||
                (message.FromName && message.FromName.trim()) ||
                'System';

              threads.push(
                new MessageThreadView({
                  ThreadId: otherCmid,
                  ThreadName: threadName,
                  MessageCount: filteredMessages.length,
                  LastMessagePreview: message.ContentText ?? '',
                  LastUpdate: message.DateSent,
                  HasNewMessages: filteredMessages.some(
                    (_: PrivateMessage) => _.ToCmid === steamMember.Cmid && !_.IsRead,
                  ),
                }),
              );
            }
          }

          ListProxy.Serialize<MessageThreadView>(outputStream, threads, MessageThreadViewProxy.Serialize);
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetAllMessageThreadsForUser', error);
    }

    return null;
  }

  static async GetMessageWithIdForCmid(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const messageId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetMessageWithIdForCmid', authToken, messageId);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const message = await PrivateMessage.findOne({
            where: {
              PrivateMessageId: messageId,
              [Op.or]: [
                {
                  FromCmid: steamMember.Cmid,
                  IsDeletedBySender: false,
                },
                {
                  ToCmid: steamMember.Cmid,
                  IsDeletedByReceiver: false,
                },
              ],
            },
          });

          if (message) {
            PrivateMessageViewProxy.Serialize(
              outputStream,
              new PrivateMessageView({ ...message.get({ plain: true }) }),
            );
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMessageWithIdForCmid', error);
    }

    return null;
  }

  static async GetThreadMessages(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const otherCmid = Int32Proxy.Deserialize(bytes);
      const pageNumber = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetThreadMessages', authToken, otherCmid, pageNumber);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const messages = await PrivateMessage.findAll({
            where: {
              [Op.or]: [
                {
                  FromCmid: steamMember.Cmid,
                  ToCmid: otherCmid,
                  IsDeletedBySender: false,
                },
                {
                  FromCmid: otherCmid,
                  ToCmid: steamMember.Cmid,
                  IsDeletedByReceiver: false,
                },
              ],
            },
          });

          ListProxy.Serialize<PrivateMessageView>(
            outputStream,
            messages as PrivateMessageView[],
            PrivateMessageViewProxy.Serialize,
          );
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetThreadMessages', error);
    }

    return null;
  }

  static async MarkThreadAsRead(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const otherCmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('MarkThreadAsRead', authToken, otherCmid);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const messages = await PrivateMessage.findAll({
            where: {
              ToCmid: steamMember.Cmid,
              FromCmid: otherCmid,
              IsRead: false,
            },
          });

          for (const message of messages) {
            await message.update({ IsRead: true });
          }

          BooleanProxy.Serialize(outputStream, true);
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('MarkThreadAsRead', error);
    }

    return null;
  }

  static async SendMessage(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const receiverCmid = Int32Proxy.Deserialize(bytes);
      const content = StringProxy.Deserialize(bytes);

      this.debugEndpoint('SendMessage', authToken, receiverCmid, content);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const sender = await PublicProfile.findOne({ where: { Cmid: steamMember.Cmid } });
          const receiver = await PublicProfile.findOne({ where: { Cmid: receiverCmid } });

          if (sender && receiver) {
            const privateMessage = await PrivateMessage.create({
              PrivateMessageId: Math.randomInt(),
              FromCmid: sender.Cmid,
              FromName: sender.Name,
              ToCmid: receiver.Cmid,
              DateSent: new Date(),
              ContentText: content,
              IsRead: false,
            });

            PrivateMessageViewProxy.Serialize(
              outputStream,
              new PrivateMessageView({ ...privateMessage.get({ plain: true }) }),
            );

            // Realtime: nudge the recipient's client to pull in the new message instantly (like
            // friend requests) instead of waiting for the inbox poll.
            await RealtimeNotify.inboxMessage(receiver.Cmid, privateMessage.PrivateMessageId);
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('SendMessage', error);
    }

    return null;
  }
}
