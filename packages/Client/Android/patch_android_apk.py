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


def patch_android_manifest_compatibility(manifest: bytes) -> bytes:
    """Allow phone screens and raise target API for newer Android install flows."""
    patched = bytearray(manifest)
    strings: list[str] = []
    small_screen_found = False
    target_sdk_found = False
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
                else:
                    char_length, length_bytes = _read_length16(patched, string_offset)
                    start = string_offset + length_bytes
                    value = bytes(patched[start : start + char_length * 2]).decode("utf-16le", "replace")
                strings.append(value)

        elif chunk_type == 0x0102:  # RES_XML_START_ELEMENT_TYPE
            name_index = struct.unpack_from("<I", patched, offset + 20)[0]
            tag_name = strings[name_index] if name_index < len(strings) else ""
            if tag_name in ("supports-screens", "uses-sdk"):
                attribute_start, attribute_size, attribute_count = struct.unpack_from("<HHH", patched, offset + 24)
                found: set[str] = set()
                for index in range(attribute_count):
                    attribute = offset + 16 + attribute_start + index * attribute_size
                    attribute_name_index = struct.unpack_from("<I", patched, attribute + 4)[0]
                    attribute_name = strings[attribute_name_index] if attribute_name_index < len(strings) else ""
                    if tag_name == "supports-screens" and attribute_name == "smallScreens":
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
                        # API 26 clears newer Android low-target warnings and exceeds the
                        # Android 14/15 install minimums while avoiding API 28 cleartext changes.
                        patched[attribute + 15] = 0x10
                        struct.pack_into("<I", patched, attribute + 16, 26)
                        found.add(attribute_name)
                if tag_name == "supports-screens":
                    small_screen_found = found == {"smallScreens", "requiresSmallestWidthDp"}
                elif tag_name == "uses-sdk":
                    target_sdk_found = "targetSdkVersion" in found
                if small_screen_found and target_sdk_found:
                    return bytes(patched)

        offset += chunk_size

    raise ValueError("AndroidManifest.xml did not contain the expected small-screen restrictions")


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
        if CONFIG_ENTRY not in names or PHOTON_ENTRY not in names or GAME_ASSEMBLY_ENTRY not in names:
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
            manifest_bytes = patch_android_manifest_compatibility(source.read("AndroidManifest.xml"))
        except ValueError as error:
            parser.error(str(error))
        shim_bytes = shim.read_bytes()

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
    print("Raised target API to 26; minimum API is unchanged and HTTP cleartext defaults remain compatible.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
