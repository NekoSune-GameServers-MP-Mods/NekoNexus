using System;
using NekoNexus.Realtime.Server.Legacy;

internal static class Program {
    private static void Assert(bool ok, string message) {
        if (!ok) throw new Exception("Legacy protocol smoke test failed: " + message);
    }

    private static int Main() {
        var input = LegacySerialization.ToBytes(1234, "Hello Android", (byte)2);
        var values = LegacySerialization.ToObjects(input);
        Assert(values.Length == 3, "object count");
        Assert(values[0] is int && (int)values[0] == 1234, "little-endian int");
        Assert(values[1] is string && (string)values[1] == "Hello Android", "legacy string");
        Assert(values[2] is byte && (byte)values[2] == 2, "legacy byte");
        Assert(input[0] == LegacySerialization.Type.Int32, "int type code");
        Assert(input[5] == LegacySerialization.Type.String, "string type code");

        var load = new LegacyServerLoadData {
            PlayersConnected = 2, PeersConnected = 3, RoomsCreated = 1, MaxPlayerCount = 100
        };
        var roundTrip = LegacySerialization.ToObjects(LegacySerialization.ToBytes(load));
        Assert(roundTrip.Length == 1 && roundTrip[0] is LegacyServerLoadData, "server load type");
        var parsed = (LegacyServerLoadData)roundTrip[0];
        Assert(parsed.PlayersConnected == 2 && parsed.PeersConnected == 3 &&
               parsed.RoomsCreated == 1 && parsed.MaxPlayerCount == 100, "server load values");

        bool rejected = false;
        try { LegacySerialization.ToObjects(new byte[] { LegacySerialization.Type.String, 5, 0, 65 }); }
        catch (LegacyProtocolException) { rejected = true; }
        Assert(rejected, "truncated string rejection");

        Assert(LegacyOperationCode.MessageToServer == 0x52 &&
               LegacyOperationCode.MessageToAll == 0x51, "legacy operation codes");

        // SyncObject (CommActorInfo): [mask][id][typed fields], bit 0 is written twice.
        var actor = new LegacySyncObject { Id = 5 };
        actor[LegacyActorField.Cmid] = 9;
        actor[LegacyActorField.PlayerName] = "Ab";
        actor[LegacyActorField.AccessLevel] = (byte)3;
        var actorBytes = LegacySerialization.ToBytes(actor);
        var expectedActor = new byte[] { 0x30, 0x43,0,0,0, 5,0,0,0, 0x06,9,0,0,0, 0x0C,2,0,(byte)'A',0,(byte)'b',0, 0x01,3, 0x06,9,0,0,0 };
        Assert(actorBytes.Length == expectedActor.Length, "sync object length");
        for (var k = 0; k < expectedActor.Length; k++) Assert(actorBytes[k] == expectedActor[k], "sync object byte " + k);
        var actorBack = (LegacySyncObject)LegacySerialization.ToObjects(actorBytes)[0];
        Assert(actorBack.Id == 5 && (int)actorBack[LegacyActorField.Cmid] == 9 && (string)actorBack[LegacyActorField.PlayerName] == "Ab", "sync object round trip");
        var actorList = LegacySerialization.ToObjects(LegacySerialization.ToBytes(new System.Collections.Generic.List<LegacySyncObject> { actor, actor }));
        Assert(actorList.Length == 1 && ((System.Collections.Generic.List<LegacySyncObject>)actorList[0]).Count == 2, "sync object list");

        // RoomMetaData from PhotonGameJoin: small strings, 3 bytes, CmuneRoomID (int + ip + port).
        var room = new System.Collections.Generic.List<byte> { 0x28, 1, (byte)'C', 0, 0, 16, 2, 0, 0x58,0,0,0, 75,119,148,51, 0xBF,0x13 };
        var meta = (LegacyRoomMetaData)LegacySerialization.ToObjects(room.ToArray())[0];
        Assert(meta.Name == "C" && meta.MaxPlayers == 16 && meta.RoomId.Number == 88 && meta.RoomId.Server == "75.119.148.51:5055", "room metadata");
        Assert(meta.RoomId.ToBytes().Length == LegacyRoomId.Size, "room id size");
        Assert(LegacyEventCode.NetworkMessage == 0, "remote calls use event code 0");

        // Exact bytes a real 4.3.10 client sends to join the game-list lobby (room 66). The wire is
        // little-endian (DefaultByteConverter.CheckEndian); reading it big-endian made 66 look like
        // 0x42000000 and every lobby join was refused as a "PC match".
        var lobbyJoin = new byte[] { 0x67, 0, 0, 0, 0, 0, 0x42,0,0,0, 75,119,148,51, 0x23,0x14,
            0,0, 0,0,0,0, 0,0,0,0, 0,0, 0,0,0,0, 0, 0, 0,0,0,0 };
        var lobbyMeta = (LegacyRoomMetaData)LegacySerialization.ToObjects(lobbyJoin)[0];
        Assert(lobbyMeta.RoomId.Number == LegacyGameMetaData.LobbyRoomNumber, "lobby join room number from client bytes");
        var loadBytes = LegacySerialization.ToBytes(new LegacyServerLoadData { PlayersConnected = 2, MaxPlayerCount = 100 });
        Assert(loadBytes[1] == 2 && loadBytes[2] == 0, "server load is little-endian");

        // Game list: List<RoomData> (6F) of GameMetaData (67) built from PC rooms.
        var game = new LegacyGameMetaData {
            Name = "PC TDM", MaxPlayers = 16, InPlayers = 3,
            RoomId = new LegacyRoomId { Number = 1, Address = new byte[] { 75, 119, 148, 51, 0x23, 0x14 } },
            MapId = 13, TimeLimitSeconds = 600, SplatLimit = 50, GameMode = LegacyGameMetaData.ModeTeamDeathMatch, LevelMax = 80, PlayersInGame = 3
        };
        var gameBytes = LegacySerialization.ToBytes(new System.Collections.Generic.List<LegacyRoomMetaData> { game });
        Assert(gameBytes[0] == 0x6F && gameBytes[1] == 1 && gameBytes[2] == 0 && gameBytes[3] == 0x67, "room list header");
        Assert(gameBytes.Length == 53, "game metadata length");
        var gameBack = (LegacyGameMetaData)((System.Collections.Generic.List<LegacyRoomMetaData>)LegacySerialization.ToObjects(gameBytes)[0])[0];
        Assert(gameBack.MapId == 13 && gameBack.GameMode == 100 && gameBack.RoomId.Server == "75.119.148.51:5155" && gameBack.SplatLimit == 50, "game metadata round trip");

        // In-match types (UberStrikeByteConverter): CharacterInfo as a SyncObject with typed fields.
        var character = new LegacySyncObject { Id = 3 };
        character[LegacyCharField.Cmid] = 42;
        character[LegacyCharField.Ping] = (ushort)30;
        character[LegacyCharField.Health] = (short)100;
        character[LegacyCharField.TeamID] = (byte)1;
        character[LegacyCharField.SkinColor] = new LegacyColor(1, 0.5f, 0.25f);
        character[LegacyCharField.Stats] = new LegacyStatsInfo { Kills = 5, Deaths = 2 };
        character[LegacyCharField.Armor] = new LegacyArmorInfo { ArmorPoints = 50, ArmorPointCapacity = 100 };
        character[LegacyCharField.Weapons] = new LegacyWeaponInfo();
        character[LegacyCharField.Gear] = new System.Collections.Generic.List<int> { 1, 2, 3, 4, 5, 6, 7 };
        var charBytes = LegacySerialization.ToBytes(new LegacyCharacterInfo(character));
        Assert(charBytes[0] == 101, "character info type");
        var charBack = (LegacyCharacterInfo)LegacySerialization.ToObjects(charBytes)[0];
        Assert(charBack.Sync.Id == 3 && (int)charBack.Sync[LegacyCharField.Cmid] == 42
            && (ushort)charBack.Sync[LegacyCharField.Ping] == 30 && (short)charBack.Sync[LegacyCharField.Health] == 100
            && ((LegacyStatsInfo)charBack.Sync[LegacyCharField.Stats]).Kills == 5
            && ((LegacyArmorInfo)charBack.Sync[LegacyCharField.Armor]).ArmorPointCapacity == 100
            && ((LegacyWeaponInfo)charBack.Sync[LegacyCharField.Weapons]).ItemIds.Count == 5
            && ((System.Collections.Generic.List<int>)charBack.Sync[LegacyCharField.Gear]).Count == 7, "character info round trip");

        // ShortVector3: shorts of x*10, y*100, z*10 (12.3, 4.56, -7.8 -> 123, 456, -78).
        var sv = new System.Collections.Generic.List<byte>();
        new LegacyShortVector3(new LegacyVector3(12.3f, 4.56f, -7.8f)).WriteRaw(sv);
        Assert(sv[0] == 123 && sv[1] == 0 && sv[2] == 0xC8 && sv[3] == 0x01 && sv[4] == 0xB2 && sv[5] == 0xFF, "short vector layout");

        // A 4.3.10 position update (83): [int actor][ShortVector3][int time] inside a List<byte> (0x0F).
        var position = LegacySerialization.ToObjects(new byte[] { 0x0F, 14,0,0,0, 1,0,0,0, 123,0, 0xC8,0x01, 0xB2,0xFF, 9,0,0,0 });
        Assert(position[0] is byte[] raw && raw.Length == 14, "position update payload");

        // DamageEvent (107) and Vector3 lists (0x23) round trip.
        var damage = new LegacyDamageEvent { BodyPartFlag = 2 };
        damage.Damage[10] = 25;
        var damageBack = (LegacyDamageEvent)LegacySerialization.ToObjects(LegacySerialization.ToBytes(damage))[0];
        Assert(damageBack.BodyPartFlag == 2 && damageBack.Damage[10] == 25, "damage event round trip");
        var vectors = LegacySerialization.ToObjects(LegacySerialization.ToBytes(new System.Collections.Generic.List<LegacyVector3> { new LegacyVector3(1, 2, 3) }));
        Assert(((System.Collections.Generic.List<LegacyVector3>)vectors[0])[0].Y == 2, "vector3 list round trip");

        Console.WriteLine("Android 4.3.10 legacy codec smoke tests passed.");
        return 0;
    }
}
