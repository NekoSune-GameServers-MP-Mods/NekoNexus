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

import { XpPointsUtil } from '@/utils';
import { ApiVersion } from '@/utils/enums';
import {
  ItemTransaction,
  Map,
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
  UserAccount,
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
  EnumProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  BundleViewProxy,
  LuckyDrawUnityViewProxy,
  MysteryBoxUnityViewProxy,
  UberStrikeItemShopClientViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import { UberstrikeItemType } from '@festivaldev/uberstrike-js/UberStrike/Core/Types';
import moment from 'moment';
import { Op } from 'sequelize';
import BaseWebService from '../BaseWebService';
import { bridge, callV2, sessionTokenFor } from './LegacyBridge';

export default class ShopWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'ShopWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IShopWebServiceContract'; }

  static async GetShop(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const applicationVersion = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetShop', applicationVersion);

      UberStrikeItemShopClientViewProxy.Serialize(
        outputStream,
        new UberStrikeItemShopClientView({
          FunctionalItems: (await ShopFunctionalItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown as UberStrikeItemFunctionalView[],
          GearItems: (await ShopGearItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown as UberStrikeItemGearView[],
          QuickItems: (await ShopQuickItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown as UberStrikeItemQuickView[],
          WeaponItems: (await ShopWeaponItem.findAll({
            include: [{ model: ShopItemPrice, as: 'Prices', required: false }],
          })) as unknown as UberStrikeItemWeaponView[],
          ItemsRecommendationPerMap: await Map.findAll().then((maps) =>
            maps.reduce((acc: any, cur: Map) => {
              acc[cur.MapId] = cur.RecommendedItemId;
              return acc;
            }, {}),
          ),
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

  static async BuyItem(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const itemId = Int32Proxy.Deserialize(bytes);
      const buyerCmid = Int32Proxy.Deserialize(bytes);
      const currencyType = EnumProxy.Deserialize<UberStrikeCurrencyType>(bytes);
      const durationType = EnumProxy.Deserialize<BuyingDurationType>(bytes);
      const itemType = EnumProxy.Deserialize<UberstrikeItemType>(bytes);
      const marketLocation = EnumProxy.Deserialize<BuyingLocationType>(bytes);
      const recommendationType = EnumProxy.Deserialize<BuyingRecommendationType>(bytes);

      this.debugEndpoint(
        'BuyItem',
        itemId,
        buyerCmid,
        currencyType,
        durationType,
        itemType,
        marketLocation,
        recommendationType,
      );

      const userAccount = await UserAccount.findOne({ where: { Cmid: buyerCmid } });

      if (!userAccount) {
        Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
      } else {
        const publicProfile = await PublicProfile.findOne({ where: { Cmid: userAccount.Cmid } });

        if (!publicProfile) {
          Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
        } else {
          const playerStatistics = await PlayerStatistics.findOne({ where: { Cmid: userAccount.Cmid } });
          const memberWallet = await MemberWallet.findOne({ where: { Cmid: publicProfile.Cmid } });

          if (!memberWallet) {
            Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidMember);
          } else {
            if (
              await PlayerInventoryItem.findOne({
                where: {
                  Cmid: userAccount.Cmid,
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
                  // Set when a check below fails: the item must then NOT be granted (previously it
                  // was granted anyway - free items - and a second result code was written).
                  let failed = false;
                  if (currencyType === UberStrikeCurrencyType.Credits) {
                    const price = item.Prices.find((_: ShopItemPrice) => _.Currency === UberStrikeCurrencyType.Credits);

                    if (!price) {
                      Int32Proxy.Serialize(outputStream, BuyItemResult.IsNotForSale); failed = true;
                    } else if (memberWallet.Credits! < price.Price) {
                      Int32Proxy.Serialize(outputStream, BuyItemResult.NotEnoughCurrency); failed = true;
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
                    }
                  } else if (currencyType === UberStrikeCurrencyType.Points) {
                    const price = item.Prices.find((_: ShopItemPrice) => _.Currency === UberStrikeCurrencyType.Points);

                    if (!price) {
                      Int32Proxy.Serialize(outputStream, BuyItemResult.IsNotForSale); failed = true;
                    } else if (memberWallet.Points! < price.Price) {
                      Int32Proxy.Serialize(outputStream, BuyItemResult.NotEnoughCurrency); failed = true;
                    } else {
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
                    }
                  } else if (
                    currencyType === UberStrikeCurrencyType.None ||
                    UberStrikeCurrencyType[currencyType] === undefined
                  ) {
                    Int32Proxy.Serialize(outputStream, BuyItemResult.InvalidData); failed = true;
                  }

                  if (failed) {
                    return isEncrypted
                      ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
                      : outputStream;
                  }

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

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('BuyItem', e);
    }

    return null;
  }

  static async BuyPack(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Packs are quick/consumable items bought N uses at a time: the price row for the requested
    // PackType holds the price and the number of uses (Amount), which is added to the item.
    return bridge(this, 'BuyPack', data, outputStream, async (bytes, out) => {
      const itemId = Int32Proxy.Deserialize(bytes);
      const buyerCmid = Int32Proxy.Deserialize(bytes);
      const packType = EnumProxy.Deserialize<PackType>(bytes);
      const currencyType = EnumProxy.Deserialize<UberStrikeCurrencyType>(bytes);

      const wallet = await MemberWallet.findOne({ where: { Cmid: buyerCmid } });
      const stats = await PlayerStatistics.findOne({ where: { Cmid: buyerCmid } });
      if (!wallet || !stats) return Int32Proxy.Serialize(out, BuyItemResult.InvalidMember);

      const include = [{ model: ShopItemPrice, as: 'Prices', required: false }];
      const item: any =
        (await ShopQuickItem.findOne({ where: { ID: itemId }, include })) ??
        (await ShopFunctionalItem.findOne({ where: { ID: itemId }, include }));
      if (!item) return Int32Proxy.Serialize(out, BuyItemResult.ItemNotFound);
      if (!item.IsForSale) return Int32Proxy.Serialize(out, BuyItemResult.IsNotForSale);
      if (XpPointsUtil.GetLevelForXp(stats.Xp) < item.LevelLock) return Int32Proxy.Serialize(out, BuyItemResult.InvalidLevel);

      const price = (item.Prices ?? []).find(
        (p: ShopItemPrice) => p.Currency === currencyType && (p.PackType ?? PackType.One) === packType,
      );
      if (!price) return Int32Proxy.Serialize(out, BuyItemResult.IsNotForSale);

      if (currencyType === UberStrikeCurrencyType.Credits) {
        if (wallet.Credits! < price.Price!) return Int32Proxy.Serialize(out, BuyItemResult.NotEnoughCurrency);
        await wallet.update({ Credits: wallet.Credits! - price.Price! });
      } else if (currencyType === UberStrikeCurrencyType.Points) {
        if (wallet.Points! < price.Price!) return Int32Proxy.Serialize(out, BuyItemResult.NotEnoughCurrency);
        await wallet.update({ Points: wallet.Points! - price.Price! });
      } else {
        return Int32Proxy.Serialize(out, BuyItemResult.InvalidData);
      }

      await ItemTransaction.create({
        Cmid: buyerCmid,
        ItemId: itemId,
        Duration: BuyingDurationType.Permanent,
        ...(currencyType === UberStrikeCurrencyType.Credits ? { Credits: price.Price } : { Points: price.Price }),
        WithdrawalDate: new Date(),
        WithdrawalId: Math.randomInt(),
      });

      const uses = Math.max(1, price.Amount ?? 1);
      const owned = await PlayerInventoryItem.findOne({ where: { Cmid: buyerCmid, ItemId: itemId } });
      if (owned) {
        // -1 means unlimited; keep it unlimited, otherwise add the pack's uses.
        if (owned.AmountRemaining !== -1) await owned.update({ AmountRemaining: (owned.AmountRemaining ?? 0) + uses });
      } else {
        await PlayerInventoryItem.create({ Cmid: buyerCmid, ItemId: itemId, AmountRemaining: uses, ExpirationDate: null });
      }
      Int32Proxy.Serialize(out, BuyItemResult.OK);
    });
  }

  static async GetBundles(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const channel = EnumProxy.Deserialize<ChannelType>(bytes);

      this.debugEndpoint('GetBundles', channel);

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
      ).filter((_) => _.Availability!.includes(channel));

      ListProxy.Serialize<BundleView>(outputStream, bundles as BundleView[], BundleViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (error) {
      this.handleEndpointError('GetBundles', error);
    }

    return null;
  }

  static async BuyBundle(data: number[], outputStream: number[]): Promise<number[] | null> {
    // Real-money bundles bought through the Android store: the receipt cannot be verified by
    // this server (no Google Play billing integration), so the purchase is refused. Credits can
    // be bought through the website store (NekoPay) and are shared with this account.
    return bridge(this, 'BuyBundle', data, outputStream, async (_bytes, out) => {
      BooleanProxy.Serialize(out, false);
    });
  }

  static async UseConsumableItem(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'UseConsumableItem', data, outputStream, async (bytes, out) => {
      const cmid = Int32Proxy.Deserialize(bytes);
      const itemId = Int32Proxy.Deserialize(bytes);
      const owned = await PlayerInventoryItem.findOne({ where: { Cmid: cmid, ItemId: itemId } });
      let ok = false;
      if (owned && owned.AmountRemaining === -1) {
        ok = true; // unlimited
      } else if (owned && (owned.AmountRemaining ?? 0) > 0) {
        await owned.update({ AmountRemaining: owned.AmountRemaining! - 1 });
        ok = true;
      }
      BooleanProxy.Serialize(out, ok);
    });
  }

  static async GetAllMysteryBoxs_1(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetAllMysteryBoxs_1');

      ListProxy.Serialize(outputStream, [], MysteryBoxUnityViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllMysteryBoxs_1', e);
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

      ListProxy.Serialize(outputStream, [], MysteryBoxUnityViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllMysteryBoxs_2', e);
    }

    return null;
  }

  static async GetMysteryBox(data: number[], outputStream: number[]): Promise<number[] | null> {
    // No mystery boxes are configured (GetAllMysteryBoxs returns none); answer with null.
    return bridge(this, 'GetMysteryBox', data, outputStream, async (_bytes, out) => {
      MysteryBoxUnityViewProxy.Serialize(out, null as any);
    });
  }

  static async RollMysteryBox(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'RollMysteryBox', data, outputStream, async (_bytes, out) => {
      Int32Proxy.Serialize(out, 0); // empty List<MysteryBoxWonItemUnityView>
    });
  }

  static async GetAllLuckyDraws_1(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetAllLuckyDraws_1');

      ListProxy.Serialize(outputStream, [], LuckyDrawUnityViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllLuckyDraws_1', e);
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

      ListProxy.Serialize(outputStream, [], LuckyDrawUnityViewProxy.Serialize);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetAllLuckyDraws_2', e);
    }

    return null;
  }

  static async GetLuckyDraw(data: number[], outputStream: number[]): Promise<number[] | null> {
    // No lucky draws are configured (GetAllLuckyDraws returns none); answer with null.
    return bridge(this, 'GetLuckyDraw', data, outputStream, async (_bytes, out) => {
      LuckyDrawUnityViewProxy.Serialize(out, null as any);
    });
  }

  static async RollLuckyDraw(data: number[], outputStream: number[]): Promise<number[] | null> {
    return bridge(this, 'RollLuckyDraw', data, outputStream, async (_bytes, out) => {
      Int32Proxy.Serialize(out, 0); // no item won
    });
  }
}
