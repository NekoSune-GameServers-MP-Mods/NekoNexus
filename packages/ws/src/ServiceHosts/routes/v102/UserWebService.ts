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

import { LoadoutFilter } from '@/utils';
import { ApiVersion, UberstrikeInventoryItem } from '@/utils/enums';
import {
  CurrencyDeposit,
  ItemTransaction,
  MemberWallet,
  PlayerInventoryItem,
  PlayerLoadout,
  PlayerStatistics,
  PointDeposit,
  PublicProfile,
  UserAccount,
} from '@festivaldev/nekonexus-models';
import {
  ItemInventoryView,
  MemberOperationResult,
  MemberView,
  type MemberWalletView,
  type PublicProfileView,
} from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  BooleanProxy,
  EnumProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  CurrencyDepositsViewModelProxy,
  ItemInventoryViewProxy,
  ItemTransactionsViewModelProxy,
  LoadoutViewProxy,
  PlayerLevelCapViewProxy,
  PointDepositsViewModelProxy,
  UberstrikeUserViewModelProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import {
  CurrencyDepositsViewModel,
  ItemTransactionsViewModel,
  PointDepositsViewModel,
  UberstrikeUserViewModel,
} from '@festivaldev/uberstrike-js/UberStrike/Core/ViewModel';
import {
  LoadoutView,
  PlayerLevelCapView,
  UberstrikeMemberView,
  type PlayerStatisticsView,
} from '@festivaldev/uberstrike-js/UberStrike/DataCenter/Common/Entities';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';
import V2UserWebService from '../v2/UserWebService';
import { bridge, callV2, sessionTokenFor } from './LegacyBridge';
import { MemberWalletViewProxy as V2MemberWalletViewProxy } from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import { MemberWalletViewProxy as LegacyMemberWalletViewProxy } from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';

export default class UserWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'UserWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IUserWebServiceContract'; }

  static async ChangeMemberName(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Same name rules (profanity, duplicates, ...) as PC; MemberOperationResult is the same enum in both.
    return bridge(this, 'ChangeMemberName', data, outputStream, async (bytes, out) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const name = StringProxy.Deserialize(bytes);
      const locale = StringProxy.Deserialize(bytes);
      const machineId = StringProxy.Deserialize(bytes);
      const v2 = await callV2(V2UserWebService, 'ChangeMemberName', (i) => {
        StringProxy.Serialize(i, token);
        StringProxy.Serialize(i, name);
        StringProxy.Serialize(i, locale);
        StringProxy.Serialize(i, machineId);
      });
      // 6 = MemberOperationResult.InvalidCmid when the 2.0 handler wrote nothing (no session).
      if (v2.length) out.push(...v2);
      else Int32Proxy.Serialize(out, 6);
    });
  }

  static async IsDuplicateMemberName(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const username = StringProxy.Deserialize(bytes);

      this.debugEndpoint('IsDuplicateMemberName', username);

      BooleanProxy.Serialize(outputStream, !!(await PublicProfile.findOne({ where: { Name: username } })));

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('IsDuplicateMemberName', e);
    }

    return null;
  }

  static async GenerateNonDuplicatedMemberNames(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const username = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GenerateNonDuplicatedMemberNames', username);

      const generatedUsernames: any[] = [];

      while (generatedUsernames.length < 3) {
        const number = Math.randomInt(1, 99999);

        const generatedUsername = `${username.substring(0, Math.min(username.length, 18 - String(number).length))}${number}`;

        if (!(await PublicProfile.findOne({ where: { Name: generatedUsername } }))) {
          generatedUsernames.push(generatedUsername);
        }
      }

      ListProxy.Serialize<string>(outputStream, generatedUsernames, StringProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GenerateNonDuplicatedMemberNames', error);
    }

    return null;
  }

  static async GetMemberWallet(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'GetMemberWallet', data, outputStream, async (bytes, out) => {
      const token = await sessionTokenFor(Int32Proxy.Deserialize(bytes));
      const v2 = await callV2(V2UserWebService, 'GetMemberWallet', (i) => StringProxy.Serialize(i, token));
      LegacyMemberWalletViewProxy.Serialize(out, (v2.length ? V2MemberWalletViewProxy.Deserialize([...v2]) : null) as any);
    });
  }

  static async GetInventory(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetInventory', cmid);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const playerInventoryItems = await PlayerInventoryItem.findAll({
          where: {
            Cmid: userAccount.Cmid,
            [Op.or]: [{ ExpirationDate: null }, { ExpirationDate: { [Op.gte]: new Date() } }],
          },
          raw: true,
        });

        ListProxy.Serialize<ItemInventoryView>(
          outputStream,
          playerInventoryItems as ItemInventoryView[],
          ItemInventoryViewProxy.Serialize,
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetInventory', error);
    }

    return null;
  }

  static async GetCurrencyDeposits(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetCurrencyDeposits', cmid, pageIndex, elementPerPage);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const currencyDeposits = await CurrencyDeposit.findAll({
          where: {
            Cmid: userAccount.Cmid,
          },
          order: [['DepositDate', 'DESC']],
          raw: true,
        });

        CurrencyDepositsViewModelProxy.Serialize(
          outputStream,
          new CurrencyDepositsViewModel({
            CurrencyDeposits: currencyDeposits.slice(
              (pageIndex - 1) * elementPerPage,
              (pageIndex - 1) * elementPerPage + elementPerPage,
            ),
            TotalCount: currencyDeposits.length,
          }),
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetCurrencyDeposits', error);
    }

    return null;
  }

  static async GetItemTransactions(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetItemTransactions', cmid, pageIndex, elementPerPage);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const itemTransactions = await ItemTransaction.findAll({
          where: {
            Cmid: userAccount.Cmid,
          },
          order: [['WithdrawalDate', 'DESC']],
          raw: true,
        });

        ItemTransactionsViewModelProxy.Serialize(
          outputStream,
          new ItemTransactionsViewModel({
            ItemTransactions: itemTransactions.slice(
              (pageIndex - 1) * elementPerPage,
              (pageIndex - 1) * elementPerPage + elementPerPage,
            ),
            TotalCount: itemTransactions.length,
          }),
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetItemTransactions', error);
    }

    return null;
  }

  static async GetPointsDeposits(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetPointsDeposits', cmid, pageIndex, elementPerPage);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const pointDeposits = await PointDeposit.findAll({
          where: {
            Cmid: userAccount.Cmid,
          },
          order: [['DepositDate', 'DESC']],
          raw: true,
        });

        PointDepositsViewModelProxy.Serialize(
          outputStream,
          new PointDepositsViewModel({
            PointDeposits: pointDeposits.slice(
              (pageIndex - 1) * elementPerPage,
              (pageIndex - 1) * elementPerPage + elementPerPage,
            ),
            TotalCount: pointDeposits.length,
          }),
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetPointsDeposits', error);
    }

    return null;
  }

  static async SetScore(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Match results are recorded by the realtime server (UpdatePlayerStatistics), not trusted
    // from the client; acknowledge with the empty reply 4.3.10 expects.
    return bridge(this, 'SetScore', data, outputStream, async () => {});
  }

  static async GetMember(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetMember', cmid);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        const publicProfile = await PublicProfile.findOne({ where: { Cmid: userAccount.Cmid } });
        const memberWallet = await MemberWallet.findOne({ where: { Cmid: userAccount.Cmid } });
        const memberItems = (await PlayerInventoryItem.findAll({ where: { Cmid: userAccount.Cmid } })).map(
          (_) => _.ItemId,
        );
        const playerStatistics = await PlayerStatistics.findOne({ where: { Cmid: userAccount.Cmid } });

        if (publicProfile && memberWallet && memberItems && playerStatistics) {
          UberstrikeUserViewModelProxy.Serialize(
            outputStream,
            new UberstrikeUserViewModel({
              CmuneMemberView: new MemberView({
                PublicProfile: publicProfile.get({ plain: true }) as PublicProfileView,
                MemberWallet: memberWallet.get({ plain: true }) as MemberWalletView,
                MemberItems: memberItems,
              }),
              UberstrikeMemberView: new UberstrikeMemberView({
                PlayerStatisticsView: playerStatistics.get({ plain: true }) as PlayerStatisticsView,
              }),
            }),
          );
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMember', error);
    }

    return null;
  }

  static async GetLoadout(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetLoadout', cmid);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        let playerLoadout = await PlayerLoadout.findOne({ where: { Cmid: userAccount.Cmid } });

        if (!playerLoadout) {
          playerLoadout = await PlayerLoadout.create({
            Cmid: userAccount.Cmid,
            Boots: UberstrikeInventoryItem.LutzDefaultGearBoots,
            Gloves: UberstrikeInventoryItem.LutzDefaultGearGloves,
            Head: UberstrikeInventoryItem.LutzDefaultGearHead,
            LowerBody: UberstrikeInventoryItem.LutzDefaultGearLowerBody,
            UpperBody: UberstrikeInventoryItem.LutzDefaultGearUpperBody,
            MeleeWeapon: UberstrikeInventoryItem.TheSplatbat,
            Weapon1: UberstrikeInventoryItem.MachineGun,
            Weapon2: UberstrikeInventoryItem.ShotGun,
            Weapon3: UberstrikeInventoryItem.SniperRifle,
          });
        }

        const playerInventory = await PlayerInventoryItem.findAll({ where: { Cmid: userAccount.Cmid } });
        playerLoadout = LoadoutFilter.Filter<PlayerLoadout>(playerLoadout, playerInventory);

        LoadoutViewProxy.Serialize(outputStream, new LoadoutView({ ...playerLoadout.get({ plain: true }) }));
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetLoadout', error);
    }

    return null;
  }

  static async SetLoadout(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      let loadoutView = LoadoutViewProxy.Deserialize(bytes);

      this.debugEndpoint('SetLoadout', loadoutView);

      const userAccount = await UserAccount.findOne({ where: { Cmid: loadoutView!.Cmid } });

      if (!userAccount) {
        EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.MemberNotFound);
      } else {
        const playerInventory = await PlayerInventoryItem.findAll({ where: { Cmid: userAccount.Cmid } });

        loadoutView = LoadoutFilter.Filter(loadoutView!, playerInventory);

        const playerLoadout = await PlayerLoadout.findOne({ where: { Cmid: userAccount.Cmid } });
        if (!playerLoadout) {
          EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.InvalidData);
        } else {
          await playerLoadout.update({
            UpperBody: loadoutView.UpperBody,
            Weapon1: loadoutView.Weapon1,
            Weapon2: loadoutView.Weapon2,
            Weapon3: loadoutView.Weapon3,
            Type: loadoutView.Type,
            QuickItem3: loadoutView.QuickItem3,
            QuickItem2: loadoutView.QuickItem2,
            QuickItem1: loadoutView.QuickItem1,
            MeleeWeapon: loadoutView.MeleeWeapon,
            LowerBody: loadoutView.LowerBody,
            Head: loadoutView.Head,
            Gloves: loadoutView.Gloves,
            FunctionalItem3: loadoutView.FunctionalItem3,
            FunctionalItem2: loadoutView.FunctionalItem2,
            FunctionalItem1: loadoutView.FunctionalItem1,
            Face: loadoutView.Face,
            Cmid: loadoutView.Cmid,
            Boots: loadoutView.Boots,
            Backpack: loadoutView.Backpack,
            LoadoutId: loadoutView.LoadoutId,
            Webbing: loadoutView.Webbing, // Holo
            SkinColor: loadoutView.SkinColor,
          });

          EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.Ok);
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('SetLoadout', error);
    }

    return null;
  }

  static async GetXPEventsView(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Dictionary<int, PlayerXPEventView> with no entries: XP is awarded server-side.
    return bridge(this, 'GetXPEventsView', data, outputStream, async (_bytes, out) => {
      Int32Proxy.Serialize(out, 0);
    });
  }

  static async GetLevelCapsView(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetLevelCapsView');

      ListProxy.Serialize<PlayerLevelCapView>(
        outputStream,
        [
          new PlayerLevelCapView({ Level: 1, XPRequired: 0 }),
          new PlayerLevelCapView({ Level: 2, XPRequired: 800 }),
          new PlayerLevelCapView({ Level: 3, XPRequired: 2100 }),
          new PlayerLevelCapView({ Level: 4, XPRequired: 3800 }),
          new PlayerLevelCapView({ Level: 5, XPRequired: 6100 }),
          new PlayerLevelCapView({ Level: 6, XPRequired: 9500 }),
          new PlayerLevelCapView({ Level: 7, XPRequired: 12500 }),
          new PlayerLevelCapView({ Level: 8, XPRequired: 16000 }),
          new PlayerLevelCapView({ Level: 9, XPRequired: 19800 }),
          new PlayerLevelCapView({ Level: 10, XPRequired: 24000 }),
        ],
        PlayerLevelCapViewProxy.Serialize,
      );

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetLevelCapsView', e);
    }

    return null;
  }
}
