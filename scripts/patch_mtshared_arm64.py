#!/usr/bin/env python3
"""Normalize the PE Machine header of the pinned managed MTShared assembly.

Used on staged files by fetch_vendor_libs.py before final SHA-256 validation.
Explicit paths are required; never rewrite loaded assemblies or build outputs.
A matching PE header alone does not establish arbitrary DLL portability.
"""
import argparse
from pathlib import Path
import struct
import sys

MACHINES = {"x64": 0x8664, "arm64": 0xAA64}


def patch(dll_path: Path, target_machine: int) -> str:
    """Validate headers before an idempotent, explicit architecture change."""
    if target_machine not in MACHINES.values():
        raise ValueError(f"unsupported target Machine: 0x{target_machine:04X}")
    data = bytearray(dll_path.read_bytes())
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError("MTShared.dll has an invalid DOS header")
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[offset:offset + 4] != b"PE\0\0" or offset + 6 > len(data):
        raise ValueError("MTShared.dll has an invalid PE header")
    current = struct.unpack_from("<H", data, offset + 4)[0]
    if current not in MACHINES.values():
        raise ValueError(f"unexpected MTShared Machine: 0x{current:04X}")
    if current == target_machine:
        return f"already at 0x{target_machine:04X}: {dll_path}"
    struct.pack_into("<H", data, offset + 4, target_machine)
    dll_path.write_bytes(data)
    return f"patched 0x{current:04X}->0x{target_machine:04X}: {dll_path}"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", choices=sorted(MACHINES), required=True)
    parser.add_argument("paths", nargs="+", type=Path)
    args = parser.parse_args()
    try:
        for path in args.paths:
            print(patch(path, MACHINES[args.machine]))
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
