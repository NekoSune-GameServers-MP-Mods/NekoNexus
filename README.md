# NekoNexus — community game servers for UberStrike

**A NekoSune Community project.** NekoNexus runs the **UberStrike** game server **without the
proprietary Photon Server SDK** — no licence, no CCU/slot cap — on **Windows, Linux (x64 +
ARM64), and Docker**. It also adds a modern **admin dashboard** (server management, players,
leaderboard, store + payments).

> You set your own player slots. The whole server stack is free software.

**Official project links:** [NekoNexus repository](https://github.com/NekoSune-GameServers-MP-Mods/NekoNexus) · [NekoSune Game Servers organization](https://github.com/NekoSune-GameServers-MP-Mods)

NekoNexus is an independent, community-run continuation that began as a fork of the
GPL-licensed [Paradise](https://github.com/festivaldev/Paradise) server, then rebuilt the
transport (de-Photon shims), web services, admin dashboard, and many gameplay features from
the ground up. It is **not** affiliated with or endorsed by Cmune/UberStrike or the Paradise
project — "UberStrike" is referenced only to describe compatibility.

---

## How it works (de-Photon)

The original server needs Photon's paid, Windows-only Server SDK. This fork replaces every
proprietary drop-in with free managed shims (in `shims/`), keeping the game/protocol code
untouched:

| Proprietary dependency | Free replacement (`shims/`) |
|---|---|
| `Photon.SocketServer.dll` + `PhotonHostRuntimeInterfaces.dll` (server) | LiteNetLib-backed `Photon.SocketServer` shim |
| `Photon3Unity3D.dll` (`ExitGames.Client.Photon`, client) | LiteNetLib-backed `PhotonPeer` shim |
| `UnityEngine.dll` (math types used by shared code) | managed `UnityEngine` shim |

Both client and server talk the **same message envelope over LiteNetLib** (a shared wire
codec), so neither the game logic nor `NekoNexus.Realtime` had to change — they just compile
against the shims. A self-hosting launcher (`NekoNexus.Realtime.Host`) replaces
`PhotonSocketServer.exe`. Full detail in [`shims/README.md`](shims/README.md).

## Repository layout

```
packages/
  ws/                    Web services (Bun/TypeScript) — SOAP API, file server, master socket
  admin/                 Admin dashboard (Bun) — auth, servers, players, leaderboard, store
  nekonexus-models/       Sequelize models (shared)
  Realtime/
    NekoNexus.Realtime/       realtime game server (C#, now Photon-free)
    NekoNexus.Realtime.Host/  self-host launcher (replaces PhotonSocketServer.exe)
  Client/                NekoNexus client mod (patches UberStrike)
  Core/                  submodule: reverse-engineered UberStrike SDK
  Patcher/               submodule: UniversalUnityPatcher
shims/                   free managed replacements for the proprietary DLLs (+ vendored LiteNetLib)
docker/                  Dockerfiles + compose + configs (multi-arch amd64/arm64)
misc/                    bundling scripts, server-manager GUI, reverse-proxy configs, setup docs
```

---

## Quick start

### Option A — Docker (everything, multi-arch)

```bash
git submodule update --init packages/Core
docker compose up --build
```
Brings up MySQL + web services + realtime Comm/Game. See [`docker/README.md`](docker/README.md).

### Option B — Windows, one click

Build the bundle (below) or grab a release, then double-click **`Start-Server-Manager.bat`**.
A GUI lets you set MySQL, server name, and max players, **Initialize Database**, and **Start**
all nodes. See [`misc/SERVER-SETUP.txt`](misc/SERVER-SETUP.txt).

### Option C — native, by hand

```bash
# 1. database (once)
NekoNexus.WebServices_x64.exe seed
# 2. run
NekoNexus.WebServices_x64.exe                       # web services :8080/:8081/:8082
NekoNexus.Realtime.Host.exe Comm --master-host 127.0.0.1   # lobby  :5055/udp
NekoNexus.Realtime.Host.exe Game --master-host 127.0.0.1   # game   :5155/udp
NekoNexus.Admin_x64.exe                             # admin dashboard :8088
```

---

## Building from source

Prerequisites: **.NET SDK 9+**, **Bun**, and (for the client patcher on Windows) **MSBuild +
NuGet**. Then:

```bash
git submodule update --init packages/Core
bun install

# Free shims + realtime self-host
bun run build:shims
bun run build:host

# Web services exe(s)
bun --cwd packages/ws    run build:win-x64        # or build-all for every OS/arch
# Admin dashboard exe
bun --cwd packages/admin run build:win-x64

# Client patcher (Windows; needs MSBuild + NuGet)
bun run build:patcher

# Package it all up
powershell -File misc/bundle-server.ps1     # -> .release/server/NekoNexus-server-win-x64.zip
powershell -File misc/bundle-client.ps1     # -> .release/client/NekoNexus-free-server-client.zip
```

CI: [`.github/workflows/build.yml`](.github/workflows/build.yml) builds the server exes, the
client patch toolkit, and multi-arch Docker images on every tag — push `vX.Y.Z` to cut a
release.

---

## Patching the game client

The client toolkit (`.release/client/NekoNexus-free-server-client.zip`) patches **your own**
UberStrike install to use the free server (game files are never redistributed):

```powershell
packages\Client\make-client-patch.ps1 -UberStrikePath "C:\…\Steam\steamapps\common\UberStrike" -ServerHost 127.0.0.1
```
It builds the NekoNexus mod against your game, injects the bootstrap, installs the free
transport, and writes the settings. In-game, switch/add servers under **NekoNexus Settings →
Web Service URLs** (that list is your server browser). Details: [`packages/Client/README.md`](packages/Client/README.md).

---

## Admin dashboard

`NekoNexus.Admin_x64.exe` → **http://localhost:8088** (default login **admin / admin** — change
it immediately). Dark/green Tailwind UI:

- **Servers** — add servers (IP, port, name, region with IP auto-detect), enable/disable,
  online/offline status. Disabled servers are hidden from clients.
- **Players** — search, ban/unban (with reason + duration), edit level/XP/points, edit
  credits, set access level (mod/admin), give items.
- **Leaderboard** — top 50 by XP / level / points / splats.
- **Reports** — moderation log.
- **Store** — credit packages, orders, "set all shop items free", and **NekoPay** payments.

### Store + NekoPay (real money → in-game credits)

1. Copy `packages/admin/.nekopay.env.example` → `.nekopay.env` (git-ignored) and set
   `NEKOPAY_SECRET=sk_live_…` and `PUBLIC_BASE_URL=https://your-domain`.
2. In the dashboard → Store, create credit packages.
3. Players buy via `POST /api/store/buy {cmid, packageId}` → they're redirected to NekoPay's
   hosted checkout. On `checkout.completed`, NekoPay calls
   `https://your-domain/api/webhooks/nekopay` and credits are granted **once** (idempotent).

---

## One domain (reverse proxy)

To serve the web service (8080), file server (8081) behind a single HTTPS domain, use the
configs in [`misc/reverse-proxy/`](misc/reverse-proxy/) (Caddy or nginx). The realtime
master socket (8082) stays internal. Then point the client at `https://your-domain`.

---

## Notes

- Runs on **Mono** for Linux/ARM today; a native .NET 8 build is a planned optimisation.
- Default secrets/credentials in the sample configs are for local testing — **change them for
  production**, and keep the realtime `ApplicationIdentifier`/`EncryptionPassPhrase` matched
  to the web service `ServerCredentials`.
- `.gitignore` excludes build output (`.release/`, `bin/`, `obj/`), generated shim DLLs, and
  secrets (`.env`, `.nekopay.env`).

## Credits & licence

NekoNexus is maintained by the **NekoSune Community** and is free software under the
**GNU GPL v3** (see [`LICENSE`](LICENSE)). New work in this repository — the de-Photon
transport shims, web services, admin dashboard, and the added/restored gameplay features —
is © the NekoSune Community.

This project began as a fork of [festivaldev/Paradise](https://github.com/festivaldev/Paradise)
(GPL v3); portions of the original Paradise code remain © their respective authors and are
retained under the same licence. It builds on the reverse-engineered UberStrike SDK; transport
by [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT).

**UberStrike** and all related game content are © Cmune / their respective owners. NekoNexus is
an unofficial, community-run server and is not affiliated with or endorsed by them. It ships
**no game assets** — you patch your own UberStrike install.
