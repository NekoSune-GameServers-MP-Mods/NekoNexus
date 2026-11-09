# NekoNexus Android maps (4.3.10 Android client)

The Android client (APK 4.3.10, Unity **4.0.0f7** player) cannot load the PC map bundles.
The `pc-maps-to-android` tools convert them into `<Scene>-Android.unity3d` bundles without
Unity. They need Python 3.11+, `UnityPy`, `ffmpeg` (with libmp3lame) on `PATH`, and the
NekoNexus Android APK (`NekoNexus-Android-fixed.apk`).

> You must own a copy of UberStrike. The maps are converted from *your* game files.

## Building the maps

**Built-in maps** (Steam 4.7.1 client, Unity 4.6 — the `levelN` files in `UberStrike_Data`):

```powershell
# all maps
py pc46_to_android.py "C:\Program Files (x86)\Steam\steamapps\common\UberStrike\UberStrike_Data" `
   NekoNexus-Android-fixed.apk <out-dir>
# one or more maps only (level5 = CuberSpace; scene i is file level(i-1) in BuildSettings)
py pc46_to_android.py "<...>\UberStrike_Data" NekoNexus-Android-fixed.apk <out-dir> --level level5
```

**Bundle maps** (`UberStrike_Data\Maps\*.unity3d` — SpaceCity, SpacePortAlpha, UberZone; these
are already Unity 4.0.0f7, so they are only retargeted):

```powershell
py retarget_map_android.py NekoNexus-Android-fixed.apk `
   "<...>\UberStrike_Data\Maps\UberZone.unity3d" <out-dir>\UberZone-Android.unity3d
```

Both tools re-read and verify every object they write.

## Deploying

1. Upload the `*-Android.unity3d` files to `UberStrike/Maps/4.3.10/` on the map/file server.
   The phone requests `<SceneName>-Android.unity3d`. *The Hangar* is `TheBunker` on 4.3.10.
2. A map only appears in the Android map list if its `Maps` row has a `"4.3.10"` key in
   `FileName` (`GetMaps` filters on it). For maps that only had a `"4.7.1"` entry:

   ```sql
   UPDATE Maps SET FileName = JSON_SET(FileName, '$."4.3.10"', 'ApexTwin-Android.unity3d') WHERE MapId = 3;
   ```

   The phone loads maps by scene name, so the value only has to exist.
3. **Players must clear the app cache after a map is replaced** (Settings → Apps →
   UberStrike → Storage → Clear cache). The client uses `WWW.LoadFromCacheOrDownload` with a
   fixed version, so it keeps an old copy forever.

## What the conversion changes

- **No type trees.** Android player files carry none. The 4.0 mobile player logs
  `Serialized file contains typetree's but the target can't use them` and then crashes
  (SIGSEGV in `libunity.so`) on files that have them. Object data is written in the exact
  4.0.0f7 native layout instead.
- **Shaders** are replaced by the APK's GLES-compiled copies. Shaders the APK lacks are
  mapped to a close APK shader or a fixed-function fallback (`SHADER_ALIASES` /
  `FALLBACK_SHADERS` in `pc46_to_android.py`). For example, `Terrain/Diffuse` becomes
  `Nature/Terrain/Diffuse`. A shader still reported as `MISSING (kept PC copy)` renders pink
  on the phone.
- **Audio**: Ogg Vorbis is re-encoded to MP3.
- **PC-only scripts** with no 4.0 layout in the APK are disabled and unlinked. The game logs
  `The referenced script on this Behaviour is missing!` for each one; this is expected.

## Known limitations

- Water (`FX/Water4`, `CMune/Mobile/Water`) is drawn as a flat tinted surface, with no
  reflections or waves.
- Disabled PC-only scripts include effects (Bloom, fog, sun shafts) and ambient sound
  (`SoundArea`), as well as a few gameplay scripts: `DoorBehaviour`/`DoorTrigger`,
  `TempleTeleporter`, `WaterGate`, `SecretTrigger`, `ShipBob`. Those features do not work on
  Android.
