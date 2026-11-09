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

import { Log } from '@/utils';
import { ApiVersion } from '@/utils/enums';
import fs from 'fs';
import path from 'path';
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

  /** Mobile devices cannot connect to a loopback/bind-all/blank server address. */
  static isConnectableAndroidServer(server: { IP?: string | null; Port?: number | null }): boolean {
    const host = String(server?.IP ?? '').trim().toLowerCase();
    const port = Number(server?.Port);
    return host.length > 0 &&
      !['127.0.0.1', 'localhost', '::1', '0.0.0.0', '::', '[::1]'].includes(host) &&
      Number.isInteger(port) && port >= 1 && port <= 65535;
  }

  static supportedClientVersions: string[] = ['4.3.10'];
  static supportedClientChannels: ChannelType[] = [
    ChannelType.WindowsStandalone,
    ChannelType.OSXStandalone,
    ChannelType.Android,
  ];

  /** Folder served at <ContentBaseUrl>Maps/4.3.10/ for the Android client, or null if not configured. */
  static androidMapsDir(): string | null {
    const root = process.env.NEKONEXUS_ANDROID_CONTENT_DIR?.trim();
    return root ? path.join(path.resolve(root), 'UberStrike/Maps/4.3.10') : null;
  }

  /** Android map bundles currently hosted (re-read on every GetMaps, so dropping files in needs no restart). */
  static listAndroidMapFiles(): string[] {
    const dir = ApplicationWebService.androidMapsDir();
    if (!dir || !fs.existsSync(dir)) return [];
    return fs.readdirSync(dir).filter((file) => file.toLowerCase().endsWith('.unity3d'));
  }

  /**
   * Picks the hosted Android bundle for a map. Matches the map's base name, taken from its
   * 4.3.10 filename ("AqualabResearchHub-<hash>-HD.unity3d" -> "AqualabResearchHub") or its scene
   * name, against "<Base>.unity3d" or "<Base>-<anything>.unity3d" (case-insensitive). If several
   * match, a name containing "android" wins, then any name that isn't the PC "-HD" build.
   */
  static findAndroidMapFile(map: Map, files: string[]): string | null {
    const bases = new Set<string>();
    const pcFile = map.FileName?.['4.3.10'];
    if (pcFile) bases.add(pcFile.split('-')[0].replace(/\.unity3d$/i, '').toLowerCase());
    for (const scene of [map.SceneName?.['4.3.10'], map.SceneName?.default]) {
      if (scene) bases.add(scene.toLowerCase());
    }

    const candidates = files.filter((file) => {
      const name = file.toLowerCase();
      return [...bases].some((base) => name === `${base}.unity3d` || name.startsWith(`${base}-`));
    });
    if (!candidates.length) return null;

    return (
      candidates.find((file) => file.toLowerCase().includes('android')) ??
      candidates.find((file) => !file.toLowerCase().endsWith('-hd.unity3d')) ??
      candidates[0]
    );
  }

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

      // The original Android client may use GetPhotonServers as well as
      // AuthenticateApplication. Never send it 127.0.0.1/blank endpoints,
      // which point back to the handset and produce empty-connection errors.
      const visibleServers = applicationView?.Channel === ChannelType.Android
        ? servers.filter(ApplicationWebService.isConnectableAndroidServer)
        : servers;
      ListProxy.Serialize(outputStream, visibleServers, PhotonViewProxy.Serialize);
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
            GameServers: (await PhotonServer.findAll({ where: { UsageType: PhotonUsageType.All }, raw: true }))
              .filter((server) => channelType !== ChannelType.Android || ApplicationWebService.isConnectableAndroidServer(server))
              .map(withServerRegionFlag),
            CommServer:
              (
                await PhotonServer.findAll({
                  where: { UsageType: PhotonUsageType.CommServer, Enabled: { [Op.ne]: false } },
                  order: [['MinLatency', 'ASC']],
                  raw: true,
                })
              ).filter((server) => channelType !== ChannelType.Android || ApplicationWebService.isConnectableAndroidServer(server))
               .map(withServerRegionFlag)[0] ?? null,
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
        const isAndroid = definition === DefinitionType.Android;
        const androidMapFiles = isAndroid ? ApplicationWebService.listAndroidMapFiles() : [];

        const maps = await Map.findAll().then((mapList) =>
          mapList.filter((map) => {
            if (map.FileName?.['4.3.10'] === undefined) return false;
            // Android: advertise a map only when an Android-built bundle for it is hosted,
            // otherwise the client lists it and then fails to download it.
            if (isAndroid) return ApplicationWebService.findAndroidMapFile(map, androidMapFiles) !== null;
            // Only advertise maps whose scene ships with the standard client. The "extra" maps
            // (Space City / Spaceport Alpha / UberZone) carry a real 4.7.1 filename rather than
            // an empty one; their .unity3d isn't in the build, so loading them throws
            // "Couldn't open file ...". Matches the v2 GetMaps exclusion.
            return !map.FileName?.['4.7.1']?.length;
          }),
        );

        if (isAndroid) {
          Log.info(
            `[GetMaps] Android: ${maps.length} map(s) with hosted bundles (${androidMapFiles.length} file(s) in ${ApplicationWebService.androidMapsDir() ?? '<NEKONEXUS_ANDROID_CONTENT_DIR not set>'})`,
          );
        }

        const mapSettings = await MapSettings.findAll({
          raw: true,
        });

        const mapData = maps.reduce((acc: any[], cur: Map) => {
          acc.push({
            ...cur.get({ plain: true }),
            SceneName: cur.SceneName['4.3.10'] || cur.SceneName.default,
            FileName: isAndroid
              ? ApplicationWebService.findAndroidMapFile(cur, androidMapFiles)
              : cur.FileName?.['4.3.10'],
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
