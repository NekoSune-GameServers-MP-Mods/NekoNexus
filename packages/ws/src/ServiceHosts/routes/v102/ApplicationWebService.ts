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
import { withServerRegionFlag } from '../../serverRegion';
import { Map, MapSettings, PhotonServer, UserAccount } from '@festivaldev/nekonexus-models';
import { BuildType, ChannelType, PhotonUsageType } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import { Op } from 'sequelize';
import {
  BooleanProxy,
  EnumProxy,
  Int32Proxy,
  ListProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import {
  ApplicationViewProxy,
  AuthenticateApplicationViewProxy,
  BugViewProxy,
  MapViewProxy,
  PhotonViewProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization/Legacy';
import { DefinitionType, LocaleType, TutorialStepType } from '@festivaldev/uberstrike-js/UberStrike/Core/Types';
import { AuthenticateApplicationView } from '@festivaldev/uberstrike-js/UberStrike/DataCenter/Common/Entities';
import BaseWebService from '../BaseWebService';

export default class ApplicationWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'ApplicationWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Legacy102;
  }
  // protected static get ServiceInterface(): string { return 'IApplicationWebServiceContract';

  static supportedClientVersions: string[] = ['4.3.10'];
  static supportedClientChannels: ChannelType[] = [
    ChannelType.WindowsStandalone,
    ChannelType.OSXStandalone,
    ChannelType.Android,
  ];

  static async GetPhotonServers(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const applicationView = ApplicationViewProxy.Deserialize(bytes);

      this.debugEndpoint('GetPhotonServers', applicationView);

      const servers = await PhotonServer.findAll({
        where: {
          UsageType: PhotonUsageType.All,
          Enabled: { [Op.ne]: false },
        },
        raw: true,
      });

      ListProxy.Serialize(outputStream, servers, PhotonViewProxy.Serialize);
      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetPhotonServers', e);
    }

    return null;
  }

  static async GetMyIP(_data: number[], outputStream: number[], remoteAddress = ''): Promise<number[] | null> {
    try {
      // Node reports IPv4 peers as IPv4-mapped IPv6 addresses on dual-stack listeners.
      const clientAddress = remoteAddress.replace(/^::ffff:/i, '');
      this.debugEndpoint('GetMyIP', clientAddress);
      StringProxy.Serialize(outputStream, clientAddress);
      return outputStream;
    } catch (e) {
      this.handleEndpointError('GetMyIP', e);
    }

    return null;
  }

  static async AuthenticateApplication(data: number[], outputStream: number[]): Promise<number[] | null> {
    const bytes = data;

    try {
      const clientVersion = StringProxy.Deserialize(bytes);
      const channelType = EnumProxy.Deserialize<ChannelType>(bytes);
      const publicKey = StringProxy.Deserialize(bytes);

      this.debugEndpoint('AuthenticateApplication', clientVersion, channelType, publicKey);

      if (!ApplicationWebService.supportedClientChannels.includes(channelType)) {
        AuthenticateApplicationViewProxy.Serialize(
          outputStream,
          new AuthenticateApplicationView({
            IsEnabled: false,
          }),
        );
      } else {
        AuthenticateApplicationViewProxy.Serialize(
          outputStream,
          new AuthenticateApplicationView({
            IsEnabled: true,
            GameServers: (await PhotonServer.findAll({ where: { UsageType: PhotonUsageType.All }, raw: true })).map(withServerRegionFlag),
            CommServer:
              (
                await PhotonServer.findAll({
                  where: { UsageType: PhotonUsageType.CommServer },
                  order: [['MinLatency', 'ASC']],
                  raw: true,
                })
              ).map(withServerRegionFlag)[0] ?? null,
            WarnPlayer: !ApplicationWebService.supportedClientVersions.includes(clientVersion),
            EncryptionInitVector: this.EncryptionInitVector,
            EncryptionPassPhrase: this.EncryptionPassPhrase,
          }),
        );
      }

      return outputStream;
    } catch (e) {
      this.handleEndpointError('AuthenticateApplication', e);
    }

    return null;
  }

  static async RecordException(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const buildType = EnumProxy.Deserialize<BuildType>(bytes);
      const channelType = EnumProxy.Deserialize<ChannelType>(bytes);
      const buildNumber = StringProxy.Deserialize(bytes);
      const logString = StringProxy.Deserialize(bytes);
      const stackTrace = StringProxy.Deserialize(bytes);
      const exceptionData = StringProxy.Deserialize(bytes);

      this.debugEndpoint(
        'RecordException',
        cmid,
        buildType,
        channelType,
        buildNumber,
        logString,
        stackTrace,
        exceptionData,
      );

      // throw new Error('Not Implemented');
      return outputStream;
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('RecordException', e);
    }

    return null;
  }

  static async RecordExceptionUnencrypted(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const buildType = EnumProxy.Deserialize<BuildType>(bytes);
      const channelType = EnumProxy.Deserialize<ChannelType>(bytes);
      const buildNumber = StringProxy.Deserialize(bytes);
      const errorType = StringProxy.Deserialize(bytes);
      const errorMessage = StringProxy.Deserialize(bytes);

      this.debugEndpoint('RecordExceptionUnencrypted', buildType, channelType, buildNumber, errorType, errorMessage);

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('RecordExceptionUnencrypted', e);
    }

    return null;
  }

  static async RecordTutorialStep(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const cmid = Int32Proxy.Deserialize(bytes);
      const step = EnumProxy.Deserialize<TutorialStepType>(bytes);

      this.debugEndpoint('RecordTutorialStep', cmid, step);

      const userAccount = await UserAccount.findOne({ where: { Cmid: cmid } });

      if (userAccount) {
        await userAccount.update({
          TutorialStep: step,
        });
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('RecordTutorialStep', e);
    }

    return null;
  }

  static async ReportBug(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const bugView = BugViewProxy.Deserialize(bytes);

      this.debugEndpoint('ReportBug', bugView);

      BooleanProxy.Serialize(outputStream, true);
      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('ReportBug', e);
    }

    return null;
  }

  static async GetLiveFeed(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('GetLiveFeed');

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('GetLiveFeed', e);
    }

    return null;
  }

  static async GetMaps(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const appVersion = StringProxy.Deserialize(bytes);
      const locale = EnumProxy.Deserialize<LocaleType>(bytes);
      const definition = EnumProxy.Deserialize<DefinitionType>(bytes);

      this.debugEndpoint('GetMaps', appVersion, locale, definition);

      if (ApplicationWebService.supportedClientVersions.includes(appVersion)) {
        const maps = await Map.findAll().then((mapList) =>
          mapList.filter(
            (map) =>
              map.FileName?.['4.3.10'] !== undefined &&
              // Only advertise maps whose scene ships with the standard client. The "extra" maps
              // (Space City / Spaceport Alpha / UberZone) carry a real 4.7.1 filename rather than
              // an empty one; their .unity3d isn't in the build, so loading them throws
              // "Couldn't open file ...". Matches the v2 GetMaps exclusion.
              !map.FileName?.['4.7.1']?.length,
          ),
        );

        const mapSettings = await MapSettings.findAll({
          raw: true,
        });

        const mapData = maps.reduce((acc: any[], cur: Map) => {
          acc.push({
            ...cur.get({ plain: true }),
            SceneName: cur.SceneName['4.3.10'] || cur.SceneName.default,
            FileName: cur.FileName?.['4.3.10'],
            Settings: mapSettings
              .filter((_) => _.MapId === cur.MapId)
              .reduce((acc: { [key: number]: any }, cur) => {
                acc[cur.GameModeType!] = {
                  ...cur,
                  MapId: undefined,
                  GameModeType: undefined,
                };

                return acc;
              }, {}),
          });

          return acc;
        }, []);

        ListProxy.Serialize(outputStream, mapData, MapViewProxy.Serialize);
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetMaps', e);
    }

    return null;
  }

  static async GetItemAssetBundles(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const appVersion = StringProxy.Deserialize(bytes);
      const definition = EnumProxy.Deserialize<DefinitionType>(bytes);

      this.debugEndpoint('GetItemAssetBundles', appVersion, definition);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('GetItemAssetBundles', e);
    }

    return null;
  }

  static async SetLevelVersion(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const id = Int32Proxy.Deserialize(bytes);
      const version = Int32Proxy.Deserialize(bytes);
      const md5Hash = StringProxy.Deserialize(bytes);

      this.debugEndpoint('SetLevelVersion', id, version, md5Hash);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('SetLevelVersion', e);
    }

    return null;
  }

  static async GetPhotonServerName(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const applicationVersion = StringProxy.Deserialize(bytes);
      const ipAddress = StringProxy.Deserialize(bytes);
      const port = Int32Proxy.Deserialize(bytes);

      this.debugEndpoint('GetPhotonServerName', applicationVersion, ipAddress, port);

      const server = await PhotonServer.findOne({
        where: { IP: ipAddress, Port: port, Enabled: { [Op.ne]: false } },
        raw: true,
      });
      StringProxy.Serialize(outputStream, server?.Name ?? `${ipAddress}:${port}`);
      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('GetPhotonServerName', e);
    }

    return null;
  }

  static async IsAlive(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      this.debugEndpoint('IsAlive');

      StringProxy.Serialize(outputStream, 'OK');
      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('IsAlive', e);
    }

    return null;
  }
}
