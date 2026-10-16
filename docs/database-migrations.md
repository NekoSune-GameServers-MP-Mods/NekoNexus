# Database migrations

The admin service applies pending database migrations automatically during startup, after it connects to MySQL/MariaDB and creates any missing admin-owned tables. Applied migration names are recorded in `NekoNexusMigrations`, so each migration is normally run once.

## Adding a schema update

1. Add a new entry to the `migrations` array in `packages/admin/src/migrations.ts` with a new, sortable, permanent name.
2. Put the schema change in its `up` function using Sequelize's query interface or parameterized SQL.
3. Make the migration safe for the supported MySQL/MariaDB versions and existing production data. Backfill data before adding a `NOT NULL` constraint when needed.
4. Test it against both a fresh database and a copy of a database from the previous release.

For example, use a migration to add a column, backfill it, and then tighten its constraint. Use a later migration to remove a column that has genuinely been retired. Never edit or rename a migration after it has shipped; add another migration instead.

## Safety rules

- Do not use `sync({ alter: true })` at startup. It can repeatedly create duplicate indexes on MySQL/MariaDB and can make unreviewed schema changes.
- Plain model `sync()` is used only to create a table if it does not exist; it does not upgrade an existing table's columns.
- Additions, data backfills, index changes, renames, and removals for existing tables belong in versioned migrations.
- A migration that drops a column or table must be explicit and reviewed because it can permanently delete data. Back up production data before deploying destructive migrations.
- If a migration fails, startup fails rather than pretending the database is up to date. Fix the migration or database condition, then restart; successful migrations are recorded only after their `up` function completes.

The first migration removes redundant duplicate indexes left behind by older `alter: true` startup logic on `PaymentOrders`, while preserving one unique index for `ExternalId`. It does not delete payment rows.
