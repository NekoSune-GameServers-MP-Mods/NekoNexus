import models from '@festivaldev/nekonexus-models';
import bcrypt from 'bcryptjs';
import mysql from 'mysql2';
import { Sequelize } from 'sequelize';
import { type AdminConfig } from './config';

export let sequelize: Sequelize;

export async function initDatabase(cfg: AdminConfig) {
  sequelize = new Sequelize(cfg.db.database, cfg.db.user, cfg.db.password, {
    host: cfg.db.host,
    port: cfg.db.port,
    dialect: 'mysql',
    dialectModule: mysql,
    logging: false,
  });

  // Initialize all models against this connection (so associations/columns line up with the
  // tables the web service created).
  for (const model of Object.values(models) as any[]) model.initialize?.(sequelize);
  for (const model of Object.values(models) as any[]) model.associate?.(models);

  await sequelize.authenticate();

  // AdminUsers has a stable schema. Using alter-sync here makes Sequelize repeatedly attempt
  // to add the Username unique index on existing MySQL/MariaDB tables; over time this can hit
  // MySQL's 64-index limit and crash the admin container with ER_TOO_MANY_KEYS.
  // Plain sync creates the table on a fresh install but leaves existing admin data and indexes
  // untouched on subsequent starts.
  await models.AdminUser.sync();
  await models.CreditPackage.sync({ alter: true });
  await models.PaymentOrder.sync({ alter: true });
  await models.PlayerReport.sync({ alter: true }); // player-report inbox (owned by the admin app)
  try {
    await models.PhotonServer.sync({ alter: true });
  } catch {
    /* PhotonServers may be managed by the web service seed; ignore alter conflicts */
  }

  await ensureDefaultAdmin();
  await ensureDefaultPackages();
}

// Create a default admin/admin login on first run. The user is prompted to change it.
async function ensureDefaultAdmin() {
  const count = await models.AdminUser.count();
  if (count === 0) {
    const hash = await bcrypt.hash('admin', 10);
    await models.AdminUser.create({ Username: 'admin', PasswordHash: hash } as any);
    // eslint-disable-next-line no-console
    console.log('[admin] Created default admin account -> username: admin  password: admin  (change this!)');
  }
}

// Seed a few starter credit packages on first run so the in-game web store isn't empty.
// Edit / add / remove these in the admin dashboard (Store -> Packages). Cheap by design.
async function ensureDefaultPackages() {
  const count = await models.CreditPackage.count();
  if (count > 0) return;
  const defaults = [
    { Name: 'Starter Pack', Credits: 1000, PriceCents: 99, Currency: 'USD', Enabled: true },
    { Name: 'Value Pack', Credits: 5000, PriceCents: 299, Currency: 'USD', Enabled: true },
    { Name: 'Pro Pack', Credits: 12000, PriceCents: 499, Currency: 'USD', Enabled: true },
    { Name: 'Elite Pack', Credits: 30000, PriceCents: 999, Currency: 'USD', Enabled: true },
  ];
  for (const p of defaults) await models.CreditPackage.create(p as any);
  // eslint-disable-next-line no-console
  console.log('[admin] Seeded 4 default credit packages (edit them in Store -> Packages).');
}
