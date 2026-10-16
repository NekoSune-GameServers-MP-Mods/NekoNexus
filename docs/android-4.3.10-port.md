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
