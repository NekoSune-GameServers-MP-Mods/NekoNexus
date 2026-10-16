#!/usr/bin/env python3
"""Prepare a local UberStrike 4.3.10 Android APK for a NekoNexus test server.

This edits only the APK configuration and managed Photon transport. Unity assets stay
byte-for-byte unchanged in the APK. The extracted Android item bundle can optionally be
placed in a local NekoNexus wwwroot so the legacy client can download it from its new URL.

The output is deliberately unsigned. Android requires APK signing before installation.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


CONFIG_ENTRY = "assets/UberStrike.xml"
PHOTON_ENTRY = "assets/bin/Data/Managed/Photon3Unity3D.dll"
GAME_ASSEMBLY_ENTRY = "assets/bin/Data/Managed/Assembly-CSharp.dll"
ANDROID_UNITY_ENTRY = "assets/libs/armeabi-v7a/libunity.so"
ANDROID_MONO_ENTRY = "assets/libs/armeabi-v7a/libmono.so"
APK_UNITY_ENTRY = "lib/armeabi-v7a/libunity.so"
APK_MONO_ENTRY = "lib/armeabi-v7a/libmono.so"
CONTENT_PREFIX = "UberStrike/Items/4.3.10/"


def _read_length8(data: bytes, offset: int) -> tuple[int, int]:
    first = data[offset]
    if first & 0x80:
        return ((first & 0x7F) << 8) | data[offset + 1], 2
    return first, 1


def _read_length16(data: bytes, offset: int) -> tuple[int, int]:
    first = struct.unpack_from("<H", data, offset)[0]
    if first & 0x8000:
        second = struct.unpack_from("<H", data, offset + 2)[0]
        return ((first & 0x7FFF) << 16) | second, 4
    return first, 2


def patch_android_manifest_compatibility(
    manifest: bytes, target_sdk: int, version_code: int, version_name: str
) -> bytes:
    """Allow phone screens, set the target API, and increment the Android package version."""
    patched = bytearray(manifest)
    strings: list[str] = []
    string_data_offsets: list[int] = []
    small_screen_found = False
    target_sdk_found = False
    version_code_found = False
    version_name_found = False
    offset = 8
    while offset + 8 <= len(patched):
        chunk_type, header_size, chunk_size = struct.unpack_from("<HHI", patched, offset)
        if chunk_size < header_size or offset + chunk_size > len(patched):
            raise ValueError("invalid Android binary XML chunk")

        if chunk_type == 0x0001:  # RES_STRING_POOL_TYPE
            string_count, _, flags, strings_start, _ = struct.unpack_from("<IIIII", patched, offset + 8)
            utf8 = bool(flags & 0x100)
            offsets_start = offset + header_size
            string_data_start = offset + strings_start
            for index in range(string_count):
                string_offset = string_data_start + struct.unpack_from("<I", patched, offsets_start + index * 4)[0]
                if utf8:
                    _, length_bytes = _read_length8(patched, string_offset)
                    byte_length, byte_length_bytes = _read_length8(patched, string_offset + length_bytes)
                    start = string_offset + length_bytes + byte_length_bytes
                    value = bytes(patched[start : start + byte_length]).decode("utf-8", "replace")
                    string_data_offsets.append(start)
                else:
                    char_length, length_bytes = _read_length16(patched, string_offset)
                    start = string_offset + length_bytes
                    value = bytes(patched[start : start + char_length * 2]).decode("utf-16le", "replace")
                    string_data_offsets.append(start)
                strings.append(value)

        elif chunk_type == 0x0102:  # RES_XML_START_ELEMENT_TYPE
            name_index = struct.unpack_from("<I", patched, offset + 20)[0]
            tag_name = strings[name_index] if name_index < len(strings) else ""
            if tag_name in ("manifest", "supports-screens", "uses-sdk"):
                attribute_start, attribute_size, attribute_count = struct.unpack_from("<HHH", patched, offset + 24)
                found: set[str] = set()
                for index in range(attribute_count):
                    attribute = offset + 16 + attribute_start + index * attribute_size
                    attribute_name_index = struct.unpack_from("<I", patched, attribute + 4)[0]
                    attribute_name = strings[attribute_name_index] if attribute_name_index < len(strings) else ""
                    if tag_name == "manifest" and attribute_name == "versionCode":
                        patched[attribute + 15] = 0x10
                        struct.pack_into("<I", patched, attribute + 16, version_code)
                        version_code_found = True
                    elif tag_name == "manifest" and attribute_name == "versionName":
                        if patched[attribute + 15] != 0x03:
                            raise ValueError("Android manifest versionName is not a string")
                        string_index = struct.unpack_from("<I", patched, attribute + 16)[0]
                        if string_index >= len(strings):
                            raise ValueError("Android manifest versionName index is invalid")
                        old_name = strings[string_index]
                        string_encoding = "utf-8" if utf8 else "utf-16le"
                        old_bytes = old_name.encode(string_encoding)
                        new_bytes = version_name.encode(string_encoding)
                        if len(old_bytes) != len(new_bytes):
                            raise ValueError("new versionName must have the same UTF-8 byte length as the existing name")
                        patched[string_data_offsets[string_index] : string_data_offsets[string_index] + len(new_bytes)] = new_bytes
                        strings[string_index] = version_name
                        version_name_found = True
                    elif tag_name == "supports-screens" and attribute_name == "smallScreens":
                        # TYPE_INT_BOOLEAN, true
                        patched[attribute + 15] = 0x12
                        struct.pack_into("<I", patched, attribute + 16, 0xFFFFFFFF)
                        found.add(attribute_name)
                    elif tag_name == "supports-screens" and attribute_name == "requiresSmallestWidthDp":
                        # A 1 dp minimum removes the original tablet-only install filter.
                        patched[attribute + 15] = 0x10
                        struct.pack_into("<I", patched, attribute + 16, 1)
                        found.add(attribute_name)
                    elif tag_name == "uses-sdk" and attribute_name == "targetSdkVersion":
                        # Target API is selectable because the original Unity libraries use
                        # text relocations rejected when the effective target API is 23+.
                        patched[attribute + 15] = 0x10
                        struct.pack_into("<I", patched, attribute + 16, target_sdk)
                        found.add(attribute_name)
                if tag_name == "supports-screens":
                    small_screen_found = found == {"smallScreens", "requiresSmallestWidthDp"}
                elif tag_name == "uses-sdk":
                    target_sdk_found = "targetSdkVersion" in found
                if small_screen_found and target_sdk_found and version_code_found and version_name_found:
                    return bytes(patched)

        offset += chunk_size

    raise ValueError("AndroidManifest.xml did not contain the expected small-screen restrictions")


def patch_unity_armv7_text_relocation(library: bytes) -> bytes:
    """Make the one known Unity 4.3.10 ARMv7 text relocation position-independent."""
    patched = bytearray(library)
    if len(patched) < 52 or patched[:4] != b"\x7fELF" or patched[4:6] != b"\x01\x01":
        raise ValueError("Unity ARM library is not a little-endian ELF32 file")
    if struct.unpack_from("<H", patched, 18)[0] != 40:
        raise ValueError("Unity native library is not for ARM")

    phoff = struct.unpack_from("<I", patched, 28)[0]
    phentsize, phnum = struct.unpack_from("<HH", patched, 42)
    if phentsize < 32 or phoff + phentsize * phnum > len(patched):
        raise ValueError("Unity ARM library has an invalid program header table")

    segments = [
        struct.unpack_from("<IIIIIIII", patched, phoff + index * phentsize)
        for index in range(phnum)
    ]

    def vaddr_to_offset(address: int, size: int) -> int:
        for kind, offset, vaddr, _, file_size, _, _, _ in segments:
            if kind == 1 and vaddr <= address and address + size <= vaddr + file_size:
                return offset + address - vaddr
        raise ValueError(f"Unity ARM library address 0x{address:x} is not file-backed")

    dynamic = next((segment for segment in segments if segment[0] == 2), None)
    if dynamic is None or dynamic[4] % 8:
        raise ValueError("Unity ARM library has no valid dynamic section")
    dyn_offset, dyn_size = dynamic[1], dynamic[4]
    dynamic_entries: list[tuple[int, int]] = []
    for offset in range(dyn_offset, dyn_offset + dyn_size, 8):
        tag, value = struct.unpack_from("<II", patched, offset)
        dynamic_entries.append((tag, value))
        if tag == 0:
            break
    if not dynamic_entries or dynamic_entries[-1][0] != 0:
        raise ValueError("Unity ARM dynamic section has no terminator")

    values: dict[int, int] = {}
    for tag, value in dynamic_entries:
        values.setdefault(tag, value)
    if 22 not in values:  # DT_TEXTREL
        return bytes(patched)
    if not all(tag in values for tag in (17, 18, 19, 0x6FFFFFFA)):
        raise ValueError("Unity ARM dynamic section is missing REL metadata")
    if values[19] != 8 or values[18] % 8:
        raise ValueError("Unity ARM REL table has an unexpected entry size")

    rel_offset = vaddr_to_offset(values[17], values[18])
    rel_count = values[18] // 8
    text_relocations: list[tuple[int, int, int]] = []
    for index in range(rel_count):
        entry_offset = rel_offset + index * 8
        address, info = struct.unpack_from("<II", patched, entry_offset)
        relocation_type = info & 0xFF
        if relocation_type != 0 and any(
            kind == 1 and flags & 1 and vaddr <= address < vaddr + mem_size
            for kind, _, vaddr, _, _, mem_size, flags, _ in segments
        ):
            text_relocations.append((entry_offset, address, info))

    # Unity 4.3.10 has two text relocations in its startup veneer: a relative pointer to
    # __data_start and an absolute pointer to __cxa_atexit. Replace both with PC-relative
    # code so Android 14 can load the player at target API 26.
    data_start_pointer = 0x4D3BC
    cxa_atexit_pointer = 0x4D3D4
    if {address for _, address, _ in text_relocations} != {
        data_start_pointer,
        cxa_atexit_pointer,
    }:
        raise ValueError(
            "Unity ARM executable relocations differ from the known 4.3.10 startup veneer; "
            f"found {len(text_relocations)} executable relocations"
        )
    data_entry = next(entry for entry in text_relocations if entry[1] == data_start_pointer)
    cxa_entry = next(entry for entry in text_relocations if entry[1] == cxa_atexit_pointer)
    if data_entry[2] != 23 or cxa_entry[2] != 0x54B02:
        raise ValueError("Unity ARM startup relocations have unexpected types or symbols")
    code_offset = vaddr_to_offset(data_start_pointer - 12, 0x28)
    expected = bytes.fromhex(
        "04008fe2 000090e5 cefbffea 00607700 "
        "10b5 034b 0021 7b44 1a68 024b 9847 10bd 368c7200 00000000"
    )
    if patched[code_offset : code_offset + len(expected)] != expected:
        raise ValueError("Unity ARM startup veneer does not match the known 4.3.10 code")

    # ARM veneer: load the fixed offset literal and add the second instruction's PC.
    # This keeps the original result, load-bias+0x776000, without an absolute relocation.
    relative_delta = 0x776000 - data_start_pointer
    patched[code_offset : code_offset + 12] = struct.pack(
        "<III", 0xE59F0004, 0xE08F0000, 0xEAFFFBCE
    )
    struct.pack_into("<I", patched, code_offset + 12, relative_delta)

    # The following Thumb constructor calls __cxa_atexit through a relocated function
    # pointer. Replace LDR+BLX with a direct Thumb BLX to its checked ARM PLT entry.
    plt_target = 0x4CB14
    blx_address = cxa_atexit_pointer - 10
    pc = (blx_address + 4) & ~3
    branch_delta = plt_target - pc
    if branch_delta % 4 or not -(1 << 24) <= branch_delta < (1 << 24):
        raise ValueError("Unity __cxa_atexit PLT entry is outside Thumb BLX range")
    encoded = branch_delta & 0x1FFFFFF
    sign = (encoded >> 24) & 1
    i1 = (encoded >> 23) & 1
    i2 = (encoded >> 22) & 1
    j1 = (~(i1 ^ sign)) & 1
    j2 = (~(i2 ^ sign)) & 1
    first = 0xF000 | (sign << 10) | ((encoded >> 12) & 0x3FF)
    second = 0xC000 | (j1 << 13) | (j2 << 11) | (((encoded >> 2) & 0x3FF) << 1)
    thumb_blx_offset = vaddr_to_offset(blx_address, 4)
    struct.pack_into("<HH", patched, thumb_blx_offset, first, second)

    # Mark both replaced relocations as R_ARM_NONE. The relative one is the first
    # DT_RELCOUNT entry, so lower that prefix length by one.
    for entry_offset, _, _ in text_relocations:
        struct.pack_into("<I", patched, entry_offset + 4, 0)

    # The removed relocation is the first entry in the DT_RELCOUNT prefix.
    relcount = values[0x6FFFFFFA]
    if relcount <= 0:
        raise ValueError("Unity ARM RELCOUNT is invalid")
    dynamic_entries = [
        (tag, value - 1 if tag == 0x6FFFFFFA else value)
        for tag, value in dynamic_entries
    ]

    # Remove DT_TEXTREL without leaving a terminator before the remaining tags.
    textrel_index = next(index for index, (tag, _) in enumerate(dynamic_entries) if tag == 22)
    dynamic_entries.pop(textrel_index)
    if any(tag == 30 and value & 4 for tag, value in dynamic_entries):  # DF_TEXTREL
        dynamic_entries = [
            (tag, value & ~4 if tag == 30 else value)
            for tag, value in dynamic_entries
        ]
    dynamic_entries = [entry for entry in dynamic_entries if entry[0] != 0]
    dynamic_entries.append((0, 0))
    if len(dynamic_entries) * 8 > dyn_size:
        raise ValueError("Unity ARM dynamic section no longer fits")
    patched[dyn_offset : dyn_offset + dyn_size] = b"\0" * dyn_size
    for index, entry in enumerate(dynamic_entries):
        struct.pack_into("<II", patched, dyn_offset + index * 8, *entry)
    return bytes(patched)


def normalized_base_url(value: str, label: str) -> str:
    value = value.strip()
    if not re.match(r"^https?://", value, flags=re.IGNORECASE):
        raise argparse.ArgumentTypeError(f"{label} must start with http:// or https://")
    return value.rstrip("/") + "/"


def is_signature_file(name: str) -> bool:
    if not name.upper().startswith("META-INF/"):
        return False
    leaf = name.rsplit("/", 1)[-1].upper()
    return leaf == "MANIFEST.MF" or leaf.endswith((".SF", ".RSA", ".DSA", ".EC")) or leaf.startswith("SIG-")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=pathlib.Path, help="source UberStrike 4.3.10 Android APK")
    parser.add_argument("output", type=pathlib.Path, help="destination for unsigned patched APK")
    parser.add_argument("--web-service", required=True, help="base URL ending in /1.0.2/")
    parser.add_argument("--content", required=True, help="content base URL ending in /UberStrike/")
    parser.add_argument(
        "--target-sdk",
        type=int,
        choices=(22, 26),
        default=26,
        help="target API; 22 enables legacy text-relocation loading but needs ADB bypass on Android 14+",
    )
    parser.add_argument("--version-code", type=int, default=4, help="Android package version code")
    parser.add_argument("--version-name", default="4.3.11", help="Android package version name")
    parser.add_argument(
        "--content-root",
        type=pathlib.Path,
        help="optional NekoNexus wwwroot directory to receive the APK's Android item bundle",
    )
    parser.add_argument(
        "--photon-shim",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parents[3]
        / "shims/Photon3Unity3D.Shim/bin/Release/net35/Photon3Unity3D.dll",
        help="built NekoNexus Photon3Unity3D shim (defaults to this repo's Release build)",
    )
    args = parser.parse_args()

    apk = args.apk.resolve()
    output = args.output.resolve()
    shim = args.photon_shim.resolve()
    if not apk.is_file():
        parser.error(f"APK not found: {apk}")
    if not shim.is_file():
        parser.error(f"Photon shim not found: {shim}; build shims/Photon3Unity3D.Shim first")
    if apk == output:
        parser.error("output must be a different path; the source APK is kept unchanged")

    web_service_url = normalized_base_url(args.web_service, "--web-service")
    content_url = normalized_base_url(args.content, "--content")
    output.parent.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(apk, "r") as source:
        names = set(source.namelist())
        required_entries = {
            CONFIG_ENTRY,
            PHOTON_ENTRY,
            GAME_ASSEMBLY_ENTRY,
            ANDROID_UNITY_ENTRY,
            ANDROID_MONO_ENTRY,
            APK_UNITY_ENTRY,
            APK_MONO_ENTRY,
        }
        if not required_entries.issubset(names):
            parser.error("APK does not contain the expected UberStrike 4.3.10 Unity files")

        try:
            root = ET.fromstring(source.read(CONFIG_ENTRY))
        except ET.ParseError as error:
            parser.error(f"could not parse {CONFIG_ENTRY}: {error}")

        application = root.find("Application")
        if application is None:
            parser.error(f"{CONFIG_ENTRY} has no Application element")
        application.set("WebServiceBaseUrl", web_service_url)
        application.set("ContentBaseUrl", content_url)
        config_bytes = ET.tostring(root, encoding="us-ascii", xml_declaration=True)
        try:
            manifest_bytes = patch_android_manifest_compatibility(
                source.read("AndroidManifest.xml"), args.target_sdk, args.version_code, args.version_name
            )
        except ValueError as error:
            parser.error(str(error))
        shim_bytes = shim.read_bytes()
        try:
            android_unity_bytes = patch_unity_armv7_text_relocation(
                source.read(ANDROID_UNITY_ENTRY)
            )
        except ValueError as error:
            parser.error(f"could not make Unity ARM library Android-compatible: {error}")
        android_mono_bytes = source.read(ANDROID_MONO_ENTRY)

        # Patch a temporary copy only. The input APK is never opened for writing.
        with tempfile.TemporaryDirectory(prefix="nekonexus-android-") as temp_dir:
            temp_path = pathlib.Path(temp_dir)
            assembly_path = temp_path / "Assembly-CSharp.dll"
            for entry in names:
                if entry.startswith("assets/bin/Data/Managed/") and entry.lower().endswith(".dll"):
                    (temp_path / entry.rsplit("/", 1)[-1]).write_bytes(source.read(entry))
            repo_root = pathlib.Path(__file__).resolve().parents[3]
            patcher_project = repo_root / "packages/Client/Android/AndroidAssemblyPatcher/AndroidAssemblyPatcher.csproj"
            subprocess.run(
                ["dotnet", "run", "--project", str(patcher_project), "--configuration", "Release", "--", str(assembly_path)],
                cwd=repo_root,
                check=True,
            )
            assembly_bytes = assembly_path.read_bytes()

        extracted_bundle: tuple[str, bytes] | None = None
        if args.content_root:
            bundle_names = [
                name
                for name in names
                if name.startswith("assets/") and name.endswith("-Android.unity3d")
            ]
            if len(bundle_names) != 1:
                parser.error(f"expected one Android item bundle in APK, found {len(bundle_names)}")
            bundle_name = bundle_names[0]
            extracted_bundle = (bundle_name.rsplit("/", 1)[-1], source.read(bundle_name))

        with zipfile.ZipFile(output, "w") as patched:
            for info in source.infolist():
                if is_signature_file(info.filename):
                    continue
                if info.filename == "AndroidManifest.xml":
                    payload = manifest_bytes
                elif info.filename == CONFIG_ENTRY:
                    payload = config_bytes
                elif info.filename == PHOTON_ENTRY:
                    payload = shim_bytes
                elif info.filename == GAME_ASSEMBLY_ENTRY:
                    payload = assembly_bytes
                elif info.filename == APK_UNITY_ENTRY:
                    # The APK's small proxy .so crashes in JNI_OnLoad on current Android.
                    # Install the full Unity player library carried in assets/libs instead.
                    payload = android_unity_bytes
                elif info.filename == APK_MONO_ENTRY:
                    payload = android_mono_bytes
                else:
                    payload = source.read(info.filename)
                patched.writestr(info, payload)

    if extracted_bundle:
        filename, payload = extracted_bundle
        content_path = args.content_root.resolve() / CONTENT_PREFIX / filename
        content_path.parent.mkdir(parents=True, exist_ok=True)
        content_path.write_bytes(payload)
        print(f"Android item bundle copied to: {content_path}")

    print(f"Unsigned NekoNexus APK written to: {output}")
    print(f"Web service: {web_service_url}")
    print(f"Content:     {content_url}")
    print("APK signatures were removed. Sign the APK before installing it on Android.")
    print("Enabled small-screen support and removed the original 600 dp tablet requirement.")
    print("Installed the APK's full Unity/Mono native libraries instead of its proxy loaders.")
    print("Made the legacy Android clan lookup fail open so it cannot stall post-login loading.")
    print(f"Set target API to {args.target_sdk}; minimum API is unchanged.")
    print(f"Set Android version to {args.version_name} (code {args.version_code}).")
    if args.target_sdk < 23:
        print("Android 14+ requires ADB's --bypass-low-target-sdk-block option to install this legacy test build.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
