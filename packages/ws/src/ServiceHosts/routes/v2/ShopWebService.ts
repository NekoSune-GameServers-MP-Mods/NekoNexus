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
import { XpPointsUtil } from '@/utils';
import { ApiVersion } from '@/utils/enums';
import {
  CurrencyDeposit,
  ItemTransaction,
  MemberWallet,
  PlayerInventoryItem,
  PlayerStatistics,
  PublicProfile,
  ShopBundle,
  ShopBundleItem,
  ShopFunctionalItem,
  ShopGearItem,
  ShopItemPrice,
  ShopQuickItem,
  ShopWeaponItem,
} from '@festivaldev/nekonexus-models';
import {
  BundleCategoryType,
  BundleView,
  BuyItemResult,
  BuyingDurationType,
  BuyingLocationType,
  BuyingRecommendationType,
  ChannelType,
  PackType,
  PaymentProviderType,
  UberStrikeCurrencyType,
} from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import {
  UberStrikeItemShopClientView,
  type UberStrikeItemFunctionalView,
  type UberStrikeItemGearView,
  type UberStrikeItemQuickView,
  type UberStrikeItemWeaponView,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Models/Views';
import {
  BooleanProxy,
  BundleViewProxy,
  EnumProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
  UberStrikeItemShopClientViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import { UberstrikeItemType } from '@festivaldev/uberstrike-js/UberStrike/Core/Types';
import crypto from 'crypto';
import moment from 'moment';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';

export default class ShopWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'ShopWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Current;
  }
  // protected static get ServiceInterface(): string { return 'IShopWebServiceContract'; }

  static async BuyBundle(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const bundleId = Int32Proxy.Deserialize(bytes);
      const channel = EnumProxy.Deserialize<ChannelType>(bytes);
      const hashedReceipt = StringProxy.Deserialize(bytes);

      this.debugEndpoint('BuyBundle', authToken, bundleId, channel, hashedReceipt);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('BuyBundle', error);
    }

    return null;
  }

  static async BuyBundleSteam(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const bundleId = Int32Proxy.Deserialize(bytes);
      const steamId = StringProxy.Deserialize(bytes);
      const authToken = StringProxy.Deserialize(bytes);

      this.debugEndpoint('BuyBundleSteam', bundleId, steamId, authToken);

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);
      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (steamMember) {
          const publicProfile = await PublicProfile.findOne({ where: { Cmid: steamMember.Cmid } });

          if (publicProfile) {
            // TODO: Still need to check availability for ChannelType.Steam
            const bundle = await ShopBundle.findOne({
              where: {
                Id: bundleId,
              },
            });

            if (bundle) {
              const memberWallet = await MemberWallet.findOne({ where: { Cmid: steamMember.Cmid } });

              if (memberWallet) {
                const transactionKey = crypto.randomBytes(32).toString('hex');

                await CurrencyDeposit.create({
                  BundleId: bundle.Id,
                  BundleName: bundle.Name,
                  ChannelId: ChannelType.Steam,
                  Cmid: publicProfile.Cmid,
                  Credits: bundle.Credits,
                  CreditsDepositId: Math.randomInt(),
                  CurrencyLabel: 'USD',
                  DepositDate: new Date(),
                  PaymentProviderId: PaymentProviderType.Cmune,
                  Points: bundle.Points,
                  TransactionKey: transactionKey,
                  UsdAmount: bundle.USDPrice,
                });

                const bundleItems = await ShopBundleItem.findAll({
                  where: { BundleId: bundleId },
                });

                if (bundleItems?.length) {
                  for (const bundleItem of bundleItems) {
                    let expirationDate: Date | null = null;

                    switch (bundleItem.Duration) {
                      case BuyingDurationType.OneDay:
                        expirationDate = moment(new Date()).add(1, 'day').toDate();
                        break;
                      case BuyingDurationType.SevenDays:
                        expirationDate = moment(new Date()).add(7, 'days').toDate();
                        break;
                      case BuyingDurationType.ThirtyDays:
                        expirationDate = moment(new Date()).add(30, 'days').toDate();
                        break;
                      case BuyingDurationType.NinetyDays:
                        expirationDate = moment(new Date()).add(90, 'days').toDate();
                        break;
                      default:
                        break;
                    }

                    await PlayerInventoryItem.create({
                      Cmid: publicProfile.Cmid,
                      ItemId: bundleItem.ItemId,
                      ExpirationDate: expirationDate,
                      AmountRemaining: bundleItem.Amount,
                    });

                    await ItemTransaction.create({
                      WithdrawalId: Math.randomInt(),
                      WithdrawalDate: new Date(),
                      Points: 0,
                      Credits: 0,
                      Cmid: publicProfile.Cmid,
                      IsAdminAction: false,
                      ItemId: bundleItem.ItemId,
                      Duration: bundleItem.Duration,
                    });
                  }
                }

                await memberWallet.update({
                  Credits: memberWallet.Credits! + bundle.Credits!,
                  Points: memberWallet.Points! + bundle.Points!,
                });

                BooleanProxy.Serialize(outputStream, true);
              }
            }
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('BuyBundleSteam', error);
    }

    return null;
  }

  static async BuyItem(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const itemId = Int32Proxy.Deserialize(bytes);
      const authToken = StringProxy.Deserialize(bytes);
      const currencyType = EnumProxy.Deserialize<UberStrikeCurrencyType>(bytes);
      const durationType = EnumProxy.Deserialize<BuyingDurationType>(bytes);
      const itemType = EnumProxy.Deserialize<UberstrikeItemType>(bytes);
      const marketLocation = EnumProxy.Deserialize<BuyingLocationType>(bytes);
      const recommendationType = EnumProxy.Deserialize<BuyingRecommendationType>(bytes);

      this.debugEndpoint(
        'BuyItem',
        itemId,
        authToken,
        currencyType,
        durationType,
        itemType,
        marketLocation,
        recommendationType,
      );

      const session = await NekoNexusService.Instance.SessionManager.findSessionForSteamUser(authToken);

      if (session) {
        const steamMember = (await session.SteamMember) ?? { Cmid: session.Cmid as number }; // email/Android accounts have no SteamMember

        if (!steamMember) {
          Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
        } else {
          const publicProfile = await PublicProfile.findOne({ where: { Cmid: steamMember.Cmid } });

          if (!publicProfile) {
            Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
          } else {
            const playerStatistics = await PlayerStatistics.findOne({ where: { Cmid: steamMember.Cmid } });
            const memberWallet = await MemberWallet.findOne({ where: { Cmid: publicProfile.Cmid } });

            if (!memberWallet) {
              Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
            } else {
              if (
                await PlayerInventoryItem.findOne({
                  where: {
                    Cmid: steamMember.Cmid,
                    ItemId: itemId,
                    ExpirationDate: {
                      [Op.or]: [
                        null,
                        {
                          [Op.gt]: new Date(),
                        },
                      ],
                    },
                  },
                })
              ) {
                Int32Proxy.Serialize(outputStream, BuyItemResult.AlreadyInInventory);
              } else {
                let item: any = null;

                switch (itemType) {
                  case UberstrikeItemType.Weapon:
                    item = await ShopWeaponItem.findOne({
                      where: { ID: itemId },
                      include: [
                        {
                          model: ShopItemPrice,
                          as: 'Prices',
                          required: false,
                        },
                      ],
                    });
                    break;
                  case UberstrikeItemType.Gear:
                    item = await ShopGearItem.findOne({
                      where: { ID: itemId },
                      include: [
                        {
                          model: ShopItemPrice,
                          as: 'Prices',
                          required: false,
                        },
                      ],
                    });
                    break;
                  case UberstrikeItemType.QuickUse:
                    item = await ShopQuickItem.findOne({
                      where: { ID: itemId },
                      include: [
                        {
                          model: ShopItemPrice,
                          as: 'Prices',
                          required: false,
                        },
                      ],
                    });
                    break;
                  case UberstrikeItemType.Functional:
                    item = await ShopFunctionalItem.findOne({
                      where: { ID: itemId },
                      include: [
                        {
                          model: ShopItemPrice,
                          as: 'Prices',
                          required: false,
                        },
                      ],
                    });
                    break;
                  default:
                    break;
                }

                if (!item) {
                  Int32Proxy.Serialize(outputStream, BuyItemResult.ItemNotFound);
                } else {
                  if (!item) {
                    Int32Proxy.Serialize(outputStream, BuyItemResult.ItemNotFound);
                  } else if (!item.IsForSale) {
                    Int32Proxy.Serialize(outputStream, BuyItemResult.IsNotForSale);
                  } else if (XpPointsUtil.GetLevelForXp(playerStatistics!.Xp) < item.LevelLock) {
                    Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidLevel);
                  } else {
                    // Track whether payment actually succeeded - the item must NOT be granted
                    // (and OK must NOT be returned) on a failed/insufficient purchase.
                    let purchased = false;

                    if (currencyType === UberStrikeCurrencyType.Credits) {
                      const price = item.Prices.find(
                        (_: ShopItemPrice) => _.Currency === UberStrikeCurrencyType.Credits,
                      );

                      if (!price) {
                        Int32Proxy.Serialize(outputStream, BuyItemResult.IsNotForSale);
                      } else if (memberWallet.Credits! < price.Price) {
                        Int32Proxy.Serialize(outputStream, BuyItemResult.NotEnoughCurrency);
                      } else {
                        await memberWallet.update({
                          Credits: memberWallet.Credits! - price.Price,
                        });

                        await ItemTransaction.create({
                          Cmid: publicProfile.Cmid,
                          Duration: durationType,
                          ItemId: itemId,
                          Credits: price.Price,
                          WithdrawalDate: new Date(),
                          WithdrawalId: Math.randomInt(),
                        });
                        purchased = true;
                      }
                    } else if (currencyType === UberStrikeCurrencyType.Points) {
                      const price = item.Prices.find(
                        (_: ShopItemPrice) => _.Currency === UberStrikeCurrencyType.Points,
                      );

                      if (!price) {
                        Int32Proxy.Serialize(outputStream, BuyItemResult.IsNotForSale);
                      } else if (memberWallet.Points! < price.Price) {
                        Int32Proxy.Serialize(outputStream, BuyItemResult.NotEnoughCurrency);
                      } else {
                        // Deduct POINTS (was wrongly writing to Credits).
                        await memberWallet.update({
                          Points: memberWallet.Points! - price.Price,
                        });

                        await ItemTransaction.create({
                          Cmid: publicProfile.Cmid,
                          Duration: durationType,
                          ItemId: itemId,
                          Points: price.Price,
                          WithdrawalDate: new Date(),
                          WithdrawalId: Math.randomInt(),
                        });
                        purchased = true;
                      }
                    } else {
                      Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidData);
                    }

                    // Only grant the item + return OK when payment actually succeeded.
                    if (purchased) {
                      let expirationDate;

                      switch (durationType) {
                        case BuyingDurationType.OneDay:
                          expirationDate = moment(new Date()).add(1, 'day').toDate();
                          break;
                        case BuyingDurationType.SevenDays:
                          expirationDate = moment(new Date()).add(7, 'days').toDate();
                          break;
                        case BuyingDurationType.ThirtyDays:
                          expirationDate = moment(new Date()).add(30, 'days').toDate();
                          break;
                        case BuyingDurationType.NinetyDays:
                          expirationDate = moment(new Date()).add(90, 'days').toDate();
                          break;
                        default:
                          break;
                      }

                      await PlayerInventoryItem.create({
                        Cmid: publicProfile.Cmid,
                        ItemId: itemId,
                        AmountRemaining: -1,
                        ExpirationDate: expirationDate,
                      });

                      Int32Proxy.Serialize(outputStream, BuyItemResult.OK);
                    }
                  }
                }
              }
            }
          }
        }
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('BuyItem', error);
    }

    return null;
  }

  static async BuyPack(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const itemId = Int32Proxy.Deserialize(bytes);
      const authToken = StringProxy.Deserialize(bytes);
      const packType = EnumProxy.Deserialize<PackType>(bytes);
      const currencyType = EnumProxy.Deserialize<UberStrikeCurrencyType>(bytes);
      const itemType = EnumProxy.Deserialize<UberstrikeItemType>(bytes);
      const marketLocation = EnumProxy.Deserialize<BuyingLocationType>(bytes);
      const recommendationType = EnumProxy.Deserialize<BuyingRecommendationType>(bytes);

      this.debugEndpoint(
        'BuyPack',
        itemId,
        authToken,
        packType,
        currencyType,
        itemType,
        marketLocation,
        recommendationType,
      );

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('BuyPack', error);
    }

    return null;
  }

  static async FinishBuyBundleSteam(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const orderId = StringProxy.Deserialize(bytes);

      this.debugEndpoint('FinishBuyBundleSteam', orderId);

      BooleanProxy.Serialize(outputStream, true);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('FinishBuyBundleSteam', error);
    }

    return null;
  }

  static async GetAllLuckyDraws_1(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetAllLuckyDraws_1');

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetAllLuckyDraws_1', error);
    }

    return null;
  }

  static async GetAllLuckyDraws_2(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const bundleCategoryType = EnumProxy.Deserialize<BundleCategoryType>(bytes);

      this.debugEndpoint('GetAllLuckyDraws_2', bundleCategoryType);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetAllLuckyDraws_2', error);
    }

    return null;
  }

  static async GetAllMysteryBoxs_1(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetAllMysteryBoxs_1');

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetAllMysteryBoxs_1', error);
    }

    return null;
  }

  static async GetAllMysteryBoxs_2(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const bundleCategoryType = EnumProxy.Deserialize<BundleCategoryType>(bytes);

      this.debugEndpoint('GetAllMysteryBoxs_2', bundleCategoryType);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetAllMysteryBoxs_2', error);
    }

    return null;
  }

  static async GetBundles(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const channelType = EnumProxy.Deserialize<ChannelType>(bytes);

      this.debugEndpoint('GetBundles', channelType);

      const bundles = (
        await ShopBundle.findAll({
          include: [
            {
              model: ShopBundleItem,
              as: 'BundleItemViews',
              required: false,
            },
          ],
        })
      ).filter((_) => _.Availability!.includes(channelType));

      ListProxy.Serialize<BundleView>(outputStream, bundles as BundleView[], BundleViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetBundles', error);
    }

    return null;
  }

  static async GetLuckyDraw(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const luckyDrawId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetLuckyDraw', luckyDrawId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetLuckyDraw', error);
    }

    return null;
  }

  static async GetMysteryBox(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const mysteryBoxId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetMysteryBox', mysteryBoxId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('GetMysteryBox', error);
    }

    return null;
  }

  static async GetShop(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetShop');

      UberStrikeItemShopClientViewProxy.Serialize(
        outputStream,
        new UberStrikeItemShopClientView({
          FunctionalItems: (await ShopFunctionalItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown[] as UberStrikeItemFunctionalView[],
          GearItems: (await ShopGearItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown[] as UberStrikeItemGearView[],
          QuickItems: (await ShopQuickItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown[] as UberStrikeItemQuickView[],
          WeaponItems: (await ShopWeaponItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown[] as UberStrikeItemWeaponView[],
        }),
      );

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetShop', error);
    }

    return null;
  }

  static async RollLuckyDraw(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const luckDrawId = Int32Proxy.Deserialize(bytes);
      const channel = EnumProxy.Deserialize<ChannelType>(bytes);

      this.debugEndpoint('RollLuckyDraw', authToken, luckDrawId, channel);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('RollLuckyDraw', error);
    }

    return null;
  }

  static async RollMysteryBox(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const mysteryBoxId = Int32Proxy.Deserialize(bytes);
      const channel = EnumProxy.Deserialize<ChannelType>(bytes);

      this.debugEndpoint('RollMysteryBox', authToken, mysteryBoxId, channel);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('RollMysteryBox', error);
    }

    return null;
  }

  static async UseConsumableItem(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const authToken = StringProxy.Deserialize(bytes);
      const itemId = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('UseConsumableItem', authToken, itemId);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('UseConsumableItem', error);
    }

    return null;
  }

  static async VerifyReceipt(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const hashedReceipt = StringProxy.Deserialize(bytes);

      this.debugEndpoint('VerifyReceipt', hashedReceipt);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (error) {
      this.handleEndpointError('VerifyReceipt', error);
    }

    return null;
  }
}
