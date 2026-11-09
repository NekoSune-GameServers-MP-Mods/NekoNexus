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
import { Op } from 'sequelize';
import V2ClanWebService from '../v2/ClanWebService';
import { sessionTokenFor as sharedSessionTokenFor } from './LegacyBridge';
import { ClanView, GroupInvitationView } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  BooleanProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  ClanCreationReturnViewProxy as V2ClanCreationReturnViewProxy,
  ClanRequestAcceptViewProxy as V2ClanRequestAcceptViewProxy,
  ClanRequestDeclineViewProxy as V2ClanRequestDeclineViewProxy,
  GroupCreationViewProxy as V2GroupCreationViewProxy,
  MemberPositionUpdateViewProxy as V2MemberPositionUpdateViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  ClanCreationReturnViewProxy,
  ClanRequestAcceptViewProxy,
  ClanRequestDeclineViewProxy,
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

  // ---------------------------------------------------------------- 1.0.2 -> 2.0 bridge
  // 4.3.10 identifies the player by CMID; the 2.0 handlers take the player's session token.
  // Look up the session the 1.0.2 login created and run the 2.0 logic with it, so both client
  // versions share one implementation (and the same realtime notifications).
  private static async sessionTokenFor(cmid: number): Promise<string> {
    return sharedSessionTokenFor(cmid);
  }

  /** Run a 2.0 ClanWebService handler with plain (unencrypted) input; returns its plain output. */
  private static async callV2(method: string, write: (input: number[]) => void): Promise<number[]> {
    const input: number[] = [];
    write(input);
    const output = await (V2ClanWebService as any)[method](input, []);
    return output ?? [];
  }

  /** Decrypt/encrypt and error handling shared by the bridged 1.0.2 methods. */
  private static async bridge(
    name: string,
    data: number[],
    outputStream: number[],
    body: (bytes: number[], out: number[]) => Promise<void>,
  ): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;
    try {
      await body(bytes, outputStream);
      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError(name, error);
    }
    return null;
  }

  /** Copy a 2.0 int result (ClanActionResultCode / ClanCreationResultCode, same values in 4.3.10). */
  private static passInt(out: number[], v2Output: number[]): void {
    Int32Proxy.Serialize(out, v2Output.length >= 4 ? Int32Proxy.Deserialize([...v2Output]) : 1);
  }

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
    return this.bridge('IsMemberPartOfAnyGroup', data, outputStream, async (bytes, out) => {
      const cmuneId = Int32Proxy.Deserialize(bytes);
      Int32Proxy.Deserialize(bytes); // applicationId
      BooleanProxy.Serialize(out, !!(await ClanMember.findOne({ where: { Cmid: cmuneId } })));
    });
  }

  static async GetClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('GetClan', data, outputStream, async (bytes, out) => {
      const groupId = Int32Proxy.Deserialize(bytes);
      const clan = await Clan.findOne({
        where: { GroupId: groupId },
        include: [{ model: ClanMember, as: 'Members', required: false }],
      });
      if (clan) ClanViewProxy.Serialize(out, clan.get({ plain: true }) as ClanView);
    });
  }

  static async UpdateMemberPosition(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('UpdateMemberPosition', data, outputStream, async (bytes, out) => {
      const view = MemberPositionUpdateViewProxy.Deserialize(bytes);
      view.AuthToken = await this.sessionTokenFor(view.CmidMakingAction);
      this.passInt(out, await this.callV2('UpdateMemberPosition', (i) => V2MemberPositionUpdateViewProxy.Serialize(i, view)));
    });
  }

  static async InviteMemberToJoinAGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('InviteMemberToJoinAGroup', data, outputStream, async (bytes, out) => {
      const clanId = Int32Proxy.Deserialize(bytes);
      const inviterCmid = Int32Proxy.Deserialize(bytes);
      const inviteeCmid = Int32Proxy.Deserialize(bytes);
      const message = StringProxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(inviterCmid);
      this.passInt(out, await this.callV2('InviteMemberToJoinAGroup', (i) => {
        Int32Proxy.Serialize(i, clanId);
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, inviteeCmid);
        StringProxy.Serialize(i, message);
      }));
    });
  }

  static async AcceptClanInvitation(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('AcceptClanInvitation', data, outputStream, async (bytes, out) => {
      const clanInvitationId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const v2 = await this.callV2('AcceptClanInvitation', (i) => {
        Int32Proxy.Serialize(i, clanInvitationId);
        StringProxy.Serialize(i, token);
      });
      // ActionResult 1 (error) when the 2.0 handler produced nothing (bad id / no session).
      const view = v2.length
        ? V2ClanRequestAcceptViewProxy.Deserialize([...v2])
        : { ActionResult: 1, ClanRequestId: clanInvitationId, ClanView: null };
      ClanRequestAcceptViewProxy.Serialize(out, view as any);
    });
  }

  static async DeclineClanInvitation(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('DeclineClanInvitation', data, outputStream, async (bytes, out) => {
      const clanInvitationId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const v2 = await this.callV2('DeclineClanInvitation', (i) => {
        Int32Proxy.Serialize(i, clanInvitationId);
        StringProxy.Serialize(i, token);
      });
      const view = v2.length
        ? V2ClanRequestDeclineViewProxy.Deserialize([...v2])
        : { ActionResult: 1, ClanRequestId: clanInvitationId };
      ClanRequestDeclineViewProxy.Serialize(out, view as any);
    });
  }

  static async KickMemberFromClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('KickMemberFromClan', data, outputStream, async (bytes, out) => {
      const groupId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const cmidToKick = Int32Proxy.Deserialize(bytes);
      this.passInt(out, await this.callV2('KickMemberFromClan', (i) => {
        Int32Proxy.Serialize(i, groupId);
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, cmidToKick);
      }));
    });
  }

  static async DisbandGroup(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('DisbandGroup', data, outputStream, async (bytes, out) => {
      const groupId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      this.passInt(out, await this.callV2('DisbandGroup', (i) => {
        Int32Proxy.Serialize(i, groupId);
        StringProxy.Serialize(i, token);
      }));
    });
  }

  static async LeaveAClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('LeaveAClan', data, outputStream, async (bytes, out) => {
      const groupId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      this.passInt(out, await this.callV2('LeaveAClan', (i) => {
        Int32Proxy.Serialize(i, groupId);
        StringProxy.Serialize(i, token);
      }));
    });
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
    return this.bridge('CancelInvitation', data, outputStream, async (bytes, out) => {
      const groupInvitationId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      this.passInt(out, await this.callV2('CancelInvitation', (i) => {
        Int32Proxy.Serialize(i, groupInvitationId);
        StringProxy.Serialize(i, token);
      }));
    });
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
    return this.bridge('CreateClan', data, outputStream, async (bytes, out) => {
      const view = GroupCreationViewProxy.Deserialize(bytes);
      view.AuthToken = await this.sessionTokenFor(view.Cmid);
      const v2 = await this.callV2('CreateClan', (i) => V2GroupCreationViewProxy.Serialize(i, view));
      const result = v2.length ? V2ClanCreationReturnViewProxy.Deserialize([...v2]) : { ResultCode: 1, ClanView: null };
      ClanCreationReturnViewProxy.Serialize(out, result as any);
    });
  }

  static async TransferOwnership(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('TransferOwnership', data, outputStream, async (bytes, out) => {
      const groupId = Int32Proxy.Deserialize(bytes);
      const token = await this.sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const newLeaderCmid = Int32Proxy.Deserialize(bytes);
      this.passInt(out, await this.callV2('TransferOwnership', (i) => {
        Int32Proxy.Serialize(i, groupId);
        StringProxy.Serialize(i, token);
        Int32Proxy.Serialize(i, newLeaderCmid);
      }));
    });
  }

  static async CanOwnAClan(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('CanOwnAClan', data, outputStream, async (bytes, out) => {
      Int32Proxy.Deserialize(bytes); // cmid
      // The real requirements (level, friends, license) are enforced by CreateClan, which
      // returns the specific ClanCreationResultCode; report "allowed" here so the UI opens.
      Int32Proxy.Serialize(out, 0);
    });
  }

  static async test(data: number[], outputStream: number[]): Promise<number[] | null> {
    return this.bridge('test', data, outputStream, async (_bytes, out) => {
      Int32Proxy.Serialize(out, 0);
    });
  }
}
