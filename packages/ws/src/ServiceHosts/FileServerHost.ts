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
import { Log } from '@/utils';
import express, { type Express } from 'express';
import * as http from 'http';
import { type AddressInfo } from 'net';
import path from 'path';

export default class FileServerHost {
  readonly port: number;

  readonly expressApp: Express;
  private listener?: http.Server;

  constructor(port: number = 8081) {
    this.port = port;

    this.expressApp = express();
    this.expressApp.disable('x-powered-by');
    this.expressApp.disable('etag');
    this.expressApp.set('json spaces', 2);

    // if (process.env.NODE_ENV !== 'production') {
    //   this.expressApp.use(morgan(`[${chalk.blue('INFO')}] [FileServerHost] [:date[iso]] :remote-addr ":method :url HTTP/:http-version" :status (:req[Content-Length]/:res[content-length] bytes)`));
    // }

    this.expressApp.use((req, res, next) => {
      res.set('X-Powered-By', 'HartwörkingMexikanerTM/1.0');

      return next();
    });

    // Relay /updates to GitHub Pages (the channel CI publishes), so the in-game auto-updater can
    // keep pointing at THIS domain while Pages stays the single source of truth - no need to host
    // the update payload on this box. Set UPDATE_RELAY_URL to override the Pages base, or to '' to
    // disable the relay and serve updates from local wwwroot/updates instead. If the relay is
    // unreachable or the file isn't on Pages, it falls through to any local copy (next()).
    const relayBase = (process.env.UPDATE_RELAY_URL ?? 'https://nekosune-gameservers-mp-mods.github.io/NekoNexus').replace(/\/+$/, '');
    if (relayBase) {
      this.expressApp.use('/updates', async (req, res, next) => {
        if (req.method !== 'GET' && req.method !== 'HEAD') return next();

        const target = `${relayBase}${req.url}`; // req.url is the path after /updates, e.g. /v2/stable/updates.yml
        try {
          const upstream = await fetch(target, { redirect: 'follow' });
          if (!upstream.ok) return next(); // not on Pages -> try local

          res.status(upstream.status);
          const ct = upstream.headers.get('content-type');
          if (ct) res.set('content-type', ct);
          if (req.method === 'HEAD') return res.end();

          const buf = Buffer.from(await upstream.arrayBuffer());
          res.set('content-length', String(buf.length));
          return res.send(buf);
        } catch (e) {
          Log.warn(`[updates] relay to ${target} failed; falling back to local. ${e}`);
          return next(); // relay down -> serve local copy if present
        }
      });
    }

    this.expressApp.use('/', express.static(path.join(process.cwd(), 'wwwroot')));
    this.expressApp.use(
      '/UberStrike/Images/MapIcons/',
      express.static(path.join(process.cwd(), 'wwwroot/images/maps')),
    );
    this.expressApp.use(
      '/UberStrike/Images/MapIcons/TheBunker.jpg',
      express.static(path.join(process.cwd(), 'wwwroot/images/maps/TheHangar.jpg')),
    );
  }

  async start(): Promise<void> {
    Log.info('Starting HTTP server...');

    return new Promise((resolve, reject) => {
      this.listener = this.expressApp.listen(
        this.port,
        NekoNexusService.Instance.ServiceSettings.Hostname ?? '0.0.0.0',
        () => {
          const address: AddressInfo = this.listener?.address() as AddressInfo;
          Log.info(`HTTP server listening on ${address.address}:${address.port}.`);

          resolve();
        },
      );
    });
  }

  stop(): void {
    this.listener?.close();
    this.listener = undefined;
  }
}
