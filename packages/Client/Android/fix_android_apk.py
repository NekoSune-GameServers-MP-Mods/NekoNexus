#!/usr/bin/env python3
"""Fix an existing NekoNexus UberStrike 4.3.10 Android APK without rebuilding it.

Needs only Python 3.8+ (no pip packages, no dotnet). Signing uses apksigner (Android SDK
build-tools) when it is on PATH, otherwise jarsigner/keytool from a JDK.

What it does:
  * sets WebServiceBaseUrl / ContentBaseUrl in assets/UberStrike.xml (optional)
  * patches the NekoNexus Photon3Unity3D shim inside the APK:
      - DispatchIncomingCommands / SendOutgoingCommands return false instead of true. The 4.3.10
        client runs `while (peer.DispatchIncomingCommands()) {}` every frame, so `true` froze
        the game as soon as it connected to a realtime server (frozen screen, ANR on Back).
      - RoundTripTime returns Ping + 1 instead of Ping * 2, so it is never 0 while connected.
        LiteNetLib's Ping stays 0 for about a second after connecting, and the client treats
        latency 0 as "server unreachable", which hid every server.
  * removes old signatures, 4-byte-aligns stored entries, and signs the result.

The shim fixes match the exact method bodies of the NekoNexus shim and refuse to touch anything
else. Re-running on an already fixed APK is safe. Source-level versions of the same fixes are in
shims/Photon3Unity3D.Shim/PhotonPeer.cs.

Example:
  python fix_android_apk.py UberStrike-NekoNexus.apk UberStrike-NekoNexus-fixed.apk \\
      --web-service http://75.119.148.51:8080/1.0.2/ --content http://75.119.148.51:8081/UberStrike/
"""

from __future__ import annotations

import argparse
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile

CONFIG_ENTRY = "assets/UberStrike.xml"
PHOTON_ENTRY = "assets/bin/Data/Managed/Photon3Unity3D.dll"


# ---- PE helpers ------------------------------------------------------------------


def pe_sections(data: bytes) -> list[tuple[int, int, int, int]]:
    """(virtual_address, virtual_size, raw_offset, raw_size) of each section."""
    if data[:2] != b"MZ":
        raise ValueError("not a PE file")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe : pe + 4] != b"PE\0\0":
        raise ValueError("not a PE file")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    table = pe + 24 + optional_size
    sections = []
    for index in range(count):
        entry = table + index * 40
        vsize, vaddr, rsize, roff = struct.unpack_from("<IIII", data, entry + 8)
        sections.append((vaddr, vsize, roff, rsize))
    return sections


def in_code(sections, offset: int) -> bool:
    return any(roff <= offset < roff + rsize for _, _, roff, rsize in sections)


# ---- shim patch ------------------------------------------------------------------

# Tiny-header method bodies (header byte = code_size << 2 | 2). Tokens: 0x04 = FieldDef,
# 0x06 = MethodDef. F = PhotonPeer._net, P = NetManager.PollEvents, S = PhotonPeer._server,
# G = NetPeer.get_Ping, as written by the C# compiler for the NekoNexus shim.
#   Service():                  ldarg.0; ldfld F; callvirt P; ret
SERVICE = re.compile(rb"\x32\x02\x7b(...\x04)\x6f(...\x06)\x2a", re.S)
#   Dispatch/SendOutgoing():    ldarg.0; ldfld F; callvirt P; ldc.i4.(1|0); ret
def poll_and_return(field: bytes, method: bytes) -> re.Pattern:
    return re.compile(rb"\x36\x02\x7b" + re.escape(field) + rb"\x6f" + re.escape(method) + rb"([\x16\x17])\x2a", re.S)
#   RoundTripTime getter:       ldarg.0; ldfld S; dup; brtrue.s +3; pop; ldc.i4.0; ret;
#                               call G; (ldc.i4.2; mul | ldc.i4.1; add); ret
ROUND_TRIP = re.compile(rb"\x52\x02\x7b...\x04\x25\x2d\x03\x26\x16\x2a\x28...\x06(\x18\x5a|\x17\x58)\x2a", re.S)


def patch_photon_shim(dll: bytes) -> tuple[bytes, list[str]]:
    data = bytearray(dll)
    sections = pe_sections(dll)
    notes: list[str] = []

    if b"LiteNetLib" not in dll or b"NekoNexus" not in dll:
        raise ValueError("Photon3Unity3D.dll is not the NekoNexus LiteNetLib shim; nothing to fix")

    services = [m for m in SERVICE.finditer(dll) if in_code(sections, m.start())]
    candidates = {(m.group(1), m.group(2)) for m in services}
    target = None
    for field, method in candidates:
        hits = [m for m in poll_and_return(field, method).finditer(dll) if in_code(sections, m.start())]
        if len(hits) == 2:  # DispatchIncomingCommands + SendOutgoingCommands
            if target is not None:
                raise ValueError("ambiguous shim layout (several poll methods); refusing to patch")
            target = hits
    if target is None:
        raise ValueError("could not find DispatchIncomingCommands/SendOutgoingCommands in the shim")
    changed = 0
    for hit in target:
        pos = hit.start(1)
        if data[pos] == 0x17:
            data[pos] = 0x16  # ldc.i4.1 -> ldc.i4.0
            changed += 1
    notes.append(
        "DispatchIncomingCommands/SendOutgoingCommands now return false"
        if changed
        else "DispatchIncomingCommands/SendOutgoingCommands already fixed"
    )

    rtt = [m for m in ROUND_TRIP.finditer(dll) if in_code(sections, m.start())]
    if len(rtt) != 1:
        raise ValueError(f"expected one RoundTripTime getter, found {len(rtt)}; refusing to patch")
    pos = rtt[0].start(1)
    if data[pos : pos + 2] == b"\x18\x5a":
        data[pos : pos + 2] = b"\x17\x58"  # Ping * 2 -> Ping + 1
        notes.append("RoundTripTime is never 0 while connected")
    else:
        notes.append("RoundTripTime already fixed")
    return bytes(data), notes


# ---- config ------------------------------------------------------------------------


def normalized_url(value: str, name: str) -> str:
    value = value.strip()
    if not re.match(r"^https?://", value, re.I):
        raise SystemExit(f"{name} must start with http:// or https://")
    return value.rstrip("/") + "/"


def patch_config(xml: bytes, web_service: str | None, content: str | None) -> tuple[bytes, list[str]]:
    text = xml.decode("us-ascii")
    notes = []
    for attribute, value in (("WebServiceBaseUrl", web_service), ("ContentBaseUrl", content)):
        if value is None:
            continue
        text, count = re.subn(rf'{attribute}="[^"]*"', f'{attribute}="{value}"', text)
        if count != 1:
            raise ValueError(f"{CONFIG_ENTRY} has no single {attribute} attribute")
        notes.append(f"{attribute} = {value}")
    found = dict(re.findall(r'(\w+BaseUrl)="([^"]*)"', text))
    web = found.get("WebServiceBaseUrl", "")
    if web and found.get("ContentBaseUrl") and web.split("/1.0.2")[0] == found["ContentBaseUrl"].split("/UberStrike")[0]:
        notes.append("WARNING: web service and content use the same host:port - the API is normally on 8080, files on 8081")
    return text.encode("us-ascii"), notes


# ---- APK writing -----------------------------------------------------------------


def is_signature_file(name: str) -> bool:
    upper = name.upper()
    if not upper.startswith("META-INF/"):
        return False
    leaf = upper.rsplit("/", 1)[-1]
    return leaf == "MANIFEST.MF" or leaf.endswith((".SF", ".RSA", ".DSA", ".EC")) or leaf.startswith("SIG-")


def write_aligned(source: zipfile.ZipFile, output: pathlib.Path, replacements: dict[str, bytes]) -> None:
    """Copy the APK, swapping entries, dropping signatures, and 4-byte-aligning stored data."""
    with open(output, "wb") as raw, zipfile.ZipFile(raw, "w") as out:
        for info in source.infolist():
            if is_signature_file(info.filename):
                continue
            payload = replacements.get(info.filename)
            if payload is None:
                payload = source.read(info.filename)
            new = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            new.compress_type = info.compress_type
            new.external_attr = info.external_attr
            new.create_system = info.create_system
            new.extra = b""
            if info.compress_type == zipfile.ZIP_STORED:
                # Local header is 30 bytes + name + extra; pad extra so the data is 4-aligned.
                header_end = raw.tell() + 30 + len(info.filename.encode("utf-8"))
                new.extra = b"\0" * ((4 - header_end % 4) % 4)
            out.writestr(new, payload)


def sign(apk: pathlib.Path, keystore: pathlib.Path, alias: str, password: str) -> str:
    if not keystore.exists():
        keytool = shutil.which("keytool")
        if not keytool:
            raise SystemExit("keytool not found: install a JDK, or pass --no-sign and sign the APK yourself")
        subprocess.run(
            [keytool, "-genkeypair", "-keystore", str(keystore), "-storepass", password, "-keypass", password,
             "-alias", alias, "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
             "-dname", "CN=NekoNexus Android"],
            check=True, stdout=subprocess.DEVNULL,
        )
        print(f"Created signing key {keystore} (keep it: updates must be signed with the same key)")

    apksigner = shutil.which("apksigner") or shutil.which("apksigner.bat")
    if apksigner:
        subprocess.run(
            [apksigner, "sign", "--ks", str(keystore), "--ks-pass", f"pass:{password}",
             "--ks-key-alias", alias, "--key-pass", f"pass:{password}", str(apk)],
            check=True,
        )
        return "apksigner (v1+v2)"

    jarsigner = shutil.which("jarsigner")
    if not jarsigner:
        raise SystemExit("neither apksigner nor jarsigner found: install a JDK, or pass --no-sign")
    with tempfile.TemporaryDirectory() as tmp:
        signed = pathlib.Path(tmp) / "signed.apk"
        subprocess.run(
            [jarsigner, "-keystore", str(keystore), "-storepass", password, "-keypass", password,
             "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256", "-signedjar", str(signed), str(apk), alias],
            check=True, stdout=subprocess.DEVNULL,
        )
        shutil.move(str(signed), str(apk))
    return "jarsigner (v1; fine for this APK's target API 26)"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("apk", type=pathlib.Path, help="NekoNexus UberStrike Android APK to fix (not modified)")
    parser.add_argument("output", type=pathlib.Path, help="fixed APK to write")
    parser.add_argument("--web-service", help="API base URL, e.g. http://HOST:8080/1.0.2/")
    parser.add_argument("--content", help="content base URL, e.g. http://HOST:8081/UberStrike/")
    parser.add_argument("--keystore", type=pathlib.Path, default=pathlib.Path("nekonexus-android.jks"),
                        help="signing keystore (created if missing; default nekonexus-android.jks)")
    parser.add_argument("--alias", default="nekonexus")
    parser.add_argument("--password", default=os.environ.get("NEKONEXUS_KEYSTORE_PASSWORD", "nekonexus"),
                        help="keystore password (or set NEKONEXUS_KEYSTORE_PASSWORD)")
    parser.add_argument("--no-sign", action="store_true", help="write an unsigned APK")
    args = parser.parse_args()

    source_path = args.apk.resolve()
    output = args.output.resolve()
    if source_path == output:
        parser.error("output must differ from the input APK")
    web = normalized_url(args.web_service, "--web-service") if args.web_service else None
    content = normalized_url(args.content, "--content") if args.content else None

    with zipfile.ZipFile(source_path) as source:
        names = set(source.namelist())
        for required in (CONFIG_ENTRY, PHOTON_ENTRY):
            if required not in names:
                parser.error(f"{required} missing: is this the UberStrike 4.3.10 Android APK?")

        replacements: dict[str, bytes] = {}
        config, notes = patch_config(source.read(CONFIG_ENTRY), web, content)
        replacements[CONFIG_ENTRY] = config
        try:
            shim, shim_notes = patch_photon_shim(source.read(PHOTON_ENTRY))
        except ValueError as error:
            parser.error(str(error))
        replacements[PHOTON_ENTRY] = shim
        notes += shim_notes

        output.parent.mkdir(parents=True, exist_ok=True)
        write_aligned(source, output, replacements)

    for note in notes:
        print(" - " + note)
    if args.no_sign:
        print(f"Unsigned APK written to {output}")
    else:
        how = sign(output, args.keystore.resolve(), args.alias, args.password)
        print(f"Signed with {how}: {output}")
        print("If Android refuses to install over an older build, uninstall it first (different signing key).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
