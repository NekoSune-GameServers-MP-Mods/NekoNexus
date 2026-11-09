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

* Numbers are **big-endian** (the client byte-reverses `BitConverter` output on little-endian CPUs).
* `String` (`0x0C`): `[int16 BE char count][UTF-16LE chars]`; empty string = `00 00`.
* `Array_Byte` (`0x0F`): `[int32 BE count][bytes]`.
* `ServerLoadData` (`0x2F`, named PerformanceData in the enum): `[int players][int peers][int rooms][float maxPlayers]`.

Type codes: Byte `01`, SByte `02`, Bool `03`, Int16 `04`, UInt16 `05`, Int32 `06`, Long `08`,
Float `0A`, String `0C`, Array_Byte `0F`, Array_Short `12`, Array_UShort `13`, Array_Int `14`,
Array_Long `16`, Array_Float `18`, Array_String `1A`, Vector3 `1E`, Quaternion `1F`, Color `20`,
Array_Vector3 `23`, Array_Quaternion `24`, RoomData `28`, Transform `2A`, AssetType `2B`,
PhysicsPack `2C`, RoomId `2D`, CommActorInfo `2E`, PerformanceData `2F`, SyncObject `30`,
Array_RoomId `33`, Array_SyncObject `35`, ActorInfo `65`, ShortVector `66`, GameData `67`,
Armor `68`, Weapons `69`, Statistics `6A`, DamageEvent `6B`, Stats `6C`, EndOfMatch `6D`,
Array_GameData `6F`. `LegacySerialization` implements the primitives, byte arrays and
ServerLoadData; the game types are still to do.

## Status

| Phase | Scope | State |
|-------|-------|-------|
| 1 | Accept 4.3.10 peers, handshake, server load (servers appear with live PC counts) | **Done** (`Server/Legacy`, unit-tested serializer) |
| 2 | Comm/lobby: game list (`0x5A`/`0x5B` events, RoomData/GameData types), chat | To do |
| 3 | Join/leave (`0x58`/`0x59`) into the shared `GameRoomManager` rooms | To do |
| 4 | In-match sync: translate `0x50`–`0x53` instance/method calls to and from the 4.7.1 room events so Android and PC players see each other | To do |

Unimplemented legacy opcodes and methods are logged at debug level by `LegacyPeer`
(`legacy opcode 0x.. not supported yet`, `application method N not supported yet`), with the
parameter keys and decoded argument types, which is the quickest way to find what to build next.
