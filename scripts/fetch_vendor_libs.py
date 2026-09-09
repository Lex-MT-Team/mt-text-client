#!/usr/bin/env python3
"""Restore the exact MoonTrader assemblies pinned in lib/vendor.json.

Detects the host unless --rid or MTC_VENDOR_RID selects a target. Verified DLLs
and verified archives work offline; --force re-downloads the pinned archive.
Windows SFX archives are read with system tar (libarchive) or 7-Zip, never run.
Changing the public release requires updating the lock and porting the client.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

sys.dont_write_bytecode = True
from patch_mtshared_arm64 import MACHINES, patch

_UA = "mt-text-client-fetch-vendor-libs/1.0"
_SUPPORTED = {"osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "win-x64"}


def detect_rid() -> str:
    system = {"darwin": "osx", "linux": "linux", "win32": "win"}.get(sys.platform)
    machine = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    rid = f"{system}-{machine}"
    if rid not in _SUPPORTED:
        raise ValueError(f"unsupported host: {sys.platform}/{platform.machine()}; select a supported --rid")
    return rid


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def verified(path: Path, sha: str) -> bool:
    return path.is_file() and sha256_file(path) == sha


def load_pin(root: Path, rid: str) -> tuple[dict, dict]:
    lock = json.loads((root / "lib/vendor.json").read_text(encoding="utf-8"))
    if not re.fullmatch(r"\d+", lock["version"]):
        raise ValueError("vendor version must be numeric")
    if rid not in _SUPPORTED or rid not in lock["platforms"]:
        raise ValueError(f"unsupported RID: {rid}; supported: {', '.join(sorted(_SUPPORTED))}")
    pin = lock["platforms"][rid]
    if set(pin["files"]) != {"MTShared.dll", "LiteNetLib.dll"}:
        raise ValueError("vendor lock must pin both MTShared.dll and LiteNetLib.dll")
    for sha in [pin["sha256"], *(item["sha256"] for item in pin["files"].values())]:
        if not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise ValueError("vendor lock contains an invalid SHA-256")
    for item in pin["files"].values():
        member = PurePosixPath(item["path"])
        if member.is_absolute() or ".." in member.parts:
            raise ValueError("archive member must be a relative path without traversal")
    return lock, pin


@contextmanager
def restore_lock(path: Path):
    """Serialize restores for one RID across parallel MSBuild processes."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as stream:
        stream.seek(0, os.SEEK_END)
        if stream.tell() == 0:
            stream.write(b"\0")
            stream.flush()
        deadline = time.monotonic() + 180
        while True:
            try:
                stream.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise TimeoutError(f"timed out waiting for vendor restore lock: {path.name}")
                time.sleep(0.1)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def download_verify(url: str, sha: str, destination: Path) -> None:
    """Never publish a partial download or an archive with the wrong digest."""
    print(f"  downloading {url}", flush=True)
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="download-", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as output:
            request = urllib.request.Request(url, headers={"User-Agent": _UA})
            with urllib.request.urlopen(request, timeout=60) as response:
                shutil.copyfileobj(response, output)
        if not verified(temporary, sha):
            raise ValueError("archive SHA-256 mismatch; refusing the download")
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def resolve_member(names: list[str], wanted: str) -> str:
    matches = [name for name in names if name.removeprefix("./") == wanted]
    if not matches:
        matches = [name for name in names if name.endswith("/" + wanted)]
    if len(matches) != 1:
        raise ValueError(f"expected one archive member for {wanted}, found {len(matches)}")
    return matches[0]


def extract_pair(archive: Path, pin: dict, staging: Path) -> None:
    if pin["format"] == "tar.xz":
        with tarfile.open(archive, "r:xz") as tar:
            names = tar.getnames()
            for name, item in pin["files"].items():
                member = tar.getmember(resolve_member(names, item["path"]))
                if not member.isfile():
                    raise ValueError(f"{item['path']} is not a regular archive member")
                with tar.extractfile(member) as source, (staging / name).open("wb") as output:
                    shutil.copyfileobj(source, output)
    elif pin["format"] == "7z-sfx":
        # Windows 10/11 ships libarchive tar.exe. A GNU tar on PATH may not read
        # 7z, so try the other installed readers before reporting a remedy.
        failures = []
        for tool in ("bsdtar", "tar", "7zz", "7z"):
            executable = shutil.which(tool)
            if not executable:
                continue
            try:
                if tool in ("bsdtar", "tar"):
                    listing = subprocess.run([executable, "-tf", str(archive)], check=True, capture_output=True, text=True, timeout=120)
                    names = listing.stdout.splitlines()
                    for name, item in pin["files"].items():
                        member = resolve_member(names, item["path"])
                        with (staging / name).open("wb") as output:
                            subprocess.run([executable, "-xOf", str(archive), member], stdout=output, stderr=subprocess.PIPE, check=True, timeout=120)
                else:
                    for name, item in pin["files"].items():
                        with (staging / name).open("wb") as output:
                            subprocess.run([executable, "x", "-so", str(archive), item["path"]], stdout=output, stderr=subprocess.PIPE, check=True, timeout=120)
                return
            except (subprocess.SubprocessError, ValueError) as error:
                failures.append(f"{tool}: {error}")
        raise ValueError("Windows archive needs libarchive tar.exe/bsdtar or 7-Zip on PATH. " + "; ".join(failures))
    else:
        raise ValueError(f"unsupported archive format: {pin['format']}")


def restore(root: Path, rid: str, force: bool = False, offline: bool = False) -> Path:
    lock, pin = load_pin(root, rid)
    directory = root / "lib" / rid
    cache = root / "lib/.cache"
    with restore_lock(cache / f"{rid}.lock"):
        if not force and all(verified(directory / name, item["sha256"]) for name, item in pin["files"].items()):
            print(f"[fetch_vendor_libs] verified {lock['core_build']} / {rid}")
            return directory
        archive = cache / pin["sha256"] / ("vendor.exe" if pin["format"] == "7z-sfx" else "vendor.tar.xz")
        if force or not verified(archive, pin["sha256"]):
            if offline:
                raise ValueError(f"offline: no verified vendor cache for {lock['core_build']} / {rid}; run a build online first")
            download_verify(pin["url"], pin["sha256"], archive)
        directory.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="extract-", dir=cache) as temporary:
            staging = Path(temporary)
            extract_pair(archive, pin, staging)
            patch(staging / "MTShared.dll", MACHINES[pin["machine"]])
            for name, item in pin["files"].items():
                if not verified(staging / name, item["sha256"]):
                    raise ValueError(f"{name} SHA-256 mismatch; refusing extracted DLLs")
            # Validate the whole pair before replacing either file. Publish only
            # after extraction/normalization succeeds, under the per-RID lock.
            for name in pin["files"]:
                os.replace(staging / name, directory / name)
        print(f"[fetch_vendor_libs] restored {lock['core_build']} / {rid}")
        return directory


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", help="target RID (default: MTC_VENDOR_RID or host)")
    parser.add_argument("--force", action="store_true", help="re-download the exact pinned archive")
    parser.add_argument("--offline", action="store_true", help="use verified local files/archives only")
    args = parser.parse_args()
    try:
        rid = args.rid or os.environ.get("MTC_VENDOR_RID") or detect_rid()
        restore(Path(__file__).resolve().parent.parent, rid, args.force, args.offline)
        return 0
    except (OSError, ValueError, KeyError, tarfile.TarError, subprocess.SubprocessError) as error:
        print(f"[fetch_vendor_libs] {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
