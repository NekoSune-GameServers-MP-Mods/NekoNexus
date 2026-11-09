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
import { ProfanityFilter } from '@/ProfanityFilter';
import { LoadoutFilter } from '@/utils';
import { RealtimeNotify } from '@/utils/RealtimeNotify';
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
} from '@festivaldev/nekonexus-models';
import {
  ItemInventoryView,
  MemberAccessLevel,
  MemberOperationResult,
  MemberView,
  MemberWalletView,
  type PublicProfileView,
} from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  BooleanProxy,
  CurrencyDepositViewProxy,
  CurrencyDepositsViewModelProxy,
  EnumProxy,
  Int32Proxy,
  ItemInventoryViewProxy,
  ItemTransactionsViewModelProxy,
  ListProxy,
  LoadoutViewProxy,
  MemberWalletViewProxy,
  PlayerStatisticsViewProxy,
  PointDepositViewProxy,
  PointDepositsViewModelProxy,
  StringProxy,
  UberstrikeUserViewModelProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  CurrencyDepositsViewModel,
  ItemTransactionsViewModel,
  PointDepositsViewModel,
  UberstrikeUserViewModel,
} from '@festivaldev/uberstrike-js/UberStrike/Core/ViewModel';
import {
  LoadoutView,
  UberstrikeMemberView,
  type PlayerStatisticsView,
} from '@festivaldev/uberstrike-js/UberStrike/DataCenter/Common/Entities';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';

export default class UserWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'UserWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Current;
  }
  // protected static get ServiceInterface(): string { return 'IUserWebServiceContract'; }

  private static readonly ProfanityFilter: ProfanityFilter = new ProfanityFilter();

  static async ChangeMemberName(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const name = StringProxy.Deserialize(bytes);
      const locale = StringProxy.Deserialize(bytes);
      const machineId = StringProxy.Deserialize(bytes);

      this.debugEndpoint('ChangeMemberName', authToken, name, locale, machineId);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const publicProfile = await PublicProfile.findOne({ where: { Cmid: steamMember.Cmid } });
          const existingName = await PublicProfile.findOne({ where: { Name: name } });
          const nameChangeItem = await PlayerInventoryItem.findOne({
            where: { Cmid: steamMember.Cmid, ItemId: UberstrikeInventoryItem.NameChange },
          });

          if (!nameChangeItem && publicProfile!.AccessLevel !== MemberAccessLevel.Admin) {
            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.NameChangeNotInInventory);
          } else if (existingName != null && existingName.Cmid !== publicProfile!.Cmid) {
            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.DuplicateName);
          } else if (name.length < 3 || !name.match(/^[a-zA-Z0-9_]+$/)) {
            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.InvalidName);
          } else if (this.ProfanityFilter.DetectAllProfanities(name).length > 0) {
            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.OffensiveName);
          } else {
            await publicProfile!.update({
              Name: name,
            });

            await PlayerInventoryItem.destroy({
              where: {
                Cmid: publicProfile!.Cmid,
                ItemId: UberstrikeInventoryItem.NameChange,
              },
            });

            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.Ok);
          }
        } else {
          EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.MemberNotFound);
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('ChangeMemberName', error);
    }

    return null;
  }

  static async DepositCredits(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const depositTransaction = CurrencyDepositViewProxy.Deserialize(bytes);
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('DepositCredits', depositTransaction, authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const memberWallet = await MemberWallet.findOne({ where: { Cmid: steamMember.Cmid } });

          if (memberWallet) {
            if (depositTransaction.Credits > 0) {
              await CurrencyDeposit.create({ ...depositTransaction });

              await memberWallet.update({
                Credits: memberWallet.Credits + depositTransaction.Credits,
              });

              // Refresh the player's in-game credits live if they're in the lobby (best-effort).
              void RealtimeNotify.wallet(steamMember.Cmid, memberWallet.Credits, memberWallet.Points);

              BooleanProxy.Serialize(outputStream, true);
            } else {
              BooleanProxy.Serialize(outputStream, false);
            }
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('DepositCredits', error);
    }

    return null;
  }

  static async DepositPoints(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const depositTransaction = PointDepositViewProxy.Deserialize(bytes);
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('DepositPoints', depositTransaction, authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const memberWallet = await MemberWallet.findOne({ where: { Cmid: steamMember.Cmid } });

          if (memberWallet) {
            if (depositTransaction.Points > 0) {
              await PointDeposit.create({ ...depositTransaction });

              await memberWallet.update({
                Points: memberWallet.Points + depositTransaction.Points,
              });

              // Refresh the player's in-game coins live if they're in the lobby (best-effort).
              void RealtimeNotify.wallet(steamMember.Cmid, memberWallet.Credits, memberWallet.Points);

              BooleanProxy.Serialize(outputStream, true);
            } else {
              BooleanProxy.Serialize(outputStream, false);
            }
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('DepositPoints', error);
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

  static async GetCurrencyDeposits(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetCurrencyDeposits', authToken, pageIndex, elementPerPage);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const currencyDeposits = await CurrencyDeposit.findAll({
            where: {
              Cmid: steamMember.Cmid,
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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetCurrencyDeposits', error);
    }

    return null;
  }

  static async GetInventory(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetInventory', authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const playerInventoryItems = await PlayerInventoryItem.findAll({
            where: {
              Cmid: steamMember.Cmid,
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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetInventory', error);
    }

    return null;
  }

  static async GetItemTransactions(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetItemTransactions', authToken, pageIndex, elementPerPage);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const itemTransactions = await ItemTransaction.findAll({
            where: {
              Cmid: steamMember.Cmid,
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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetItemTransactions', error);
    }

    return null;
  }

  static async GetLoadout(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetLoadout', authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);

      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          let playerLoadout = await PlayerLoadout.findOne({ where: { Cmid: steamMember.Cmid } });

          if (!playerLoadout) {
            playerLoadout = await PlayerLoadout.create({
              Cmid: steamMember.Cmid,
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

          const playerInventory = await PlayerInventoryItem.findAll({ where: { Cmid: steamMember.Cmid } });
          playerLoadout = LoadoutFilter.Filter<PlayerLoadout>(playerLoadout, playerInventory);

          LoadoutViewProxy.Serialize(outputStream, new LoadoutView({ ...playerLoadout.get({ plain: true }) }));
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetLoadout', error);
    }

    return null;
  }

  static async GetMember(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetMember', authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const publicProfile = await PublicProfile.findOne({ where: { Cmid: steamMember.Cmid } });
          const memberWallet = await MemberWallet.findOne({ where: { Cmid: steamMember.Cmid } });
          const memberItems = (await PlayerInventoryItem.findAll({ where: { Cmid: steamMember.Cmid } })).map(
            (_) => _.ItemId,
          );
          const playerStatistics = await PlayerStatistics.findOne({ where: { Cmid: steamMember.Cmid } });

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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMember', error);
    }

    return null;
  }

  static async GetMemberListSessionData(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authTokens = ListProxy.Deserialize<string>(bytes, StringProxy.Deserialize);

      this.debugEndpoint('GetMemberListSessionData', authTokens);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMemberListSessionData', error);
    }

    return null;
  }

  static async GetMemberSessionData(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetMemberSessionData', authToken);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMemberSessionData', error);
    }

    return null;
  }

  static async GetMemberWallet(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetMemberWallet', authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const memberWallet = await MemberWallet.findOne({ where: { Cmid: steamMember.Cmid } });

          if (memberWallet) {
            MemberWalletViewProxy.Serialize(
              outputStream,
              new MemberWalletView({
                ...memberWallet.get({ plain: true }),
                Credits: Math.max(memberWallet.Credits!, 0),
                Points: Math.max(memberWallet.Points!, 0),
              }),
            );
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMemberWallet', error);
    }

    return null;
  }

  static async GetPointsDeposits(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const pageIndex = Int32Proxy.Deserialize(bytes);
      const elementPerPage = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetPointsDeposits', authToken, pageIndex, elementPerPage);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const pointDeposits = await PointDeposit.findAll({
            where: {
              Cmid: steamMember.Cmid,
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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetPointsDeposits', error);
    }

    return null;
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
    } catch (error) {
      this.handleEndpointError('IsDuplicateMemberName', error);
    }

    return null;
  }

  static async SetLoadout(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      let loadoutView = LoadoutViewProxy.Deserialize(bytes);

      this.debugEndpoint('SetLoadout', authToken, loadoutView);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (!steamMember) {
          EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.MemberNotFound);
        } else {
          const playerInventory = await PlayerInventoryItem.findAll({ where: { Cmid: steamMember.Cmid } });

          loadoutView = LoadoutFilter.Filter(loadoutView, playerInventory);

          const playerLoadout = await PlayerLoadout.findOne({ where: { Cmid: steamMember.Cmid } });
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
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('SetLoadout', error);
    }

    return null;
  }

  static async UpdatePlayerStatistics(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const playerStatistics = PlayerStatisticsViewProxy.Deserialize(bytes);

      this.debugEndpoint('UpdatePlayerStatistics', authToken, playerStatistics);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (!steamMember) {
          EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.MemberNotFound);
        } else {
          const statistics = await PlayerStatistics.findOne({ where: { Cmid: steamMember.Cmid } });

          if (!statistics) {
            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.InvalidData);
          } else {
            await statistics.update({
              Hits: playerStatistics.Hits,
              Shots: playerStatistics.Shots,
              Splats: playerStatistics.Splats,
              Splatted: playerStatistics.Splatted,
              Headshots: playerStatistics.Headshots,
              Nutshots: playerStatistics.Nutshots,
              Xp: playerStatistics.Xp,
              TimeSpentInGame: playerStatistics.TimeSpentInGame,
              Level: playerStatistics.Level,

              WeaponStatistics: {
                // Machine Gun
                HandgunTotalDamageDone: playerStatistics.WeaponStatistics.HandgunTotalDamageDone,
                HandgunTotalSplats: playerStatistics.WeaponStatistics.HandgunTotalSplats,
                HandgunTotalShotsFired: playerStatistics.WeaponStatistics.HandgunTotalShotsFired,
                HandgunTotalShotsHit: playerStatistics.WeaponStatistics.HandgunTotalShotsHit,

                // Machine Gun
                MachineGunTotalDamageDone: playerStatistics.WeaponStatistics.MachineGunTotalDamageDone,
                MachineGunTotalSplats: playerStatistics.WeaponStatistics.MachineGunTotalSplats,
                MachineGunTotalShotsFired: playerStatistics.WeaponStatistics.MachineGunTotalShotsFired,
                MachineGunTotalShotsHit: playerStatistics.WeaponStatistics.MachineGunTotalShotsHit,

                // Shotgun
                ShotgunTotalDamageDone: playerStatistics.WeaponStatistics.ShotgunTotalDamageDone,
                ShotgunTotalSplats: playerStatistics.WeaponStatistics.ShotgunTotalSplats,
                ShotgunTotalShotsFired: playerStatistics.WeaponStatistics.ShotgunTotalShotsFired,
                ShotgunTotalShotsHit: playerStatistics.WeaponStatistics.ShotgunTotalShotsHit,

                // Splattergun
                SplattergunTotalDamageDone: playerStatistics.WeaponStatistics.SplattergunTotalDamageDone,
                SplattergunTotalSplats: playerStatistics.WeaponStatistics.SplattergunTotalSplats,
                SplattergunTotalShotsFired: playerStatistics.WeaponStatistics.SplattergunTotalShotsFired,
                SplattergunTotalShotsHit: playerStatistics.WeaponStatistics.SplattergunTotalShotsHit,

                // Sniper Rifle
                SniperTotalDamageDone: playerStatistics.WeaponStatistics.SniperTotalDamageDone,
                SniperTotalSplats: playerStatistics.WeaponStatistics.SniperTotalSplats,
                SniperTotalShotsFired: playerStatistics.WeaponStatistics.SniperTotalShotsFired,
                SniperTotalShotsHit: playerStatistics.WeaponStatistics.SniperTotalShotsHit,

                // Melee Weapons
                MeleeTotalDamageDone: playerStatistics.WeaponStatistics.MeleeTotalDamageDone,
                MeleeTotalSplats: playerStatistics.WeaponStatistics.MeleeTotalSplats,
                MeleeTotalShotsFired: playerStatistics.WeaponStatistics.MeleeTotalShotsFired,
                MeleeTotalShotsHit: playerStatistics.WeaponStatistics.MeleeTotalShotsHit,

                // Cannon
                CannonTotalDamageDone: playerStatistics.WeaponStatistics.CannonTotalDamageDone,
                CannonTotalSplats: playerStatistics.WeaponStatistics.CannonTotalSplats,
                CannonTotalShotsFired: playerStatistics.WeaponStatistics.CannonTotalShotsFired,
                CannonTotalShotsHit: playerStatistics.WeaponStatistics.CannonTotalShotsHit,

                // Launcher
                LauncherTotalDamageDone: playerStatistics.WeaponStatistics.LauncherTotalDamageDone,
                LauncherTotalSplats: playerStatistics.WeaponStatistics.LauncherTotalSplats,
                LauncherTotalShotsFired: playerStatistics.WeaponStatistics.LauncherTotalShotsFired,
                LauncherTotalShotsHit: playerStatistics.WeaponStatistics.LauncherTotalShotsHit,
              },
              PersonalRecord: {
                MostArmorPickedUp: playerStatistics.PersonalRecord.MostArmorPickedUp,
                MostCannonSplats: playerStatistics.PersonalRecord.MostCannonSplats,
                MostConsecutiveSnipes: playerStatistics.PersonalRecord.MostConsecutiveSnipes,
                MostDamageDealt: playerStatistics.PersonalRecord.MostDamageDealt,
                MostDamageReceived: playerStatistics.PersonalRecord.MostDamageReceived,
                MostHeadshots: playerStatistics.PersonalRecord.MostHeadshots,
                MostHealthPickedUp: playerStatistics.PersonalRecord.MostHealthPickedUp,
                MostLauncherSplats: playerStatistics.PersonalRecord.MostLauncherSplats,
                MostMachinegunSplats: playerStatistics.PersonalRecord.MostMachinegunSplats,
                MostHandgunSplats: playerStatistics.PersonalRecord.MostHandgunSplats,
                MostMeleeSplats: playerStatistics.PersonalRecord.MostMeleeSplats,
                MostNutshots: playerStatistics.PersonalRecord.MostNutshots,
                MostShotgunSplats: playerStatistics.PersonalRecord.MostShotgunSplats,
                MostSniperSplats: playerStatistics.PersonalRecord.MostSniperSplats,
                MostSplats: playerStatistics.PersonalRecord.MostSplats,
                MostSplattergunSplats: playerStatistics.PersonalRecord.MostSplattergunSplats,
                MostXPEarned: playerStatistics.PersonalRecord.MostXPEarned,
              },
            });

            EnumProxy.Serialize<MemberOperationResult>(outputStream, MemberOperationResult.Ok);
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('UpdatePlayerStatistics', error);
    }

    return null;
  }
}
