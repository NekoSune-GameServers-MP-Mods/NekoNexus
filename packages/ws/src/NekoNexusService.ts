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

import * as models from '@festivaldev/nekonexus-models';
import path from 'path';
import readline, { type Interface } from 'readline';
import { Op } from 'sequelize';
import { CommandHandler, Commands, ConsoleHelper } from './console';
import Database from './Database';
import DiscordClient from './discord/DiscordClient';
import { NekoNexusServiceSettings } from './NekoNexusServiceSettings';
import { FileServerHost, WebServiceHost } from './ServiceHosts';
import {
  ServerType,
  WebSocketHost,
  WebSocketPacketType,
  type WebSocketCommand,
  type WebSocketConnectedEventArgs,
  type WebSocketConnectionRejectedEventArgs,
  type WebSocketDataReceivedEventArgs,
  type WebSocketDisconnectedEventArgs,
  type WebSocketPacketReceivedEventArgs,
} from './ServiceHosts/WebSocket';
import { BoostManager, BotsConfigManager, ChatBuffer, GameSessionManager, Log, XpPointsUtil } from './utils';

export default class NekoNexusService {
  private static instance: NekoNexusService;

  static get Instance(): NekoNexusService {
    if (!this.instance) this.instance = new this();

    return this.instance;
  }

  private runApp: boolean = true;

  ServiceSettings: NekoNexusServiceSettings;
  SessionManager: GameSessionManager;

  private fileServer: FileServerHost;
  private webServiceHost: WebServiceHost;
  private discordClient: DiscordClient;
  SocketHost: WebSocketHost;

  private readlineInterface: Interface;

  private intToIPv4(ip: number): string {
    return `${ip >>> 24}.${(ip >> 16) & 255}.${(ip >> 8) & 255}.${ip & 255}`;
  }

  async Run({ serviceHost = true, prompt = true }: { serviceHost: boolean; prompt: boolean }) {
    ConsoleHelper.PrintConsoleHeader();

    Log.info(`Using environment: ${Bun.env.NODE_ENV || 'development'}`);

    this.ServiceSettings = new NekoNexusServiceSettings(path.join(process.cwd(), 'NekoNexus.Settings.WebServices.yml'));
    this.SessionManager = new GameSessionManager();

    // #region Database Configuration
    try {
      await Database.initialize(this.ServiceSettings.DatabaseSettings);
      Log.info('Database opened.');

      const { ActivePlayer, GameRoom, PublicProfile } = models;

      await PublicProfile.destroy({
        where: {
          Name: '',
          Cmid: {
            [Op.gt]: 0,
          },
        },
      });

      await ActivePlayer.destroy({
        truncate: true,
      });

      await GameRoom.destroy({
        truncate: true,
      });

      // Seed the in-memory global boost from its persisted value so a ws restart keeps an active
      // 2x/5x event (re-pushed to Game servers as they connect).
      await BoostManager.initialize();

      // Seed the in-memory AI fill-bots config (BETA, off by default) the same way, so a ws
      // restart doesn't silently re-enable/disable bots against the admin's last choice.
      await BotsConfigManager.initialize();
    } catch (error) {
      Log.fatal('Failed to connect to database. Please check the log for errors and try again.');
      Log.error(error);
      process.exit(1);
    }
    // #endregion

    if (serviceHost) {
      this.webServiceHost = new WebServiceHost(+this.ServiceSettings.WebServicePort!);
      await this.webServiceHost.start();

      this.fileServer = new FileServerHost(+this.ServiceSettings.FileServerPort!);
      await this.fileServer.start();

      if (this.ServiceSettings.DiscordSettings.Enabled) {
        try {
          this.discordClient = new DiscordClient();
          await this.discordClient.Connect();

          // If the connection didn't actually establish (missing/invalid token, etc.),
          // drop the client so the socket handlers skip Discord entirely instead of
          // invoking methods on a dead client.
          if (!this.discordClient.IsConnected) {
            this.discordClient = undefined as any;
          }
        } catch (error) {
          Log.error('Discord integration failed to initialize. Continuing without it.');
          Log.error(error);
          this.discordClient = undefined as any;
        }
      }

      this.SocketHost = new WebSocketHost(+this.ServiceSettings.SocketPort!);
      this.SocketHost.on('ConnectionRejected', (e: WebSocketConnectionRejectedEventArgs) => {
        Log.warn(
          `[Socket] Rejecting ${ServerType[e.Socket.Type]}Server(${e.Socket.Identifier}) from ${e.Socket.RemoteAddress}. Reason: ${e.Reason}`,
        );
      });

      this.SocketHost.on('ClientConnected', (e: WebSocketConnectedEventArgs) => {
        Log.info(
          `[Socket] ${ServerType[e.Socket.Type]}Server(${e.Socket.Identifier}) connected from ${e.Socket.RemoteAddress}.`,
        );

        // A Game server just (re)connected - hand it the current global boost so a server restart
        // never silently drops an active 2x/5x event.
        if (e.Socket.Type === ServerType.Game) {
          BoostManager.pushTo(e.Socket.Identifier);
          BotsConfigManager.pushTo(e.Socket.Identifier);
        }
      });

      this.SocketHost.on('ClientDisconnected', (e: WebSocketDisconnectedEventArgs) => {
        Log.info(
          `[Socket] ${ServerType[e.Socket.Type]}Server(${e.Socket.Identifier}) disconnected. Reason: ${e.Reason ?? 'Connection closed'}`,
        );
      });

      this.SocketHost.on('PacketReceived', async (e: WebSocketPacketReceivedEventArgs) => {
        const { PhotonServer } = models;
        switch (e.PacketType) {
          case WebSocketPacketType.Pong:
            try {
              await PhotonServer.update(
                {
                  LastResponseTime: e.Socket.LastResponseTime,
                },
                {
                  where: {
                    PhotonId: e.Socket.Info.PhotonId,
                  },
                },
              );
            } catch (error) {
              Log.error(
                `Failed to update LastResponseTime for Photon server with id ${e.Socket.Info.PhotonId}: No database entry`,
              );
            }

            break;
          default:
            break;
        }
      });

      this.SocketHost.on('DataReceived', async (e: WebSocketDataReceivedEventArgs) => {
        const { ActivePlayer, GameRoom, PhotonServer } = models;

        // Belt-and-suspenders: a throw in this async handler is an unhandled rejection that crashes
        // the whole web service (which is what an ER_DUP_ENTRY from a misconfigured node was doing).
        // Never let monitoring/discord/db hiccups take the service down.
        try {
        switch (e.Type) {
          case WebSocketPacketType.Monitoring:
            if (e.ServerType === ServerType.Comm) {
              // Monitoring is a FULL snapshot of who's on this comm server. Rebuild the live
              // set: drop players that are no longer here, upsert the current ones. This is
              // published periodically so the lobby/online list stays current in realtime.
              const commId = e.Socket.Info.PhotonId;
              const cmids = e.Data.Peers.map((p: any) => p.Cmid);
              await ActivePlayer.destroy({ where: { CommServerId: commId, Cmid: { [Op.notIn]: cmids.length ? cmids : [-1] } } });
              for (const peer of e.Data.Peers) {
                await ActivePlayer.upsert({
                  Cmid: peer.Cmid,
                  IPAddress: peer.RemoteIP,
                  Channel: peer.Channel,
                  CommServerId: commId,
                });
              }
            } else if (e.ServerType === ServerType.Game) {
              // Full snapshot of this game server's rooms. UPSERT (never raw create) so a duplicate
              // room Number can't crash the service, and clean up stale rooms by the rooms' OWN
              // reported address (the PhotonServer registry IP/Port can mismatch a node misconfigured
              // with the wrong PhotonId, which is exactly what caused the ER_DUP_ENTRY crash). Wrapped
              // so one bad room can never take the web service down.
              try {
                const rooms = (e.Data.Rooms ?? []) as any[];
                const serverIps = [...new Set(rooms.map((r) => this.intToIPv4(r.MetaData.Server.Ipv4)))];
                const currentNumbers = rooms.map((r) => r.MetaData.Number);

                for (const ip of serverIps) {
                  await GameRoom.destroy({
                    where: { ServerIp: ip, Number: { [Op.notIn]: currentNumbers.length ? currentNumbers : [-1] } },
                  });
                }

                for (const room of rooms) {
                  const [channelId, webhookUrl] = (await this.discordClient?.CreateGameRoom(room.MetaData)) || [
                    null,
                    null,
                  ];

                  await GameRoom.upsert({
                    ...room.MetaData,
                    ServerIp: this.intToIPv4(room.MetaData.Server.Ipv4),
                    ServerPort: room.MetaData.Server.Port,
                    ChannelId: channelId,
                    WebhookUrl: webhookUrl,
                  });

                  await ActivePlayer.update(
                    {
                      GameServerId: (
                        await PhotonServer.findOne({
                          where: { IP: room.MetaData.Server.IpAddress, Port: room.MetaData.Server.Port },
                        })
                      )?.PhotonId,
                      GameRoomId: room.RoomId,
                    },
                    {
                      where: {
                        Cmid: room.Peers,
                      },
                    },
                  );
                }
              } catch (error) {
                Log.error('Failed to sync game rooms (continuing).');
                Log.error(error);
              }
            }
            break;
          case WebSocketPacketType.LegacyRoomChat:
            // Android in-match chat (sent by the phone to the comm server) -> the game server that
            // hosts the player's match.
            if (e.ServerType !== ServerType.Comm) break;
            this.SocketHost.SendToGameServers(WebSocketPacketType.LegacyRoomChat, e.Data);
            break;
          case WebSocketPacketType.LegacyChatDeliver:
            // In-match chat for an Android player -> their phone, which listens on the comm server.
            if (e.ServerType !== ServerType.Game) break;
            await this.SocketHost.SendToCommServer(WebSocketPacketType.LegacyChatDeliver, e.Data);
            break;
          case WebSocketPacketType.LegacySession: {
            // A game server asks for the live session of an Android (4.3.10) player, so the player's
            // server-side stand-in in a PC room can load their profile and loadout like a PC player.
            // Only authenticated game-server sockets reach this handler.
            if (e.ServerType !== ServerType.Game) break;
            const cmid = Number((e.Data as any)?.Cmid) || 0;
            const session =
              cmid > 0
                ? await models.GameSession.findOne({
                    where: { Cmid: cmid, ExpireTime: { [Op.gt]: new Date() } },
                    order: [['ExpireTime', 'DESC']],
                  })
                : null;
            session?.extendExpireTime(); // an Android player in a match keeps their session alive
            await e.Socket.Send(
              WebSocketPacketType.LegacySession,
              { SessionId: session?.SessionId ?? '' },
              true,
              e.Payload.ConversationId,
            );
            break;
          }
          case WebSocketPacketType.MatchResult: {
            // A game server reported one player's finished-match result -> save it to match history.
            const d = e.Data as any;
            await models.MatchRecord.create({
              Cmid: Number(d.Cmid),
              MatchGuid: String(d.MatchGuid ?? ''),
              MapId: Number(d.MapId) || 0,
              GameMode: Number(d.GameMode) || 0,
              Kills: Number(d.Kills) || 0,
              Deaths: Number(d.Deaths) || 0,
              Won: !!d.Won,
              Xp: Number(d.Xp) || 0,
              Points: Number(d.Points) || 0,
            });
            break;
          }
          case WebSocketPacketType.Error:
            await this.discordClient?.LogError(e.Data);
            break;
          case WebSocketPacketType.ChatMessage:
            await this.discordClient?.SendLobbyChatMessage(e.Data);
            // Mirror in-game lobby chat into the buffer so the website shows the same chat.
            try {
              ChatBuffer.push(e.Data?.Cmid, e.Data?.Name, e.Data?.Message);
            } catch {
              /* non-fatal */
            }
            break;
          case WebSocketPacketType.ClanChatMessage:
            // In-game clan chat: look the sender's clan up by Cmid and buffer it under that GroupId
            // so the website's clan channel mirrors in-game clan chat.
            try {
              const fromCmid = Number(e.Data?.Cmid) || 0;
              // .unscoped(): the ClanMember model's defaultScope excludes GroupId (hidden from the
              // client view), so a plain findByPk returns GroupId=undefined and the message would
              // never get buffered under a clan. Unscoped so we get the real GroupId.
              const member: any = fromCmid
                ? await models.ClanMember.unscoped().findByPk(fromCmid, { raw: true }).catch(() => null)
                : null;
              if (member?.GroupId) {
                ChatBuffer.pushClan(Number(member.GroupId), fromCmid, e.Data?.Name, e.Data?.Message);
              }
            } catch {
              /* non-fatal */
            }
            break;
          case WebSocketPacketType.RoomChatMessage: {
            const [message, roomInfo] = e.Data;

            this.discordClient?.SendGameRoomMessage(message, roomInfo);

            break;
          }
          case WebSocketPacketType.Command: {
            const cmd = e.Data as WebSocketCommand;

            switch (cmd.Command) {
              case 'link': {
                if (await this.discordClient?.IsMemberLinked(cmd.Invoker.Cmid)) {
                  e.Socket.Send(
                    WebSocketPacketType.CommandOutput,
                    'Your profile has already been linked to Discord.',
                    true,
                    e.Payload.ConversationId,
                  );
                  return;
                }

                const nonce = await this.discordClient?.BeginLinkMember(cmd.Invoker.Cmid);
                e.Socket.Send(
                  WebSocketPacketType.CommandOutput,
                  `Your Discord link code is: ${nonce}.\nPlease send a DM to the NekoNexus Discord bot containing this code to complete the process.`,
                  true,
                  e.Payload.ConversationId,
                );
                break;
              }
              default:
                break;
            }

            break;
          }
          case WebSocketPacketType.PlayerJoined:
            await this.discordClient?.SendPlayerJoinMessage(e.Data);

            await ActivePlayer.upsert({
              Cmid: e.Data.Cmid,
              IPAddress: e.Data.RemoteIP,
              Channel: e.Data.Channel,
              CommServerId:
                e.ServerType === ServerType.Comm
                  ? (await PhotonServer.findOne({ where: { IP: e.Data.LocalIP, Port: e.Data.LocalPort } }))?.PhotonId
                  : undefined,
              GameServerId:
                e.ServerType === ServerType.Game
                  ? (await PhotonServer.findOne({ where: { IP: e.Data.LocalIP, Port: e.Data.LocalPort } }))?.PhotonId
                  : undefined,
            });
            break;
          case WebSocketPacketType.PlayerLeft:
            await this.discordClient?.SendPlayerLeftMessage(e.Data);

            if (e.ServerType === ServerType.Comm) {
              await ActivePlayer.destroy({ where: { Cmid: e.Data.Cmid } });
            } else if (e.ServerType === ServerType.Game) {
              await ActivePlayer.update({ GameServerId: null }, { where: { Cmid: e.Data.Cmid } });
            }

            break;
          case WebSocketPacketType.RoomOpened: {
            await this.discordClient?.SendGameRoomCreatedMessage(e.Data);
            const [channelId, webhookUrl] = (await this.discordClient?.CreateGameRoom(e.Data)) || [null, null];

            await GameRoom.create({
              ...e.Data,
              ServerIp: this.intToIPv4(e.Data.Server.Ipv4),
              ServerPort: e.Data.Server.Port,
              ChannelId: channelId,
              WebhookUrl: webhookUrl,
            });
            break;
          }
          case WebSocketPacketType.RoomClosed:
            await this.discordClient?.SendGameRoomDestroyedMessage(e.Data);
            await this.discordClient?.DestroyGameRoom(e.Data);

            await GameRoom.destroy({ where: { Number: e.Data.Number } });
            break;
          case WebSocketPacketType.PlayerJoinedRoom: {
            const [playerInfo, roomInfo] = e.Data;

            await this.discordClient?.GrantRoomPermissions(playerInfo, roomInfo);
            break;
          }
          case WebSocketPacketType.PlayerLeftRoom: {
            const [playerInfo, roomInfo] = e.Data;

            await this.discordClient?.RevokeRoomPermissions(playerInfo, roomInfo);
            break;
          }
          case WebSocketPacketType.RoundStarted:
            await this.discordClient?.SendRoundStartedMessage(e.Data);
            break;
          case WebSocketPacketType.RoundEnded: {
            const [roomInfo, matchEndData] = e.Data;

            await this.discordClient?.SendRoundEndedMessage(roomInfo, matchEndData);
            break;
          }
          default:
            break;
        }
        } catch (error) {
          Log.error('Error handling realtime socket message (continuing).');
          Log.error(error);
        }
      });
    }

    CommandHandler.Commands.push(...Commands);
    XpPointsUtil._initialize();

    if (prompt) {
      ConsoleHelper.PrintConsoleHeaderSubtitle();
      this.readlineInterface = readline.createInterface({
        input: process.stdin as any,
        output: process.stdout as any,
      });

      this.readlineInterface.on('SIGINT', () => {
        this.readlineInterface.close();

        process.stdout.write('\n');
        this.Teardown();
      });

      this.Prompt();
    }
  }

  async Teardown(): Promise<void> {
    this.runApp = false;

    await this.fileServer?.stop();
    await this.webServiceHost?.stop();
    await this.discordClient?.Disconnect();

    console.log('Bye.');
    setTimeout(() => {
      process.exit(0);
    }, 500);
  }

  private Prompt(): void {
    this.readlineInterface.question('> ', async (cmd) => {
      const cmdArgs =
        cmd.match(/[a-zA-Z0-9-]+|"(?:\\"|[^"])+"/g)?.map((_) => (_.match(/".+"/g) ? _.slice(1, -1) : _)) ?? [];

      if (cmdArgs.length) {
        await CommandHandler.HandleCommand(
          cmdArgs[0].toLocaleLowerCase(),
          cmdArgs.slice(1),
          undefined,
          (output: string, inline: boolean) => {
            if (!inline) {
              console.log(output);
            } else {
              process.stdout.write(output);
            }
          },
          (invoker: any, success: boolean, error?: string | undefined | null) => {
            if (success && !error?.trim().length) {
              // console.log(invoker.Output);
            } else {
              console.error(error);
            }
          },
        );
      }

      if (this.runApp) this.Prompt();
    });
  }
}
