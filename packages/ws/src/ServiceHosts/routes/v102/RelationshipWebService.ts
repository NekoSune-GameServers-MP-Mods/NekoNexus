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
import { RealtimeNotify } from '@/utils/RealtimeNotify';
import { ContactRequest, PublicProfile, UserAccount } from '@festivaldev/nekonexus-models';
import {
  ContactGroupView,
  ContactRequestStatus,
  ContactRequestView,
  MemberOperationResult,
  PublicProfileView,
} from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  ContactRequestAcceptView,
  ContactRequestDeclineView,
} from '@festivaldev/uberstrike-js/UberStrike/DataCenter/Common/Entities';
import {
  ContactRequestViewProxy,
  EnumProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  ContactGroupViewProxy,
  ContactRequestAcceptViewProxy,
  ContactRequestDeclineViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';

export default class RelationshipWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'RelationshipWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IRelationshipWebServiceContract'; }

  static async SendContactRequest(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const initiatorCmid = Int32Proxy.Deserialize(bytes);
      const receiverCmid = Int32Proxy.Deserialize(bytes);
      const message = StringProxy.Deserialize(bytes);

      this.debugEndpoint('SendContactRequest', initiatorCmid, receiverCmid, message);

      // Same rules as the 2.0 endpoint. 1.0.2 has no session token: the client sends its CMID.
      const initiatorProfile = await PublicProfile.findOne({ where: { Cmid: initiatorCmid } });
      let requestId = 0;
      if (initiatorProfile && receiverCmid !== initiatorCmid) {
        const existing = await ContactRequest.findOne({
          where: { InitiatorCmid: initiatorCmid, ReceiverCmid: receiverCmid },
        });
        if (!existing) {
          requestId = Math.randomInt();
          await ContactRequest.create({
            RequestId: requestId,
            InitiatorCmid: initiatorCmid,
            InitiatorName: initiatorProfile.Name,
            InitiatorMessage: message,
            ReceiverCmid: receiverCmid,
            Status: ContactRequestStatus.Pending,
            SentDate: new Date(),
          });
        } else {
          requestId = existing.RequestId;
          if (existing.Status !== ContactRequestStatus.Accepted) {
            await existing.update({
              InitiatorName: initiatorProfile.Name,
              InitiatorMessage: message,
              Status: ContactRequestStatus.Pending,
              SentDate: new Date(),
            });
          }
        }
        await RealtimeNotify.inboxRequests(receiverCmid);
      }

      // 4.3.10 RelationshipWebServiceClient.SendContactRequest reads back an int.
      Int32Proxy.Serialize(outputStream, requestId);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('SendContactRequest', e);
    }

    return null;
  }

  static async GetContactRequests(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetContactRequests', cmid);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const contactRequests = await ContactRequest.findAll({
          where: {
            ReceiverCmid: userAccount.Cmid,
            Status: ContactRequestStatus.Pending,
          },
        });

        ListProxy.Serialize<ContactRequestView>(
          outputStream,
          contactRequests as ContactRequestView[],
          ContactRequestViewProxy.Serialize,
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetContactRequests', e);
    }

    return null;
  }

  static async AcceptContactRequest(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const contactRequestId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);
      const applicationId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('AcceptContactRequest', contactRequestId, cmid, applicationId);

      // 4.3.10 expects a ContactRequestAcceptView; ActionResult 0 = success (InboxManager).
      const result = new ContactRequestAcceptView({ ActionResult: 1, Contact: null, RequestId: contactRequestId });
      const contactRequest = await ContactRequest.findOne({
        where: { RequestId: contactRequestId, ReceiverCmid: cmid },
      });
      if (contactRequest) {
        const initiatorProfile = await PublicProfile.findOne({ where: { Cmid: contactRequest.InitiatorCmid } });
        if (initiatorProfile) {
          await contactRequest.update({ Status: ContactRequestStatus.Accepted });
          result.ActionResult = 0;
          result.Contact = new PublicProfileView({ ...(initiatorProfile.get({ plain: true }) as PublicProfileView) });
          // Let the initiator's client pick up the new friend without a relog.
          await RealtimeNotify.inboxRequests(contactRequest.InitiatorCmid);
        }
      }
      ContactRequestAcceptViewProxy.Serialize(outputStream, result);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('AcceptContactRequest', e);
    }

    return null;
  }

  static async DeclineContactRequest(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const contactRequestId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('DeclineContactRequest', contactRequestId, cmid);

      const result = new ContactRequestDeclineView({ ActionResult: 1, RequestId: contactRequestId });
      const contactRequest = await ContactRequest.findOne({
        where: { RequestId: contactRequestId, ReceiverCmid: cmid },
      });
      if (contactRequest) {
        await contactRequest.update({ Status: ContactRequestStatus.Refused });
        result.ActionResult = 0;
      }
      ContactRequestDeclineViewProxy.Serialize(outputStream, result);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('DeclineContactRequest', e);
    }

    return null;
  }

  static async DeleteContact(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const contactCmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('DeleteContact', cmid, contactCmid);

      const removed = await ContactRequest.destroy({
        where: {
          [Op.or]: [
            { ReceiverCmid: cmid, InitiatorCmid: contactCmid },
            { InitiatorCmid: cmid, ReceiverCmid: contactCmid },
          ],
          Status: ContactRequestStatus.Accepted,
        },
      });
      EnumProxy.Serialize<MemberOperationResult>(
        outputStream,
        removed ? MemberOperationResult.Ok : MemberOperationResult.InvalidCmid,
      );

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('DeleteContact', e);
    }

    return null;
  }

  static async MoveContactToGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const contactCmid = Int32Proxy.Deserialize(bytes);
      const previousGroupId = Int32Proxy.Deserialize(bytes);
      const newGroupId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('MoveContactToGroup', cmid, contactCmid, previousGroupId, newGroupId);

      // Contact groups are not stored server-side (GetContactsByGroups returns one group),
      // so there is nothing to move; report success so the client keeps its local grouping.
      EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.Ok);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('MoveContactToGroup', e);
    }

    return null;
  }

  static async GetContactsByGroups(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const applicationId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetContactsByGroups', cmid, applicationId);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const contactRequests = await ContactRequest.findAll({
          where: {
            [Op.or]: {
              ReceiverCmid: userAccount.Cmid,
              InitiatorCmid: userAccount.Cmid,
            },
            Status: ContactRequestStatus.Accepted,
          },
        });

        const contacts: ContactGroupView[] = [];

        for (const contactRequest of contactRequests) {
          contacts.push(
            new ContactGroupView({
              GroupId: userAccount.Cmid,
              Contacts: [
                (await PublicProfile.findOne({
                  where: {
                    Cmid:
                      contactRequest.InitiatorCmid !== userAccount.Cmid
                        ? contactRequest.InitiatorCmid
                        : contactRequest.ReceiverCmid,
                  },
                  raw: true,
                })) as PublicProfileView,
              ],
            }),
          );
        }

        ListProxy.Serialize<ContactGroupView>(outputStream, contacts, ContactGroupViewProxy.Serialize);
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetContactsByGroups', e);
    }

    return null;
  }
}
