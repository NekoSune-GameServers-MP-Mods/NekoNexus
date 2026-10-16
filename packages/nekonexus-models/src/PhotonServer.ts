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

import type { PhotonUsageType, RegionType } from '@festivaldev/uberstrike-js/Cmune/DataCenter/Common/Entities';
import { DataTypes, Model, type Sequelize } from 'sequelize';

interface PhotonServerAttributes {
  PhotonId?: number;
  IP?: string;
  Name?: string;
  Region?: RegionType;
  CountryCode?: string | null;
  Port?: number;
  UsageType?: PhotonUsageType;
  MinLatency?: number;
  LastResponseTime?: Date;
  Guid?: string;
  Enabled?: boolean;
}

export default class PhotonServer extends Model<PhotonServerAttributes> {
  declare PhotonId: number;
  declare IP: string;
  declare Name: string;
  declare Region: RegionType;
  declare CountryCode: string | null;
  declare Port: number;
  declare UsageType: PhotonUsageType;
  declare MinLatency: number;
  declare LastReponseTime: Date;
  declare Guid: string;
  declare Enabled: boolean;

  static initialize(sequelize: Sequelize) {
    PhotonServer.init(
      {
        PhotonId: {
          type: DataTypes.INTEGER,
          primaryKey: true,
        },
        IP: DataTypes.STRING,
        Name: DataTypes.STRING,
        Region: DataTypes.INTEGER,
        CountryCode: DataTypes.STRING(2),
        Port: DataTypes.INTEGER,
        UsageType: DataTypes.INTEGER,
        MinLatency: DataTypes.INTEGER,
        LastResponseTime: DataTypes.DATE,
        Guid: DataTypes.STRING(36),
        Enabled: { type: DataTypes.BOOLEAN, defaultValue: true },
      },
      {
        sequelize,
        tableName: 'PhotonServers',
        timestamps: false,
      },
    );
  }

  static associate(_: any) {}
}
