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

        Console.WriteLine("Android 4.3.10 legacy codec smoke tests passed.");
        return 0;
    }
}
