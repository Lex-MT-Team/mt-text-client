#!/usr/bin/env python3
"""
Patch MTShared.dll PE Machine field from AMD64 (0x8664) -> ARM64 (0xAA64)
so the .NET 8 runtime on Apple Silicon will load it.

Vendor ships MTShared.dll built for x64 (Machine=0x8664). Under Apple
Silicon (.NET 8 / Apple ARM64), the CLR refuses to load that as a
native dependency. Flipping the Machine field is the minimum-impact
patch: the PE header now marks the assembly as ARM64, and because the
managed body is pure IL (AnyCPU after JIT), it runs fine. The vendor's
own bench builds use the same trick.

Default targets (idempotent — script can be run on every build):
  * lib/MTShared.dll                                          — source copy
  * bin/Release/net8.0/MTShared.dll                           — Release output
  * bin/Debug/net8.0/MTShared.dll                             — Debug output
  * tests/MTTextClient.Tests/bin/Release/net8.0/MTShared.dll  — Release test bin
  * tests/MTTextClient.Tests/bin/Debug/net8.0/MTShared.dll    — Debug test bin

Each path that already has Machine=0xAA64 is reported and skipped.
Pass explicit paths as positional args to override the default list.

Apple Silicon build hygiene. Wired into MTTextClient.csproj as a
post-build MSBuild target conditioned on the OSX runtime; on
Linux/Windows the target is a no-op.
"""
import struct
import sys
from pathlib import Path

DEFAULT_PATHS = [
    "lib/MTShared.dll",
    "bin/Release/net8.0/MTShared.dll",
    "bin/Debug/net8.0/MTShared.dll",
    "tests/MTTextClient.Tests/bin/Release/net8.0/MTShared.dll",
    "tests/MTTextClient.Tests/bin/Debug/net8.0/MTShared.dll",
]

MACHINE_AMD64 = 0x8664
MACHINE_ARM64 = 0xAA64


def patch(dll_path: Path) -> str:
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
        if cur == MACHINE_AMD64:
            f.seek(machine_off)
            f.write(struct.pack("<H", MACHINE_ARM64))
            return f"patched AMD64->ARM64: {dll_path}"
        elif cur == MACHINE_ARM64:
            return f"already patched: {dll_path}"
        else:
            return f"unexpected machine 0x{cur:04X}: {dll_path}"


if __name__ == "__main__":
    targets = sys.argv[1:] or DEFAULT_PATHS
    repo_root = Path(__file__).resolve().parent.parent
    any_patched = False
    for t in targets:
        p = (repo_root / t).resolve() if not Path(t).is_absolute() else Path(t)
        result = patch(p)
        print(result)
        if result.startswith("patched"):
            any_patched = True
    if not any_patched:
        print("(no patches needed this run)")
