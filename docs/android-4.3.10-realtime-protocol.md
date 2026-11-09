# UberStrike 4.3.10 realtime protocol (Android) and crossplay

The Android APK runs the 4.3.10 client. Its realtime SDK (`UberStrike.Realtime.UnitySdk.dll`)
speaks Cmune's older RPC protocol, not the 4.7.1 operation-handler protocol that
`NekoNexus.Realtime` implements for PC. Everything below was recovered from the 4.3.10 client
assemblies (`PhotonPeerListener`, `OperationFactory`, `RealtimeSerialization`,
`DefaultByteConverter`, `ServerRequest`, `ServerLoadRequest`).

`Server/Legacy/` adds a second front end for these clients on the **same** game and comm
servers. Android players therefore use the same server list, the same processes and the same
room manager as PC players. That shared state is the basis for crossplay.

## Telling the clients apart

The 4.3.10 SDK connects with `PhotonPeer.Connect(server, cmid.ToString())`, so the connect
payload (`InitRequest.ApplicationId`) is the player's **numeric CMID**. PC sends its version
string. `BasePeer` disconnects anything that isn't `ApiVersion.Current`, which is why Android
clients were dropped immediately. `GameServerApplication`/`CommServerApplication.OnCreatePeer`
now hand numeric ids to `LegacyGamePeer`/`LegacyCommPeer` and everything else to the existing
peers, so PC behaviour is unchanged.

## Operations

| Code | Name | Request parameters |
|------|------|--------------------|
| `0x42` | MessageToApplication | `100` methodId (byte), `61` invocationId (short), `103` args |
| `0x50` | MessageToPlayer | `102` actorId (int), `101` instanceId (short), `100` methodId, `103` args |
| `0x51` | MessageToAll | `101` instanceId, `100` methodId, `103` args |
| `0x52` | MessageToServer | `101` instanceId, `100` methodId, `103` args |
| `0x53` | MessageToOthers | `101` instanceId, `100` methodId, `103` args |
| `0x58` | PhotonGameJoin | `103` room data bytes, `206` cmid (int), `205` access level (int) |
| `0x59` | PhotonGameLeave | `120` room id (byte[]) |
| `0x5A` | GameListUpdate | (lobby event) |
| `0x5B` | GameListRemoval | (lobby event) |
| `0x5C` | RegisterGameServer | |

A `0x42` reply is an OperationResponse with code `0x42`, ReturnCode `0`, and
`61` = the request's invocationId (short), `42` = reply objects (serialized as below). The client
matches it to the waiting callback by invocationId and calls `callback(returnCode, objects)`.
A non-zero ReturnCode, or a missing `61`, is ignored by the client (the request then times out).

## Application methods (`0x42`)

| Id | Sent when | Args | Reply |
|----|-----------|------|-------|
| 1 | Every connect (`OnStatusChanged` → Connect) | `(byte peerType, string clientVersion)`; peerType 1 = game peer, 2 = comm | none expected |
| 2 | Server list refresh (`ServerLoadRequest`) | none | `[ServerLoadData]` |

The client connects, calls method 2, waits up to 5 s, then disconnects. A server only becomes
selectable (non-zero latency, status Alive) after this succeeds.

## Argument serialization (`RealtimeSerialization`)

A sequence of values, each `[CmuneDataType byte][payload]`.

* Numbers are **little-endian**. `DefaultByteConverter.CheckEndian` only reverses bytes on big-endian
  CPUs, and Android/ARM is little-endian. (An earlier version of this note said big-endian; the server
  then byte-swapped every number, e.g. the lobby room 66 arrived as `0x42000000` and every lobby
  join was refused.)
* `String` (`0x0C`): `[int16 char count][UTF-16LE chars]`; empty string = `00 00`.
* `Array_Byte` (`0x0F`): `[int32 count][bytes]`.
* `ServerLoadData` (`0x2F`, named PerformanceData in the enum): `[int players][int peers][int rooms][float maxPlayers]`.

Type codes: Byte `01`, SByte `02`, Bool `03`, Int16 `04`, UInt16 `05`, Int32 `06`, Long `08`,
Float `0A`, String `0C`, Array_Byte `0F`, Array_Short `12`, Array_UShort `13`, Array_Int `14`,
Array_Long `16`, Array_Float `18`, Array_String `1A`, Vector3 `1E`, Quaternion `1F`, Color `20`,
Array_Vector3 `23`, Array_Quaternion `24`, RoomData `28`, Transform `2A`, AssetType `2B`,
PhysicsPack `2C`, RoomId `2D`, CommActorInfo `2E`, PerformanceData `2F`, SyncObject `30`,
Array_RoomId `33`, Array_SyncObject `35`, ActorInfo `65`, ShortVector `66`, GameData `67`,
Armor `68`, Weapons `69`, Statistics `6A`, DamageEvent `6B`, Stats `6C`, EndOfMatch `6D`,
Array_GameData `6F`. `LegacySerialization` implements the primitives, byte arrays and
ServerLoadData, RoomData, RoomId, SyncObject/CommActorInfo and their lists, Array_Short/Int/String,
Vector3 and Quaternion; the game types (`65`+) are still to do.

Strings inside RoomData are "small" strings: `[byte length][UTF-16LE]`, max 127 chars.
`SyncObject` is `[int32 mask]` and, if the mask is not 0, `[int32 id]` then one type-prefixed value
per set bit. The client walks bits `0..32` with `1 << (i & 31)`, so **bit 0 is written and read
twice**; `LegacySyncObject` reproduces that, otherwise lists of actors go out of step.

## Server -> client calls

`PhotonPeerListener.OnEvent` only understands these event codes:

| Code | Meaning | Parameters |
|------|---------|------------|
| `0` | remote call to a network class | `101` network id (short), `100` method (byte), `103` args |
| `3` | room list update (full refresh) | `42` Hashtable `{122: ToBytes(List<RoomData>)}` |
| `4` | room list update | same as 3 |
| `5` | room list removal | `42` Hashtable `{123: ToBytes(List<RoomId>)}` |

Everything else is logged by the client as `UNHANDLED EventAction`.

## Comm server and lobby chat (implemented)

`CommConnectionManager` joins `RoomMetaData(88, "The CommServer", <comm server>)` with
`PhotonGameJoin 0x58` `{103: ToBytes([RoomData]), 206: cmid, 205: access level}`. The reply must be
`ReturnCode 0` with `{9: actor id (int), 4: CmuneRoomID bytes, 200: false (ClearLevel), 201: long}`;
only then `HasJoinedRoom` is true and `ClientCommCenter` calls are sent.

`ClientCommCenter` is a global network class with the fixed id **4**, but it still has to be
**registered**: every network class except the client SynchronizationCenter (id 1) stays uninitialised
until the server answers `ServerSyncCenter` (id 2) method 3 `RegisterStaticNetworkClass(int actorId,
int instanceId, short networkId)` with event 0 to id 1, method 1 `OnRecieveNetworkID(int instanceId,
short networkId)`. The client sends the request right after a room join; until it is answered
`JoinCommServer` is never sent and incoming calls are buffered. `LegacyPeer` answers it for every peer.
Client -> server calls are `MessageToServer 0x52 {101: 4, 100: method, 103: args}`:

| Id | Method | Args | Server |
|----|--------|------|--------|
| 1 | JoinCommServer | CommActorInfo (SyncObject) | name read, roster sent, PC lobby refreshed |
| 3 | SendUpdatedActorInfo | CommActorInfo | name updated |
| 4 / 47 | SendUpdateResetLobby / SendUpdateAllPlayers | - | roster resent |
| 25 | SendLobbyChatMessage | **(int actorId, string message)** - not the CMID | relayed (see below) |
| 21, 22, 43, 44 | room / contacts updates | various | accepted, ignored |

Server -> client (event code 0, network id 4): `2 OnPlayerLeft(int cmid)`,
`3 OnPlayerUpdate(SyncObject)`, `4 OnFullPlayerListUpdate(List<SyncObject>)`,
`25 OnLobbyChatMessage(int cmid, int actorId, string name, string message)`.
ActorInfo fields (`CMUNESYNC` bits): Cmid `0x01` int, PlayerName `0x02` string, Channel `0x04`
byte, CurrentRoom `0x08` RoomId, Ping `0x10` ushort, ClanTag `0x20` string, AccessLevel `0x40`
byte, ModInformation `0x200` string, ModerationFlag `0x400` byte. Types must match exactly: the
client copies them into the fields with reflection.

What players see:

- Android players get the full lobby roster (PC and Android) and every PC lobby message.
- PC players see Android players in the lobby list as `Name [Android]`.
- Android chat reaches PC players (this comm server directly, other comm servers through the
  master socket) as `Name [Android]` with **CMID 0**, only when `EnableLegacyGuestChat: true` is
  set in the realtime configuration. With it off, the Android player gets one System line saying so.

Why opt-in: 4.3.10 sends its CMID as the Photon application id and its name in CommActorInfo, with
no session ticket the server can check, so a modified client can claim any CMID or name. CMID 0
keeps PC moderation (mute/ban by CMID) from ever hitting a real account because of a forged id,
the `[Android]` tag keeps forged names from passing as PC players, the access level the client
claims is ignored, and messages are rate-limited (750 ms), stripped of control characters, cut to
140 characters and profanity-filtered. A proper fix is a login ticket from the web services that
the patched APK passes on connect.

## Game list (implemented)

When a player picks a server on the Play page, `LobbyConnectionManager` joins
`RoomMetaData(66, "The Lobby", <that server>)` on the **game** server with `PhotonGameJoin`.
`LegacyGamePeer` accepts it and, every 5 seconds while joined, pushes event **3**
`{42: Hashtable{(byte)122: ToBytes(List<RoomData>)}}`. The list type is `6F`: `[int16 count]`, then
each item with its own type byte (`28` RoomMetaData or `67` GameMetaData).

GameMetaData (`67`) = RoomMetaData + `[int16 map][int32 time limit s][int32 splat limit]`
`[int16 game mode][int32 flags][byte level min][byte level max][int32 players in game]`.
4.3.10 game modes: TeamDeathMatch `100`, DeathMatch `101`, TeamElimination `106`
(4.7.1 `GameModeType` 2 / 1 / 3). Flags `0x01..0x10` (LowGravity, Instakill, NinjaArena,
SniperArena, CannonArena) are the same bits in both versions.

Rooms come from the same `GameRoomManager` PC players use; password rooms are left out (the 4.3.10
client checks passwords itself). The CmuneRoomID address is the one the client joined the lobby
with. Joining a match (`PhotonGameJoin` with a GameMetaData) was refused with
`Android players cannot join PC matches yet.` until phase 3/4 exist.

**Needs the new APK:** the Hashtable has to cross the NekoNexus wire, which only the updated
`shims/_shared/NekoNexusWire.cs` (tag 10) can carry. Build `shims/Photon3Unity3D.Shim` and put it
into the APK with `fix_android_apk.py --photon-shim <Photon3Unity3D.dll>`. An old APK receiving the
room list logs an unknown wire tag and shows no games.

## Status

| Phase | Scope | State |
|-------|-------|-------|
| 1 | Accept 4.3.10 peers, handshake, server load (servers appear with live PC counts) | **Done** |
| 2a | Comm: join, roster both ways, lobby chat both ways | **Done** (`LegacyCommPeer`, unit-tested) |
| 2b | Game list: PC rooms shown on Android (event 3, Hashtable over the wire, GameMetaData) | **Done** (needs the rebuilt APK shim) |
| 3 | Join/create (`0x58`, room 0 = create) into the shared `GameRoomManager` rooms through a server-side stand-in (`LegacyMatchProxy`, a real `GamePeer` like the AI bots); game-mode class registration (100/101/106); session lookup over the master socket (`LegacySession`) so the stand-in loads the real profile and loadout | **Implemented, untested on device** (`Legacy/LegacyMatch.cs`) |
| 4 | In-match translation both ways: phone -> room (join 1, position 83, look/weapon 3, hits 68, respawn 72, pickups 61/98, doors 84, shots/projectiles/quick items 86/87/89/99/100) and room -> phone (players 1/2/4, positions 83, deltas 5, spawn 71/85, match start/end 76/77, kills 80, damage 78, score 79); game types 101-109 in `LegacyGameTypes.cs` | **Implemented, untested on device** |

Unimplemented legacy opcodes and methods are logged once per connection at warning level by
`LegacyPeer` (`legacy opcode 0x.. not supported yet`, `application method N not supported yet`),
with the parameter keys and decoded argument types, which is the quickest way to find what to build
next.
