# Changelog

## 4.7.18 — Match clock and bot behavior fixes

### Matches
- Normalize match timestamps to the client's 31-bit Photon clock and use a UTC server deadline,
  so signed tick-count wrap cannot freeze the countdown or prevent the round from ending.
- Unsubscribe the match-end handler when a round exits to prevent duplicate transitions in later
  rounds.

### Realtime bots
- Capture spawn positions and roam along nearby, observed player routes instead of waiting only
  when an enemy is outside the weapon's preferred range.
- Track the target with both horizontal and vertical aim. Bot shots and damage require a recent
  real-client hit confirmation on that bot, are limited to close range, and expire if either
  player moves; this uses a client-validated sight line to avoid wall shots.

## 4.7.17 — Human-like bot movement and firing

### Realtime bots
- Bots follow short movement traces recorded from real players in the same room, including valid
  grounded, jumping, and water movement. This avoids inventing map paths without server-side map
  collision or water geometry.
- Bots stop when no nearby valid route is available, and fire more slowly with lower accuracy.

## 4.7.16 — Server location flags and region detection

### Servers
- The admin server form detects a server's country and supported game-routing region from its IP, and saves the country code.
- The game server list now receives a country flag token and displays the flag next to the server name without showing the token.
- The public status page and admin dashboard show country/location labels and use the game's actual six supported region IDs, fixing Australia being labelled North America.

## 4.7.15 — Data-service retry and signup wallet fixes

### Client
- A temporary failure reaching the data service now offers **Retry** or **Quit** instead of closing
  the game after the error dialog. Failed or empty SOAP responses are handled safely, and telemetry
  no longer retries recursively through an unavailable endpoint.

### Accounts
- New email accounts now receive 1,000 credits and 10,000 coins (Points) on first login, matching
  the existing Steam signup grant.

## 4.7.14 — Updater moved to the new GitHub organization

### Client updates
- The web service now relays updater requests through the new NekoNexus GitHub Pages site. The
  client updater continues using the configured web service URL.
- Client update publishing now reads the existing manifest from the new repository location, and
  generated update prompts report product version 4.7.14.

### Shop
- Removed the unfinished custom cannon from the seeded catalogue. Running the explicit seed command
  removes its sale prices while retaining the item record for existing inventories and loadouts.

### Deployment
- Updated Docker image references and website links to the new GitHub organization.

## 4.7.13 — AI fill bots are now BETA, admin-controlled, off by default

### Admin
- **New "AI fill bots" toggle on the Gifts & Events page.** Enable/disable bots, set the room
  fill target and max-bots-per-room, right from the admin dashboard - no config file editing or
  redeploy needed. Marked **Beta** (movement/combat behavior is still being tuned per the
  4.7.11/4.7.12 fixes) and **off by default**.

### Servers
- AI fill bots are no longer a YAML setting. `GameplaySettings.BotsEnabled/BotFillTarget/MaxBots`
  are removed from the Realtime config; the toggle now lives in a DB-backed admin setting (new
  `BotsConfig` table, same pattern as the global coin-boost event) and is pushed live to every Game
  server the moment an admin saves it (new `SetBotsConfig` master-socket packet) - no realtime
  restart required, and a reconnecting Game server gets the current value re-pushed automatically so
  a restart can't silently flip it back. Defaults to disabled until an admin opts in.
  (Requires the Realtime, ws, and admin images all redeployed.)

## 4.7.12 — AI fill bots play a real walk animation

### Servers
- **Bots no longer glide/float while moving.** The client's avatar animator drives walk/run/idle
  entirely off the position update's velocity and key-state bytes; bots never set either, so no
  matter how far they actually moved each tick, other clients rendered them in a static floating
  pose sliding across the ground instead of playing a walk animation. Bots now report a real
  velocity and forward/still key state each tick, and mark themselves grounded, matching how a
  real client's movement packet looks. (Requires the Realtime image redeployed.)
- Added diagnostics for the reported stuck-timer/bots-not-engaging behavior in Team Deathmatch:
  logs now record team counts and cause whenever CanStartMatch drops mid-round or during the
  pre-match countdown, and each bot's assigned team is logged when it's added. Investigated
  extensively but couldn't reproduce a concrete root cause without a live server log - these logs
  will pinpoint it from the next report.

## 4.7.11 — AI fill bot movement & combat fixes

### Servers
- **Bots no longer fly/float around the map.** Movement now steps horizontal and vertical
  position separately, each capped to a walking-speed rate, instead of interpolating straight
  through the air toward an elevated waypoint or enemy position.
- **Bot combat range now matches the weapon they're holding.** Engagement/hit range used to be one
  flat 32-unit bubble for every ranged weapon, so a shotgun or splattergun bot could land hits from
  sniper distance. Range is now keyed per weapon archetype (shotgun/splattergun close, machinegun/
  cannon/launcher medium, sniper rifle long), so bots only fire within their own weapon's effective
  range. (Requires the Realtime image redeployed.)

## 4.7.10 — AI fill bots

### Servers
- **AI fill bots keep low-population rooms playable.** Solo/low-pop matches are now topped up
  with AI bots (random loadouts drawn from the server's own shop catalog, a patrol/engage/respawn
  FSM) so there's always someone to level up against. Bots scale down automatically as real
  players join — one bot is kicked per joining human and backfilled if a human leaves — and are
  excluded from XP/points persistence and match history. Configurable via `GameplaySettings.
  BotsEnabled` / `BotFillTarget` / `MaxBots` in the Realtime config. (Requires the Realtime image
  redeployed.)

## 4.7.9 — In-game ↔ web clan chat bridge fix

### Servers
- **In-game clan chat now reaches the web clan channel.** The ws buffered forwarded clan messages
  under `GroupId: undefined` (the ClanMember model hides GroupId by default), so nothing landed in
  the website's clan channel. Fixed with an unscoped lookup — the in-game↔web clan bridge now
  mirrors both ways. (Requires the Realtime, ws and admin images all redeployed.)

## 4.7.8 — Web clan chat + Friends DM fixes

### Website
- **Clan chat now finds your clan.** It reported "You're not in a clan" for actual members —
  the ClanMember model hides GroupId by default, so the lookup missed it. Fixed (also restores the
  clan shown on web profiles).
- **Friends DM lets you pick a friend.** The DM tab only listed existing conversations, so "Select
  a friend to chat" was a dead end — it now lists all your friends (with online status) to start a
  DM with anyone.

## 4.7.7 — Launch-only update check, admin event themes + New Year countdown

### Client
- **Update check runs only at launch**, not on every Home-menu load — the "Update available"
  popup no longer keeps reappearing while you're in the menus.

### Website / admin
- **Site event themes are now set in the admin panel** (Gifts & Events → Site event theme) and
  stored in the DB, so they change with no redeploy — and Pride no longer stays stuck on after
  June. Modes: auto | none | pride | halloween | xmas | newyears | easter. **Auto** picks by date
  (Pride June, Halloween October, Christmas Dec 1–30, New Year Dec 31 / Jan 1, Easter approx) and
  returns to the default brand off-season.
- **New Year theme shows a live countdown** to midnight in a **configurable IANA timezone**
  (default Europe/London = UK), computed per-region client-side; shows "Happy New Year!" for 24h
  after. New `GET/POST /api/config/theme` + a `SiteConfig` table.

## 4.7.6 — Auto-updater works over HTTPS, maps auto-deliver, missing maps restored

### Client / auto-updates
- **Auto-updater fixed ("Failed to download update catalog").** The updater used Unity's WWW
  with an overly-strict response-header check that rejected a perfectly good download against
  the relay/CDN in front of the update host (and an HttpWebRequest attempt hit old Mono's TLS
  wall). Now it uses WWW (the transport the game's own web service already uses successfully over
  HTTPS) and treats "no error + content" as success — updates download and install again.
- **Update detection runs on the main thread** so the "Update available" / download-progress /
  "please restart" popups actually appear (they were silently failing on a background thread).
- **The `build:` field now reflects the product version** (e.g. `4.7.6-…`) instead of the fixed
  `4.7.1`, and the channel `version:` auto-increments on every publish.

### Maps
- **The three download-on-demand maps are back:** Space City, Spaceport Alpha and UberZone. Their
  `.unity3d` bundles are shipped via the update channel (committed to the repo, staged by the
  publish pipeline) and auto-installed into `UberStrike_Data/Maps/` on launch — then they appear
  in the Create-Game map list.

### Discord
- Discord Rich Presence helper retargeted to .NET 4.8 (was 4.8.1, which wouldn't launch on a
  4.8-only machine), with a log file + an overridable application id (`discord-app-id.txt`).

## 4.7.5 — Web social & shop, multi-channel chat, updater fix, Open API docs

### Website
- **Friends & mail on the web** (`/social`): list / add / accept / remove friends and read / send /
  delete mail, backed by the same `ContactRequests` / `PrivateMessages` tables the game uses, so
  everything stays in sync. Web actions nudge the player's in-game lists in realtime.
- **Web chat with three channels**, each synced with the game:
  - **Global** — shares one stream with the in-game global lobby (web posts broadcast into the
    lobby; in-game lobby chat shows on the web).
  - **Clan** — mirrors in-game clan chat both ways (the Comm server now forwards each in-game clan
    line to the web service; web posts broadcast to the clan's online members).
  - **Friend DMs** — a persistent 1-on-1 thread (new `DirectMessage` table), limited to accepted
    friends, that is also delivered to the friend as an in-game private/whisper message when online.
  - Web chat is **gated**: you must have logged into UberStrike at least once and finished creating
    your account (a named profile) — otherwise it tells you to play once first.
- **Web shop** (`/shop`): browse the real item catalogue (weapons / gear / quick / functional) with
  prices, level locks and icons; **buy** with Credits or Points (mirrors the in-game purchase — same
  ownership / for-sale / level / balance checks, same inventory + transaction tables, live wallet
  refresh); **equip / unequip** items into your loadout. Item icons served at `/images/items/<id>.png`
  with a placeholder fallback (drop real icons in per the README).
- **Leaderboard now ranks everyone, staff included** — only the System Staff account (Cmid 0) and
  unnamed rows are excluded (public, Open-API and admin boards alike).
- **Boost-event banner on the homepage**: when a coin/XP boost is running it shows a live banner with
  the multiplier and a countdown to the end. New public `GET /api/public/boost`.

### Open API
- **Browsable API docs** at `/api/docs` (and `/docs`): every public read-only endpoint (players,
  match history, clans, leaderboard, status, boost) with parameters, a "try it" link and an example
  response. `GET /api/v1` now points at the docs.

### Client
- **Launch update check fixed.** Update detection and its popups were running on a background thread;
  Unity calls fail silently off the main thread on Mono, so the "Update available" prompt never
  showed and the game went straight into the menu. The check now runs on the main thread, so the
  download-progress popup and the "please restart" prompt appear as intended when a new version is
  published.

## 4.7.4 — Multi-node servers, realtime-notify fix, crash hardening

### Servers / multi-node
- **Per-node identity via env vars / flags** (`NEKONEXUS_IDENTIFIER` / `NEKONEXUS_PHOTON_ID` /
  `NEKONEXUS_PASSPHRASE`, or `--identifier` / `--photon-id` / `--passphrase`). Run many game nodes
  off one image — each registers as its own server instead of all colliding on the default
  `2222…`/PhotonId 2. The node's PhotonId must match its admin server-browser entry.
- **Shared node passphrase** (`GAME_NODE_PASSPHRASE` / `COMM_NODE_PASSPHRASE`): a node whose GUID
  isn't pre-registered in `ServerCredentials` is accepted if it presents the shared secret — add
  nodes with env vars only, no web-service yml edit.
- **Stale-session eviction on reconnect:** a realtime server reconnecting after a restart/blip is
  accepted (the stale session is dropped) instead of being rejected as a duplicate; a genuine
  duplicate-identifier misconfig is rejected with a clear message instead of thrashing.

### Crash & stability hardening
- **Web service no longer crashes on game-room sync.** A node with a mismatched PhotonId could make
  the room cleanup miss, causing a duplicate-primary-key insert that took the whole service down in
  a loop (which broke joins, matches and updates). Room sync now upserts, cleans up by the rooms'
  own address, and is fully guarded.
- **Realtime notifications actually send now.** The wallet / boost / mail / clan push packets were
  missing from the socket encoder *and* decoder, so every realtime push silently no-op'd. Fixed on
  both ends — live credits/coins, the global boost event, and instant inbox refresh now work.

### Client
- **Spectator-on-join fix:** a player joining a running match is routed to the proper Spectating
  state instead of being spawned as a phantom "playing" actor (broken view + a ghost at the origin).
- **Auto-update relay + faster checks:** the file server can relay `/updates` to GitHub Pages
  (`UPDATE_RELAY_URL`) so the client keeps pointing at your domain while Pages is the source of
  truth; the update-check throttle dropped from 1 hour to 20 s so new versions are picked up promptly.

## 4.7.3 — Live economy, realtime stability, updater + skin fixes, CI publisher

### Economy (updates live, no relog)
- **Live wallet push.** Credits/coins now update on screen instantly when granted — admin gift,
  store purchase, or match payout — instead of only after a relog. The web service pushes the new
  balance over a new Comm `NotifyWallet` → `SendUpdateWallet` lobby event (mod-only opcode 100),
  and a client `WalletUpdateHook` writes `PlayerDataManager` so the HUD ribbon refreshes.
- **Admin "Gifts & Events" page.** Gift credits and/or coins to one or many players at once
  (amounts are added), with a live refresh for anyone online (`POST /api/players/gift`).
- **Global 2×/5× coin boost event.** Set a coin (and optional XP) multiplier with an optional
  duration in admin; it's pushed to every game server over the master socket and applied to the
  next match — no restart. Persisted so it survives ws/game restarts (`/api/config/boost`).

### Realtime stability (random crashes / "won't read data" / ghost-walking)
- Room peer/player lists are now iterated as locked snapshots; the per-room loop tick is
  exception-isolated so one bad room can't freeze every room on its scheduler thread.
- Match-end persistence (points/stats web calls) moved off the loop thread; the end-of-match
  state transition runs on the loop thread (the state machine isn't thread-safe).
- The master-socket handler and the network poll loop are guarded so a stray exception can't take
  networking offline.

### Client / fixes
- **Match countdown** no longer stays stuck at full time — added a server-time sync handshake to
  the transport shim (the client was timing against its own clock, not the server's).
- **Inbox** now auto-refreshes incoming **friend requests** in realtime (previously only after a
  manual refresh).
- **Invisible skins** — a gear/weapon whose prefab fails to load now falls back to a default mesh
  instead of rendering nothing.
- Fixed the killed-spectator hook (disposed timer + 0 ms delay) that flipped dead players to
  noclip/free-spectator at the wrong time.
- **Auto-updater** "Failed to download" is handled cleanly (transport-failure guard + per-file
  timeout); the root cause (update channel published without its payload DLLs) is fixed by the new
  CI publisher below.

### CI / updates
- `misc/publish-update.ps1` builds the mod, stages **both** channels, refreshes the installer, and
  can publish the channel + installer to an `updates` branch (GitHub Pages) via
  `misc/publish-update-branch.ps1`.
- `.github/workflows/publish-client.yml` (self-hosted runner) builds and publishes client updates
  globally; the server Docker images keep building on hosted runners. See
  `docs/client-updates-and-ci.md`.

## 4.7.2 — Connectivity, friends/mail, in-game store, one-click installer

Fixes that get players actually online + talking, an antivirus-friendly one-click installer,
a NekoPay-powered in-game store, and a safer (non-destructive) database seed.

### Client / connectivity
- **Realtime now connects** — the LiteNetLib client shim binds **IPv4-only** (`IPv6Mode.Disabled`).
  Fixes `SocketException: An address incompatible with the requested protocol was used` on PCs
  with IPv6 disabled, which broke the server browser / lobby with "Couldn't connect to server".
- **"Get Credits" no longer freezes the game.** Hooked `ApplicationDataManager.OpenBuyCredits` to
  open the NekoNexus **web store** in the browser instead of the native credit-bundle page (which
  has no bundles here and threw every frame, locking the UI).

### One-click installer + patcher
- **`NekoNexusSetup.exe`** (Inno Setup) — single native installer: auto-detects UberStrike from
  Steam (registry + `libraryfolders.vdf`), patches, and **auto-downloads/installs .NET 4.8** if
  missing. No `.cmd`, no PowerShell. Built via `misc/build-installer.ps1`.
- **Self-contained patch zip** with a double-click `Install.cmd`, Steam auto-detect, and a baked
  `nekonexus-target.json` (server owners set their domain once). Ships prebuilt mod DLLs — no repo
  or .NET SDK needed by players.
- **Patch-order fix**: install the mod DLLs **before** patching — the patch injects a call into
  `NekoNexus.Client.Bootstrap`, which must be resolvable, or the patcher failed (exit 1).
- Dedicated **`patcher` branch** holds the ready-to-download toolkit.

### Friends, mail, presence (server-side)
- **Add friend** now works: `SendContactRequest` sets `Status: Pending` (the column had no DB
  default, so requests were invisible) and **re-sends/repairs** an existing non-accepted request.
- **Private messages**: fixed the inbox thread list (`.filter` not `.find`), the send-message
  response serialization, and a `cmid` casing typo.
- Disconnect now reliably leaves the lobby (handled in `LobbyRoom`), keeping presence accurate.

### Clans
- **Clan invites work**: `AcceptClanInvitation` created the member row **without `GroupId`** (no
  real membership) — fixed. The clan **`[TAG]`** now shows next to names in chat: the tag is written
  to the profile on create/join (cleared on leave/kick/disband) and carried on the comm actor.

### Gameplay / realtime stability
- In-match ops that **threw** (and aborted the op): **quick-item activation** and **hit-feedback**
  knockback are now implemented (broadcast to peers); ready-up, in-match gear/health/damage,
  game-info, inspect-room, report-player and backend-refresh are safe no-ops instead of exceptions.
- **Moderator ban/kick** of an *offline* player no longer NullReferences; **unban** is implemented.
- **Match progression is saved again**: points + XP/stats persistence (`DepositPoints` /
  `UpdatePlayerStatistics`) was commented out during the port — re-enabled, so players keep XP,
  level and stats after matches.

### Shop / economy
- **Get Credits & the shop's Credits tab no longer freeze the game** — both open the web store.
- **BuyItem**: the item was granted **even when payment failed** (and Points purchases deducted
  Points but wrote to Credits) — now items are only granted on a successful purchase.
- **VerifyAuthToken** returned wallet data as player stats — fixed.

### In-game store + payments
- Admin serves a public **`/store`** page (opened by Get Credits): lists credit packages, **Buy**
  creates a **NekoPay** checkout, webhook grants credits. `/pay/success|cancel` return pages.
- Seeds 4 starter credit packages on first run (edit in **Store → Packages**).

### Docker / CI / database
- **Non-destructive seed**: `sync({ alter: true })` + seed-only-if-empty — re-running the seed
  never wipes players, wallets, stats, friends, or customizations (was `force: true`).
- Admin **Docker image** + compose service (`:8088`); compose pulls prebuilt **GHCR** images.
- `PARADISE_PUBLIC_HOST` seeds the realtime server list with a reachable IP (not `127.0.0.1`).
- CI: lowercase GHCR tags, `working-directory` bun builds, `Directory.Build.props` so the net35/
  net481 server compiles on Linux, copy all workspace manifests for `bun install`.
- Reverse-proxy reference: forwarded headers, body size, `/store` + `/pay`, UDP/TLS caveats.

## 4.7.1 — Free, cross-platform server + admin platform

This release removes the proprietary Photon Server SDK, makes the whole stack free and
cross-platform, and adds an admin dashboard, store, and payments.

### De-Photon (free server)
- Replaced `Photon.SocketServer.dll`, `PhotonHostRuntimeInterfaces.dll`, `Photon3Unity3D.dll`
  and `UnityEngine.dll` (server-side) with free managed shims over **LiteNetLib** (`shims/`).
  No Photon licence, no CCU/slot cap — set your own `MaxPlayerCount`/`--max-peers`.
- Self-host launcher `NekoNexus.Realtime.Host` replaces `PhotonSocketServer.exe`
  (reads `PhotonServer.config`, one app per process, `--master-host` override).
- Client `Photon3Unity3D` shim matched to the original DLL's exact signatures + enum values
  (verified with Mono.Cecil). Fixed: NAT-punch module crash on Unity Mono, `CallInBackground`
  signature, `StatusCode`/`PeerStateValue` enum values, `SupportClass`, `uberdaemon` fallback.

### Cross-platform / deployment
- **Docker** multi-arch (amd64 + arm64): `docker-compose.yml` + `docker/` (db, web services,
  Comm, Game). Web services build standalone exes for Windows/Linux/macOS (x64 + arm64) via Bun.
- **GitHub Actions** (`.github/workflows/build.yml`): builds server exes, client patch toolkit,
  and multi-arch Docker images on tags.
- Windows one-click: **Server Manager GUI** (configure MySQL / server name / slots, initialise
  DB, start all nodes) + `start-server.bat`.
- One-domain reverse-proxy configs (Caddy + nginx) in `misc/reverse-proxy/`. See `DOMAIN-SETUP.md`.

### Client patcher
- `make-client-patch.ps1` patches a local UberStrike install to use the free server + transport
  (game files never redistributed). Built `UniversalUnityPatcher`. Releases zip in `.release/client`.

### Web service
- Added a `seed` subcommand to the web service exe (initialise the database).
- Fixed seeding: bundle `mysql2`, disable FK checks while seeding, `sync({force})` clean schema.
- Server-list now respects `PhotonServer.Enabled`; aligned PhotonIds.
- **Realtime presence is now live**: Comm/Game publish their roster every 5s; the master rebuilds
  player/room state idempotently (no more stale/duplicate data).

### Admin dashboard (`packages/admin`)
- New Tailwind dark/green dashboard exe. Secure login (default `admin`/`admin`, change in-app).
- **Servers**: add (IP, port, name, region with IP auto-detect), enable/disable, online status.
- **Players**: search, ban/unban, edit level/XP/points, edit credits, set access level,
  **give item via searchable name dropdown** (weapons/gear/quick/functional). Level edits now
  set XP to the level threshold so the game keeps the level (no reset to 1). Max level configurable
  (e.g. 750).
- **Leaderboard** (staff + unnamed accounts hidden). **Reports** (moderation log).
- **Store + NekoPay**: credit packages, orders, "set all shop items free", hosted-checkout
  creation + webhook that grants credits on payment (idempotent). Secret via git-ignored
  `.nekopay.env`.

### Repo hygiene
- `.gitignore` excludes build output, generated shim DLLs, and all secrets (`.env`, `.nekopay.env`).
