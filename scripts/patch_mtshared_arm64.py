#!/usr/bin/env python3
"""
Patch the MTShared.dll PE Machine field so the .NET 8 runtime on the
build host will load it.

Vendor ships MTShared.dll built for x64 (Machine=0x8664). The committed
lib/MTShared.dll baseline carries Machine=0xAA64 so Apple Silicon hosts
load it natively. The managed body is pure IL either way — only the PE
header byte differs — but the CLR's probing validates the Machine field
against the host, so each platform needs the matching value in the
assemblies it actually loads:

  * macOS arm64 -> --machine arm64 (0xAA64), the default
  * linux/win x64 -> --machine x64 (0x8664), build outputs only

Default targets (idempotent — script can be run on every build):
  * lib/MTShared.dll                                          — source copy (arm64 mode only)
  * bin/Release/net8.0/MTShared.dll                           — Release output
  * bin/Debug/net8.0/MTShared.dll                             — Debug output
  * tests/MTTextClient.Tests/bin/Release/net8.0/MTShared.dll  — Release test bin
  * tests/MTTextClient.Tests/bin/Debug/net8.0/MTShared.dll    — Debug test bin

In x64 mode the committed lib/MTShared.dll is left untouched — only
build outputs are rewritten, keeping the repo content stable across
platforms. Each path already at the requested Machine value is reported
and skipped. Pass explicit paths as positional args to override the
default list.

Wired into MTTextClient.csproj and the test csproj as post-build
MSBuild targets: arm64 mode on macOS, x64 mode on Linux; no-op
elsewhere.
"""
import argparse
import struct
from pathlib import Path

DEFAULT_PATHS = [
    "lib/MTShared.dll",
    "bin/Release/net8.0/MTShared.dll",
    "bin/Debug/net8.0/MTShared.dll",
    "tests/MTTextClient.Tests/bin/Release/net8.0/MTShared.dll",
    "tests/MTTextClient.Tests/bin/Debug/net8.0/MTShared.dll",
]

MACHINES = {
    "x64": 0x8664,
    "arm64": 0xAA64,
}


def patch(dll_path: Path, target_machine: int) -> str:
    """Idempotent PE Machine-field flip. Returns a human status string."""
    if not dll_path.exists():
        return f"skip (not found): {dll_path}"
    with open(dll_path, "r+b") as f:
        data = f.read()
        # PE offset stored at 0x3C in the DOS stub.
        pe_off = struct.unpack_from("<I", data, 0x3C)[0]
        # Machine field is the first 2 bytes of the COFF header
        # (= 4 bytes after the "PE\0\0" signature).
        machine_off = pe_off + 4
        cur = struct.unpack_from("<H", data, machine_off)[0]
        if cur == target_machine:
            return f"already at 0x{target_machine:04X}: {dll_path}"
        elif cur in MACHINES.values():
            f.seek(machine_off)
            f.write(struct.pack("<H", target_machine))
            return f"patched 0x{cur:04X}->0x{target_machine:04X}: {dll_path}"
        else:
            return f"unexpected machine 0x{cur:04X}: {dll_path}"


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--machine", choices=sorted(MACHINES), default="arm64",
                    help="target PE Machine value (default: arm64)")
    ap.add_argument("paths", nargs="*",
                    help="explicit dll paths (default: standard lib+bin set)")
    args = ap.parse_args()

    target = MACHINES[args.machine]
    targets = args.paths or list(DEFAULT_PATHS)
    # x64 mode rewrites build outputs only; the committed lib/ baseline
    # stays arm64 so the repo content is identical on every platform.
    if args.machine == "x64" and not args.paths:
        targets = [t for t in targets if not t.startswith("lib/")]

    repo_root = Path(__file__).resolve().parent.parent
    any_patched = False
    for t in targets:
        p = (repo_root / t).resolve() if not Path(t).is_absolute() else Path(t)
        result = patch(p, target)
        print(result)
        if result.startswith("patched"):
            any_patched = True
    if not any_patched:
        print("(no patches needed this run)")
