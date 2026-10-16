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
import { Clan, ClanMember, GameSession, GroupInvitation, UserAccount } from '@festivaldev/nekonexus-models';
import { ClanView, GroupInvitationView } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  BooleanProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  GroupCreationViewProxy,
  GroupInvitationViewProxy,
  ClanViewProxy,
  MemberPositionUpdateViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import BaseWebService from '../BaseWebService';

export default class ClanWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'ClanWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IClanWebService'; }

  static async IsMemberPartOfGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmuneId = Int32Proxy.Deserialize(bytes);
      const groupId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('IsMemberPartOfGroup', cmuneId, groupId);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmuneId } });

      if (userAccount) {
        const clan = await Clan.findOne({ where: { GroupId: groupId } });

        if (clan) {
          BooleanProxy.Serialize(
            outputStream,
            !!(await ClanMember.findOne({ where: { GroupId: clan.GroupId, Cmid: userAccount.Cmid } })),
          );
        } else {
          BooleanProxy.Serialize(outputStream, false);
        }
      } else {
        BooleanProxy.Serialize(outputStream, false);
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('IsMemberPartOfGroup', e);
    }

    return null;
  }

  static async IsMemberPartOfAnyGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmuneId = Int32Proxy.Deserialize(bytes);
      const applicationId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('IsMemberPartOfAnyGroup', cmuneId, applicationId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('IsMemberPartOfAnyGroup', e);
    }

    return null;
  }

  static async GetClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetClan', groupId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('GetClan', e);
    }

    return null;
  }

  static async UpdateMemberPosition(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const updateMemberPositionData = MemberPositionUpdateViewProxy.Deserialize(bytes);

      this.debugEndpoint('UpdateMemberPosition', updateMemberPositionData);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('UpdateMemberPosition', e);
    }

    return null;
  }

  static async InviteMemberToJoinAGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clanId = Int32Proxy.Deserialize(bytes);
      const inviterCmid = Int32Proxy.Deserialize(bytes);
      const inviteeCmid = Int32Proxy.Deserialize(bytes);
      const message = StringProxy.Deserialize(bytes);

      this.debugEndpoint('InviteMemberToJoinAGroup', clanId, inviterCmid, inviteeCmid, message);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('InviteMemberToJoinAGroup', e);
    }

    return null;
  }

  static async AcceptClanInvitation(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clanInvitationId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('AcceptClanInvitation', clanInvitationId, cmid);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('AcceptClanInvitation', e);
    }

    return null;
  }

  static async DeclineClanInvitation(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clanInvitationId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('DeclineClanInvitation', clanInvitationId, cmid);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('DeclineClanInvitation', e);
    }

    return null;
  }

  static async KickMemberFromClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);
      const cmidTakingAction = Int32Proxy.Deserialize(bytes);
      const cmidToKick = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('KickMemberFromClan', groupId, cmidTakingAction, cmidToKick);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('KickMemberFromClan', e);
    }

    return null;
  }

  static async DisbandGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);
      const cmidTakingAction = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('DisbandGroup', groupId, cmidTakingAction);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('DisbandGroup', e);
    }

    return null;
  }

  static async LeaveAClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('LeaveAClan', groupId, cmid);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('LeaveAClan', e);
    }

    return null;
  }

  static async GetMyClanId(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      // The Unity Android client sends CMID + application ID as two Int32s.
      const cmid = Int32Proxy.Deserialize(bytes);
      const applicationId = Int32Proxy.Deserialize(bytes);
      this.debugEndpoint('GetMyClanId', cmid, applicationId);

      const member = await ClanMember.findOne({ where: { Cmid: cmid } });

      // The Unity callback always deserializes one Int32, even for players
      // without a clan, so return 0 instead of an empty response body.
      Int32Proxy.Serialize(outputStream, member?.GroupId ?? 0);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetMyClanId', e);
    }

    return null;
  }

  static async GetOwnClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const groupId = Int32Proxy.Deserialize(bytes);
      this.debugEndpoint('GetOwnClan', groupId);

      const session = await GameSession.findOne({ where: { SessionId: authToken } });
      const member = session
        ? await ClanMember.findOne({ where: { Cmid: session.Cmid, GroupId: groupId } })
        : null;
      const clan = member
        ? await Clan.findOne({
            where: { GroupId: groupId },
            include: [{ model: ClanMember, as: 'Members', required: false }],
          })
        : null;

      if (clan) {
        ClanViewProxy.Serialize(outputStream, clan.get({ plain: true }) as ClanView);
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetOwnClan', error);
    }

    return null;
  }

  static async CancelInvitation(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupInvitationId = Int32Proxy.Deserialize(bytes);
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('CancelInvitation', groupInvitationId, cmid);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('CancelInvitation', e);
    }

    return null;
  }

  static async GetAllGroupInvitations(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const inviteeCmid = Int32Proxy.Deserialize(bytes);
      const applicationId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetAllGroupInvitations', inviteeCmid, applicationId);

      const userAccount = await UserAccount.findOne({ where: { Cmid: inviteeCmid } });

      if (userAccount) {
        const groupInvitations = await GroupInvitation.findAll({ where: { InviteeCmid: inviteeCmid } });

        ListProxy.Serialize<GroupInvitationView>(
          outputStream,
          groupInvitations as GroupInvitationView[],
          GroupInvitationViewProxy.Serialize,
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllGroupInvitations', e);
    }

    return null;
  }

  static async GetPendingGroupInvitations(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetPendingGroupInvitations', groupId);

      const clan = await Clan.findOne({ where: { GroupId: groupId } });

      if (clan) {
        const groupInvitations = await GroupInvitation.findAll({ where: { GroupId: clan.GroupId } });

        ListProxy.Serialize<GroupInvitationView>(
          outputStream,
          groupInvitations as GroupInvitationView[],
          GroupInvitationViewProxy.Serialize,
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetPendingGroupInvitations', e);
    }

    return null;
  }

  static async CreateClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const createClanData = GroupCreationViewProxy.Deserialize(bytes);

      this.debugEndpoint('CreateClan', createClanData);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('CreateClan', e);
    }

    return null;
  }

  static async TransferOwnership(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const groupId = Int32Proxy.Deserialize(bytes);
      const previousLeaderId = Int32Proxy.Deserialize(bytes);
      const newLeaderId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('TransferOwnership', groupId, previousLeaderId, newLeaderId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('TransferOwnership', e);
    }

    return null;
  }

  static async CanOwnAClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('CanOwnAClan', cmid);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('CanOwnAClan', e);
    }

    return null;
  }

  static async test(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('test');

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('test', e);
    }

    return null;
  }
}
