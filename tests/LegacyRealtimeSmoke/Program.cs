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
        Assert(values[0] is int && (int)values[0] == 1234, "big-endian int");
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
        try { LegacySerialization.ToObjects(new byte[] { LegacySerialization.Type.String, 0, 5, 65 }); }
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
        var expectedActor = new byte[] { 0x30, 0,0,0,0x43, 0,0,0,5, 0x06,0,0,0,9, 0x0C,0,2,(byte)'A',0,(byte)'b',0, 0x01,3, 0x06,0,0,0,9 };
        Assert(actorBytes.Length == expectedActor.Length, "sync object length");
        for (var k = 0; k < expectedActor.Length; k++) Assert(actorBytes[k] == expectedActor[k], "sync object byte " + k);
        var actorBack = (LegacySyncObject)LegacySerialization.ToObjects(actorBytes)[0];
        Assert(actorBack.Id == 5 && (int)actorBack[LegacyActorField.Cmid] == 9 && (string)actorBack[LegacyActorField.PlayerName] == "Ab", "sync object round trip");
        var actorList = LegacySerialization.ToObjects(LegacySerialization.ToBytes(new System.Collections.Generic.List<LegacySyncObject> { actor, actor }));
        Assert(actorList.Length == 1 && ((System.Collections.Generic.List<LegacySyncObject>)actorList[0]).Count == 2, "sync object list");

        // RoomMetaData from PhotonGameJoin: small strings, 3 bytes, CmuneRoomID (int + ip + port).
        var room = new System.Collections.Generic.List<byte> { 0x28, 1, (byte)'C', 0, 0, 16, 2, 0, 0,0,0,0x58, 75,119,148,51, 0x13,0xBF };
        var meta = (LegacyRoomMetaData)LegacySerialization.ToObjects(room.ToArray())[0];
        Assert(meta.Name == "C" && meta.MaxPlayers == 16 && meta.RoomId.Number == 88 && meta.RoomId.Server == "75.119.148.51:5055", "room metadata");
        Assert(meta.RoomId.ToBytes().Length == LegacyRoomId.Size, "room id size");
        Assert(LegacyEventCode.NetworkMessage == 0, "remote calls use event code 0");

        Console.WriteLine("Android 4.3.10 legacy codec smoke tests passed.");
        return 0;
    }
}
