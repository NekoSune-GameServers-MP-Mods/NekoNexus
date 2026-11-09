# Android 4.3.10 client port

The repository's Android path targets the original Unity Mono 4.3.10 APK. It reuses the
APK's own Unity assets, redirects its legacy 1.0.2 web-service/content URLs, and replaces
its Photon client assembly with the NekoNexus LiteNetLib transport. The APK is not included
in the repository.

## Build a local unsigned APK

Build the managed transport first:

```powershell
dotnet build shims/Photon3Unity3D.Shim/Photon3Unity3D.Shim.csproj -c Release
```

Then run the patcher with the address players can reach. For a Compose host with ports 8080
and 8081 open:

```powershell
python packages/Client/Android/patch_android_apk.py `
  "C:\path\to\UberStrike-4.3.10-Android.apk" `
  ".release\android\UberStrike-NekoNexus-unsigned.apk" `
  --web-service "http://SERVER_HOST:8080/1.0.2/" `
  --content "http://SERVER_HOST:8081/UberStrike/" `
  --content-root "server-data\android-content"
```

The script keeps the source APK unchanged. It updates the embedded `UberStrike.xml`, replaces
`Photon3Unity3D.dll`, bypasses the old NVIDIA Tegra-only startup check, strips the stale APK
signatures, and copies the APK's Android item bundle into ignored `server-data` for hosting.
The output is unsigned as requested; Android needs it signed before it can be installed.

## Server side

The legacy `1.0.2` API must be built and deployed with this source. The webservices container
mounts `server-data/android-content` at the old client's fixed
`/UberStrike/Items/4.3.10/` URL. The existing item manifest in `packages/ws/wwwroot` declares
the same bundle hash that the APK carries. The legacy API now allows the Android channel and
returns server discovery, server name, liveness, and client-IP responses. Email login remains
the Android account path; the APK's original login screen is retained.

The API and file URLs must actually route to the NekoNexus web-services and file-server
listeners. A website homepage or GitHub Pages URL is not a usable API endpoint. The public
`nekonexus.nekosunevr.co.uk` URL currently returned the site homepage for the legacy API and
item-manifest paths when checked, so use a reachable host/port until its reverse-proxy routes
are configured.

This is a compatibility port, not a validated release: the 2012 Unity runtime and shaders may
still fail on modern Android GPUs/versions, and realtime protocol behavior needs a device test.

## Maps

The APK only contains the Menu and Tutorial scenes. Every other map is downloaded at match
load from `<ContentBaseUrl>Maps/4.3.10/<FileName>`, where `FileName` comes from `GetMaps`.
Unity 4 asset bundles only load on the platform they were built for, so the PC `-HD.unity3d`
maps do not work here: the files must be **Android builds made with Unity 4.0**.

Put them in `server-data/android-content/UberStrike/Maps/4.3.10/` (served at
`http://SERVER_HOST:8081/UberStrike/Maps/4.3.10/`). When the client asks for Android maps,
`GetMaps` only lists maps that have a file there, matched by map name:
`AqualabResearchHub.unity3d` or `AqualabResearchHub-<anything>.unity3d` (case-insensitive;
a name containing `Android` wins over others). The folder is re-read on every request, so new
maps appear without a restart. The web-services log prints how many Android maps were found.

## Fixing an existing APK (no dotnet needed)

`packages/Client/Android/fix_android_apk.py` applies the Android fixes to an APK you already
built, using only Python 3.8+ and a JDK (or Android SDK `apksigner`) for signing:

```powershell
python packages/Client/Android/fix_android_apk.py `
  "UberStrike-NekoNexus.apk" "UberStrike-NekoNexus-fixed.apk" `
  --web-service "http://SERVER_HOST:8080/1.0.2/" `
  --content "http://SERVER_HOST:8081/UberStrike/"
```

It sets the URLs (both optional), patches the NekoNexus Photon shim inside the APK
(`DispatchIncomingCommands`/`SendOutgoingCommands` return `false`, `RoundTripTime` is never 0;
the same fixes are in `shims/Photon3Unity3D.Shim/PhotonPeer.cs` for new builds), strips old
signatures, aligns stored entries and signs with `nekonexus-android.jks` (created on first use;
keep it so later builds install as updates). It only touches the exact shim method bodies it
recognises and is safe to run again on a fixed APK.

Why the shim fixes matter on Android:

* The 4.3.10 client runs `while (peer.DispatchIncomingCommands()) {}` every frame. With the shim
  returning `true` the main thread never left that loop once a realtime connection started: the
  screen froze after login (tutorial or name screen) and Back/Home produced an ANR.
* The client's server check reads the latency right after connecting and treats 0 as
  unreachable. LiteNetLib's `Ping` is 0 for about a second after connecting, so every server was
  hidden.
* On the server side, 4.3.10 clients also need the legacy protocol layer described in
  `docs/android-4.3.10-realtime-protocol.md`.

### Android game list: rebuild the Photon shim

The Android room list needs Hashtable support in the transport shim. Build it and swap it into the
APK (the fixer then skips its byte patches; the shim source already has those fixes):

```
dotnet build shims/Photon3Unity3D.Shim/Photon3Unity3D.Shim.csproj -c Release
python packages/Client/Android/fix_android_apk.py NekoNexus.apk NekoNexus-crossplay.apk ^
    --web-service http://HOST:8080/1.0.2/ --content http://HOST:8081/UberStrike/ ^
    --photon-shim shims/Photon3Unity3D.Shim/bin/Release/net35/Photon3Unity3D.dll
```
