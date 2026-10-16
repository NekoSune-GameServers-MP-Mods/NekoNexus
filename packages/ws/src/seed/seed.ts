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

// eslint-disable-next-line import/no-named-default
import { default as ServiceSettings } from '@/NekoNexusServiceSettings';
import { Log } from '@/utils';
import models, {
  type ApplicationConfiguration,
  type Map,
  type PhotonServer,
  type PublicProfile,
  type ShopFunctionalItem,
  type ShopGearItem,
  type ShopQuickItem,
  type ShopWeaponItem,
} from '@festivaldev/nekonexus-models';
import mysql from 'mysql2';
import { type Dialect, Sequelize } from 'sequelize';

import applicationConfiguration from './applicationConfiguration.json';
import maps from './maps.json';
import photonServers from './photonServers.json';
import shop from './shop.json';
import users from './users.json';

export default async function runSeed() {
  const sequelize = new Sequelize(
    ServiceSettings.DatabaseSettings.DatabaseName!,
    ServiceSettings.DatabaseSettings.Username!,
    ServiceSettings.DatabaseSettings.Password,
    {
      host: ServiceSettings.DatabaseSettings.Server,
      port: Number(ServiceSettings.DatabaseSettings.Port),
      dialect: ServiceSettings.DatabaseSettings.Type as Dialect,
      dialectModule: mysql, // bundle mysql2 into the compiled exe (no dynamic require)
      logging: false,
      // Single connection so SET FOREIGN_KEY_CHECKS below applies to every query.
      pool: { max: 1, min: 0 },
    },
  );

  Log.info('Connecting to database...');
  Log.debug(
    `Type:${ServiceSettings.DatabaseSettings.Type} Database:${ServiceSettings.DatabaseSettings.DatabaseName} Auth:'${ServiceSettings.DatabaseSettings.Username}'@'${ServiceSettings.DatabaseSettings.Server}:${ServiceSettings.DatabaseSettings.Port}' (using password: ${ServiceSettings.DatabaseSettings.Password?.length! > 0 ? 'YES' : 'NO'})`,
  );
  for (const [modelName, model] of Object.entries(models)) {
    model.initialize(sequelize);
  }

  for (const [modelName, model] of Object.entries(models)) {
    model.associate?.(models);
  }

  // Ensure the schema WITHOUT dropping data: `alter` adds/updates columns in place, so existing
  // players, wallets, stats, friends and any admin customizations are preserved across re-runs.
  // FK checks are toggled around the alter because some column changes touch referenced keys.
  await sequelize.query('SET FOREIGN_KEY_CHECKS = 0');
  try {
    await sequelize.sync({ alter: true });
    Log.info('Database schema ensured (alter - no data dropped).');
  } catch (e) {
    Log.error('Failed to ensure database schema (check the DB user has ALTER rights).');
    Log.error(String(e));
    process.exit(1);
  }

  // Seed default/reference data ONLY into tables that are currently empty, so re-running the
  // seed on a live server never wipes those tables. The explicit retired-item cleanup below is
  // the only shop-catalog removal performed by this command.
  async function seedIfEmpty<T>(model: any, rows: T[], label: string): Promise<void> {
    const existing = await model.count();
    if (existing > 0) {
      Log.info(`Skipping ${label}: ${existing} row(s) already present.`);
      return;
    }
    if (!rows.length) return;
    await model.bulkCreate(rows as any[]);
    Log.info(`Seeded ${rows.length} ${label}.`);
  }

  await seedIfEmpty(
    models.ApplicationConfiguration,
    applicationConfiguration as Partial<ApplicationConfiguration>[],
    'application configuration',
  );
  await seedIfEmpty(models.PublicProfile, users as Partial<PublicProfile>[], 'default users');

  // Photon servers: the IP is what the GAME CLIENT connects to, so it must be reachable from the
  // player's machine - override with PARADISE_PUBLIC_HOST (your public IP/domain). Only seeded
  // when empty; change/add servers later in the admin dashboard (Servers page).
  const publicHost = process.env.PARADISE_PUBLIC_HOST?.trim();
  const photonRows = (photonServers as Partial<PhotonServer>[]).map((s) =>
    publicHost ? { ...s, IP: publicHost } : s,
  );
  if (publicHost) Log.info(`Realtime server list will use public host: ${publicHost}`);
  await seedIfEmpty(models.PhotonServer, photonRows, 'realtime servers');

  // Shop items + prices.
  await seedIfEmpty(models.ShopFunctionalItem, shop.FunctionalItems as Partial<ShopFunctionalItem>[], 'functional items');
  await seedIfEmpty(models.ShopGearItem, shop.GearItems as Partial<ShopGearItem>[], 'gear items');
  await seedIfEmpty(models.ShopQuickItem, shop.QuickItems as any[] as Partial<ShopQuickItem>[], 'quick items');
  await seedIfEmpty(models.ShopWeaponItem, shop.WeaponItems as Partial<ShopWeaponItem>[], 'weapon items');

  const priceRows = [shop.FunctionalItems, shop.GearItems, shop.QuickItems, shop.WeaponItems].reduce(
    (acc: any[], group: any[]) => {
      for (const item of group) {
        if (item.Prices && item.Prices.length) {
          acc.push(...item.Prices.map((price: any) => ({ ...price, ID: item.ID })));
        }
      }
      return acc;
    },
    [],
  );
  await seedIfEmpty(models.ShopItemPrice, priceRows, 'shop prices');

  // Backfill the shop economy onto older databases. Early seeds priced every item at 0 (the whole
  // shop was free); the seed data now carries real prices. This sets those prices ONLY on rows that
  // are still 0, so it brings an existing DB up to date without clobbering any prices an admin later
  // customised (anything already non-zero is left alone). New installs are already priced above.
  {
    let backfilled = 0;
    for (const price of priceRows) {
      if (!price.Price) continue;
      const [n] = await models.ShopItemPrice.update(
        { Price: price.Price },
        { where: { ID: price.ID, Currency: price.Currency, Duration: price.Duration, Price: 0 } },
      );
      backfilled += n;
    }
    if (backfilled) Log.info(`Backfilled ${backfilled} shop price(s) onto previously-free rows.`);
  }

  // Retire the unfinished custom cannon (ID 20144) from existing shops. Keep the item row so
  // existing owners' inventory and loadouts can still resolve it; without prices it is not for sale.
  const retiredCustomWeaponIds = [20144];
  const retiredPriceRows = await models.ShopItemPrice.destroy({ where: { ID: retiredCustomWeaponIds } });
  if (retiredPriceRows) Log.info(`Removed ${retiredPriceRows} price row(s) for retired custom weapons.`);

  // Insert shop items present in the seed catalogue but MISSING from an existing DB. seedIfEmpty
  // above only fills entirely-empty tables, so new catalogue additions would otherwise never reach
  // a live server. This is additive only - it never modifies items the DB already has, just adds
  // the new ones and their prices.
  async function seedNewItems(model: any, items: any[], label: string): Promise<void> {
    if (!items?.length) return;
    const existing = await model.findAll({ attributes: ['ID'], raw: true });
    const have = new Set((existing as any[]).map((r) => r.ID));
    const missing = items.filter((it) => !have.has(it.ID));
    if (!missing.length) return;
    await model.bulkCreate(missing as any[]);
    const newPrices = missing.flatMap((it: any) => (it.Prices ?? []).map((p: any) => ({ ...p, ID: it.ID })));
    if (newPrices.length) await models.ShopItemPrice.bulkCreate(newPrices);
    Log.info(`Added ${missing.length} new ${label} (+${newPrices.length} price rows).`);
  }
  await seedNewItems(models.ShopFunctionalItem, shop.FunctionalItems as any[], 'functional items');
  await seedNewItems(models.ShopGearItem, shop.GearItems as any[], 'gear items');
  await seedNewItems(models.ShopQuickItem, shop.QuickItems as any[], 'quick items');
  await seedNewItems(models.ShopWeaponItem, shop.WeaponItems as any[], 'weapon items');

  // Maps + map settings.
  await seedIfEmpty(models.Map, maps as Partial<Map>[], 'maps');
  const mapSettings = maps.reduce((acc: any[], cur) => {
    Object.entries(cur.Settings).forEach(([key, value]) => {
      acc.push({ ...(value as object), MapId: cur.MapId, GameModeType: key });
    });
    return acc;
  }, []);
  await seedIfEmpty(models.MapSettings, mapSettings, 'map settings');

  await sequelize.query('SET FOREIGN_KEY_CHECKS = 1');

  Log.info('Database seed complete (existing data preserved except explicitly retired shop entries).');
  process.exit(0);
}

// Allow running directly: `bun src/seed/seed.ts`
if (import.meta.main) {
  runSeed();
}
