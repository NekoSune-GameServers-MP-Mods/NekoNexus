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
import { ApplicationConfiguration, Map, MapSettings, PhotonServer } from '@festivaldev/nekonexus-models';
import { ChannelType, PhotonUsageType } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import { Op } from 'sequelize';
import { ApplicationConfigurationView } from '@festivaldev/uberstrike-js/UberStrike/Core/Models/Views';
import {
  ApplicationConfigurationViewProxy,
  AuthenticateApplicationViewProxy,
  EnumProxy,
  ListProxy,
  MapViewProxy,
  MatchStatsProxy,
  StringProxy,
} from '@festivaldev/uberstrike-js/UberStrike/Core/Serialization';
import { AuthenticateApplicationView } from '@festivaldev/uberstrike-js/UberStrike/DataCenter/Common/Entities';
import BaseWebService from '../BaseWebService';
import { withServerRegionFlag } from '../../serverRegion';

export default class ApplicationWebService extends BaseWebService {
  static get ServiceName(): string {
    return 'ApplicationWebService';
  }
  static get ServiceVersion(): string {
    return ApiVersion.Current;
  }
  // protected static get ServiceInterface(): string { return 'IApplicationWebServiceContract'; }

  static supportedClientVersions: string[] = ['4.7.1'];
  static supportedClientChannels: ChannelType[] = [ChannelType.Steam];

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
            // Only advertise servers that admins have left enabled (Enabled !== false).
            GameServers: (await PhotonServer.findAll({
              where: { UsageType: PhotonUsageType.All, Enabled: { [Op.ne]: false } },
              raw: true,
            })).map(withServerRegionFlag),
            CommServer:
              (
                await PhotonServer.findAll({
                  where: { UsageType: PhotonUsageType.CommServer, Enabled: { [Op.ne]: false } },
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

  static async GetConfigurationData(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clientVersion = StringProxy.Deserialize(bytes);

      this.debugEndpoint('GetConfigurationData', clientVersion);

      if (ApplicationWebService.supportedClientVersions.includes(clientVersion)) {
        const applicationConfiguration = await ApplicationConfiguration.findOne();

        ApplicationConfigurationViewProxy.Serialize(
          outputStream,
          new ApplicationConfigurationView({ ...applicationConfiguration!.get({ plain: true }) }),
        );
      }

      return isEncrypted
        ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
        : outputStream;
    } catch (e) {
      this.handleEndpointError('AutGetConfigurationDatahenticateApplication', e);
    }

    return null;
  }

  static async GetMaps(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clientVersion = StringProxy.Deserialize(bytes);
      const clientType = EnumProxy.Deserialize(bytes);

      this.debugEndpoint('GetMaps', clientVersion, clientType);

      if (ApplicationWebService.supportedClientVersions.includes(clientVersion)) {
        const maps = await Map.findAll().then((mapList) =>
          mapList.filter((map) => map.FileName?.['4.7.1'] !== undefined && !map.FileName?.['4.7.1'].length),
        );

        const mapSettings = await MapSettings.findAll({
          raw: true,
        });

        const mapData = maps.reduce((acc: any[], cur: Map) => {
          acc.push({
            ...cur.get({ plain: true }),
            SceneName: cur.SceneName['4.7.1'] || cur.SceneName.default,
            FileName: cur.FileName?.['4.7.1'],
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

  static async SetMatchScore(data: number[], outputStream: number[]): Promise<number[] | null> {
    const isEncrypted = this.isEncrypted(data);
    const bytes = isEncrypted
      ? this.CryptoPolicy.RijndaelDecrypt(data, this.EncryptionPassPhrase, this.EncryptionInitVector)
      : data;

    try {
      const clientVersion = StringProxy.Deserialize(bytes);
      const scoringView = MatchStatsProxy.Deserialize(bytes);
      const serverAuthentication = StringProxy.Deserialize(bytes);

      this.debugEndpoint('SetMatchScore', clientVersion, scoringView, serverAuthentication);

      throw new Error('Not Implemented');
      // return isEncrypted
      //   ? this.CryptoPolicy.RijndaelEncrypt(outputStream, this.EncryptionPassPhrase, this.EncryptionInitVector)
      //   : outputStream;
    } catch (e) {
      this.handleEndpointError('SetMatchScore', e);
    }

    return null;
  }
}
