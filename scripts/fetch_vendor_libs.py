#!/usr/bin/env python3
"""
Fetch MTShared.dll and LiteNetLib.dll for the host runtime from the public
MoonTrader CDN, verify against the vendor-published SHA-256 manifest, and
cache under lib/<rid>/. Run by MSBuild before reference resolution; can be
re-run manually:

    python3 scripts/fetch_vendor_libs.py [--rid osx-arm64|osx-x64|linux-x64|linux-arm64] [--force]

Behaviour:
  * Detects the host RID when --rid is not supplied (or honours MTC_VENDOR_RID).
  * Caches the vendor tarball under lib/.cache/ so subsequent builds are offline.
  * For osx-arm64 there is no native vendor build; we extract from macosx-x86_64
    and flip the PE Machine field from x64 (0x8664) to ARM64 (0xaa64). The
    managed IL is AnyCPU and JITs fine after the header arch claim is correct.

The pinned vendor version is set by VENDOR_VERSION below; bump it together with
any wire-protocol updates to Core/CoreConnection.cs.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import platform
import shutil
import struct
import sys
import tarfile
import urllib.error
import urllib.request


VENDOR_VERSION = "725267"
CDN_BASE = "https://cdn3.moontrader.com/beta"
# Cloudflare rejects the default urllib User-Agent with a 403; supply one.
_UA = "mt-text-client-fetch-vendor-libs/1.0 (+https://github.com/Lex-MT-Team/mt-text-client)"

# RID → (channel, archive_kind, mtshared_path, litenetlib_path)
#   archive_kind: "moontrader" (the full GUI bundle, only place MTShared.dll
#                  lives on macOS) or "mtcore" (headless server, smaller).
#   *_path: location within the tarball.
_RID_MAP = {
    "osx-x64":     ("macosx-x86_64", "moontrader",
                    "MoonTrader.app/Contents/Resources/Data/Managed/MTShared.dll",
                    "lib/LiteNetLib.dll"),
    "osx-arm64":   ("macosx-x86_64", "moontrader",
                    "MoonTrader.app/Contents/Resources/Data/Managed/MTShared.dll",
                    "lib/LiteNetLib.dll"),
    "linux-x64":   ("linux-x86_64",  "mtcore",
                    "BotClient/lib/MTShared.dll",
                    "lib/LiteNetLib.dll"),
    "linux-arm64": ("linux-arm64",   "mtcore",
                    "BotClient/lib/MTShared.dll",
                    "lib/LiteNetLib.dll"),
}


def detect_rid() -> str:
    s = sys.platform
    m = platform.machine().lower()
    if s == "darwin":
        return "osx-arm64" if m in ("arm64", "aarch64") else "osx-x64"
    if s == "linux":
        return "linux-arm64" if m in ("arm64", "aarch64") else "linux-x64"
    raise SystemExit(f"[fetch_vendor_libs] unsupported host platform: {s} {m}")


def repo_root() -> str:
    return os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))


def _opener_for(url: str) -> urllib.request.Request:
    return urllib.request.Request(url, headers={"User-Agent": _UA})


def fetch_manifest(channel: str) -> str:
    url = f"{CDN_BASE}/{channel}/version.txt"
    with urllib.request.urlopen(_opener_for(url), timeout=30) as r:
        return r.read().decode("utf-8")


def parse_manifest_sha(text: str, archive_name: str) -> str:
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            continue
        key, value = (part.strip() for part in line.split("=", 1))
        if key == archive_name:
            if not value:
                raise SystemExit(
                    f"[fetch_vendor_libs] manifest has empty SHA for {archive_name} "
                    f"(this channel may not publish it)"
                )
            return value
    raise SystemExit(f"[fetch_vendor_libs] archive {archive_name} not listed in manifest")


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def download_verify(url: str, expected_sha: str, dest: str) -> None:
    print(f"  downloading {url}")
    tmp = dest + ".part"
    h = hashlib.sha256()
    bytes_total = 0
    with urllib.request.urlopen(_opener_for(url), timeout=120) as r, open(tmp, "wb") as f:
        while True:
            chunk = r.read(1 << 16)
            if not chunk:
                break
            f.write(chunk)
            h.update(chunk)
            bytes_total += len(chunk)
    got = h.hexdigest()
    if got != expected_sha:
        os.unlink(tmp)
        raise SystemExit(
            f"[fetch_vendor_libs] SHA-256 mismatch on {os.path.basename(dest)}\n"
            f"  expected {expected_sha}\n  got      {got}"
        )
    os.replace(tmp, dest)
    print(f"  {bytes_total:>10,} bytes, SHA-256 verified")


def _resolve_member(tar: tarfile.TarFile, wanted: str) -> tarfile.TarInfo:
    """Find a member by its archive-relative path. Tarballs use './prefix' so
    accept both forms; also accept any path that ends with /<wanted>."""
    names = tar.getnames()
    for candidate in (wanted, f"./{wanted}"):
        if candidate in names:
            return tar.getmember(candidate)
    suffix = "/" + wanted
    for n in names:
        if n.endswith(suffix):
            return tar.getmember(n)
    raise SystemExit(f"[fetch_vendor_libs] {wanted} not found inside tarball")


def extract_dll(tar: tarfile.TarFile, path_in_tar: str, dest_path: str) -> None:
    member = _resolve_member(tar, path_in_tar)
    with tar.extractfile(member) as src:
        if src is None:
            raise SystemExit(f"[fetch_vendor_libs] {path_in_tar} is not a regular file")
        with open(dest_path, "wb") as dst:
            shutil.copyfileobj(src, dst)
    os.chmod(dest_path, 0o644)
    print(f"  extracted {os.path.basename(dest_path)} ({os.path.getsize(dest_path):,} bytes)")


def patch_pe_arm64(dll_path: str) -> None:
    """Flip the PE Machine field from x64 (0x8664) to ARM64 (0xaa64). Idempotent.
    Needed for the osx-arm64 RID because the vendor doesn't publish a macosx-arm64
    build; the managed IL is AnyCPU and runs fine once the header arch claim is
    corrected."""
    with open(dll_path, "r+b") as f:
        f.seek(0x3c)
        pe_off = struct.unpack("<I", f.read(4))[0]
        f.seek(pe_off + 4)
        machine = struct.unpack("<H", f.read(2))[0]
        if machine == 0xaa64:
            print(f"  PE Machine already ARM64")
            return
        if machine != 0x8664:
            raise SystemExit(
                f"[fetch_vendor_libs] unexpected PE Machine 0x{machine:04x} in {dll_path}"
            )
        f.seek(pe_off + 4)
        f.write(b"\x64\xaa")
    print(f"  PE Machine flipped x64 → ARM64")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--rid", default=None,
                    help="Target runtime identifier (default: detect from host)")
    ap.add_argument("--force", action="store_true",
                    help="Re-download even if cached")
    args = ap.parse_args()

    rid = args.rid or os.environ.get("MTC_VENDOR_RID") or detect_rid()
    if rid not in _RID_MAP:
        raise SystemExit(f"[fetch_vendor_libs] unsupported RID: {rid} "
                         f"(supported: {', '.join(sorted(_RID_MAP))})")

    channel, archive_kind, mtshared_path, litenet_path = _RID_MAP[rid]
    root = repo_root()
    out_dir = os.path.join(root, "lib", rid)
    os.makedirs(out_dir, exist_ok=True)
    mtshared_dest = os.path.join(out_dir, "MTShared.dll")
    litenet_dest = os.path.join(out_dir, "LiteNetLib.dll")

    if not args.force and os.path.exists(mtshared_dest) and os.path.exists(litenet_dest):
        print(f"[fetch_vendor_libs] cached lib/{rid}/ — nothing to do")
        return 0

    archive_basename = (
        f"{'MoonTrader' if archive_kind == 'moontrader' else 'MTCore'}"
        f"-version_{VENDOR_VERSION}.tar.xz"
    )
    archive_url = f"{CDN_BASE}/{channel}/{archive_basename}"

    print(f"[fetch_vendor_libs] rid={rid}  vendor_version={VENDOR_VERSION}")
    print(f"  channel={channel}  archive={archive_basename}")

    try:
        manifest = fetch_manifest(channel)
    except urllib.error.URLError as e:
        raise SystemExit(f"[fetch_vendor_libs] failed to fetch manifest: {e}")
    expected_sha = parse_manifest_sha(manifest, archive_basename)
    print(f"  manifest SHA-256: {expected_sha[:16]}…")

    cache_dir = os.path.join(root, "lib", ".cache")
    os.makedirs(cache_dir, exist_ok=True)
    cache_path = os.path.join(cache_dir, archive_basename)

    if os.path.exists(cache_path) and not args.force:
        cached_sha = sha256_file(cache_path)
        if cached_sha == expected_sha:
            print(f"  using cached tarball at lib/.cache/{archive_basename}")
        else:
            print(f"  cached tarball SHA mismatch; re-downloading")
            download_verify(archive_url, expected_sha, cache_path)
    else:
        download_verify(archive_url, expected_sha, cache_path)

    with tarfile.open(cache_path, "r:xz") as tar:
        extract_dll(tar, mtshared_path, mtshared_dest)
        extract_dll(tar, litenet_path, litenet_dest)

    if rid == "osx-arm64":
        patch_pe_arm64(mtshared_dest)

    print(f"[fetch_vendor_libs] lib/{rid}/ ready")
    return 0


if __name__ == "__main__":
    sys.exit(main())
