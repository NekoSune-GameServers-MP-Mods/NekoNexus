import type { Sequelize } from 'sequelize';

interface Migration {
  /** Stable, immutable identifier. Never rename a migration after it has shipped. */
  name: string;
  up: (sequelize: Sequelize) => Promise<void>;
}

/**
 * Database changes are deliberately explicit. Do not use Sequelize sync({ alter: true }) at
 * startup: on MySQL/MariaDB it can repeatedly create duplicate indexes and can make unsafe
 * guesses about destructive schema changes. Add a new, uniquely named migration for each schema
 * change; migrations run once and their names are recorded in NekoNexusMigrations.
 */
const migrations: Migration[] = [
  {
    name: '20261016_0001_dedupe_payment_order_indexes',
    async up(sequelize) {
      const [tables] = await sequelize.query(
        "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'PaymentOrders'",
      );
      if (!(tables as any[]).length) return;

      // Old alter-sync versions could create many identical indexes on ExternalId. Keep one index
      // for each equivalent definition, rather than dropping every index or touching payment rows.
      const [rows] = await sequelize.query('SHOW INDEX FROM `PaymentOrders`');
      const indexes = new Map<string, { name: string; unique: boolean; columns: string[] }>();

      for (const row of rows as any[]) {
        const name = String(row.Key_name);
        const existing = indexes.get(name) ?? {
          name,
          unique: Number(row.Non_unique) === 0,
          columns: [],
        };
        existing.columns[Number(row.Seq_in_index) - 1] = String(row.Column_name);
        indexes.set(name, existing);
      }

      const seen = new Map<string, string>();
      for (const index of indexes.values()) {
        if (index.name === 'PRIMARY') continue;
        const signature = `${index.unique ? 'unique' : 'index'}:${index.columns.join(',')}`;
        const keep = seen.get(signature);
        if (keep) {
          await sequelize.query(
            "ALTER TABLE `PaymentOrders` DROP INDEX " + sequelize.getQueryInterface().quoteIdentifier(index.name),
          );
          // eslint-disable-next-line no-console
          console.log(`[db:migrate] Removed duplicate PaymentOrders index "${index.name}" (kept "${keep}")`);
        } else {
          seen.set(signature, index.name);
        }
      }

      const [remainingRows] = await sequelize.query('SHOW INDEX FROM `PaymentOrders`');
      const remainingIndexes = new Map<string, { unique: boolean; columns: string[] }>();
      for (const row of remainingRows as any[]) {
        const name = String(row.Key_name);
        const index = remainingIndexes.get(name) ?? { unique: Number(row.Non_unique) === 0, columns: [] };
        index.columns[Number(row.Seq_in_index) - 1] = String(row.Column_name);
        remainingIndexes.set(name, index);
      }
      const hasExternalIdUnique = [...remainingIndexes.values()].some(
        (index) => index.unique && index.columns.length === 1 && index.columns[0] === 'ExternalId',
      );
      if (!hasExternalIdUnique) {
        await sequelize.query(
          'ALTER TABLE `PaymentOrders` ADD UNIQUE INDEX `uq_PaymentOrders_ExternalId` (`ExternalId`)',
        );
      }
    },
  },
];

export async function runDatabaseMigrations(sequelize: Sequelize): Promise<void> {
  await sequelize.query(
    'CREATE TABLE IF NOT EXISTS `NekoNexusMigrations` (' +
      '`Name` VARCHAR(191) NOT NULL PRIMARY KEY, ' +
      '`ExecutedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP' +
      ') ENGINE=InnoDB',
  );

  for (const migration of migrations) {
    const [rows] = await sequelize.query(
      'SELECT `Name` FROM `NekoNexusMigrations` WHERE `Name` = ? LIMIT 1',
      { replacements: [migration.name] },
    );
    if ((rows as any[]).length) continue;

    // eslint-disable-next-line no-console
    console.log(`[db:migrate] Applying ${migration.name}`);
    await migration.up(sequelize);
    await sequelize.query(
      'INSERT INTO `NekoNexusMigrations` (`Name`) VALUES (?)',
      { replacements: [migration.name] },
    );
    // eslint-disable-next-line no-console
    console.log(`[db:migrate] Applied ${migration.name}`);
  }
}
