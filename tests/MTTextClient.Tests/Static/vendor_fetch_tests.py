"""Vendor bootstrap regressions using local archives; no CDN or MTCore access."""
import hashlib
from unittest import mock
import io
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT / "scripts"))
import fetch_vendor_libs as fetch
import patch_mtshared_arm64 as patcher


def digest(data):
    return hashlib.sha256(data).hexdigest()


def pe(machine=0x8664):
    data = bytearray(512)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x84, machine)
    return bytes(data)


class VendorFetchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="vendor-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "scripts").mkdir()
        (self.root / "lib").mkdir()
        for name in ("fetch_vendor_libs.py", "patch_mtshared_arm64.py"):
            shutil.copyfile(ROOT / "scripts" / name, self.root / "scripts" / name)
        self.raw = pe()
        self.patched = pe(0xAA64)
        self.transport = b"transport assembly fixture"
        self.archive = self.root / "vendor.tar.xz"
        self.write_archive()
        self.lock = {
            "version": "725589", "core_build": "0.7.25589",
            "platforms": {
                "osx-arm64": {
                    "url": self.archive.as_uri(), "sha256": digest(self.archive.read_bytes()),
                    "format": "tar.xz", "machine": "arm64",
                    "files": {
                        "MTShared.dll": {"path": "BotClient/lib/MTShared.dll", "sha256": digest(self.patched)},
                        "LiteNetLib.dll": {"path": "lib/LiteNetLib.dll", "sha256": digest(self.transport)},
                    },
                },
            },
        }
        self.write_lock()

    def write_archive(self, missing_transport=False):
        with tarfile.open(self.archive, "w:xz") as tar:
            items = [("./BotClient/lib/MTShared.dll", self.raw)]
            if not missing_transport:
                items.append(("./lib/LiteNetLib.dll", self.transport))
            for name, data in items:
                member = tarfile.TarInfo(name)
                member.size = len(data)
                tar.addfile(member, io.BytesIO(data))

    def write_lock(self):
        (self.root / "lib/vendor.json").write_text(json.dumps(self.lock))

    def run_fetch(self, *args, success=True):
        result = subprocess.run(
            [sys.executable, str(self.root / "scripts/fetch_vendor_libs.py"), "--rid", "osx-arm64", *args],
            capture_output=True, text=True, timeout=30)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    @property
    def output(self):
        return self.root / "lib/osx-arm64"

    def test_cold_fetch_extracts_and_normalizes_both_files(self):
        self.run_fetch()
        self.assertEqual((self.output / "MTShared.dll").read_bytes(), self.patched)
        self.assertEqual((self.output / "LiteNetLib.dll").read_bytes(), self.transport)

    def test_verified_pair_is_usable_offline_without_archive(self):
        self.run_fetch()
        self.archive.unlink()
        shutil.rmtree(self.root / "lib/.cache")
        self.run_fetch("--offline")

    def test_cache_does_not_accept_a_new_pin_by_existence_alone(self):
        self.run_fetch()
        self.lock["version"] = "725590"
        self.lock["platforms"]["osx-arm64"]["files"]["MTShared.dll"]["sha256"] = "0" * 64
        self.write_lock()
        self.run_fetch("--offline", success=False)

    def test_corrupt_extracted_file_is_repaired_from_verified_archive_offline(self):
        self.run_fetch()
        (self.output / "MTShared.dll").write_bytes(b"corrupt")
        self.archive.unlink()
        self.run_fetch("--offline")
        self.assertEqual((self.output / "MTShared.dll").read_bytes(), self.patched)

    def test_missing_transport_is_repaired_offline(self):
        self.run_fetch()
        (self.output / "LiteNetLib.dll").unlink()
        self.archive.unlink()
        self.run_fetch("--offline")
        self.assertEqual((self.output / "LiteNetLib.dll").read_bytes(), self.transport)

    def test_bad_archive_hash_cannot_replace_existing_pair(self):
        self.run_fetch()
        self.lock["platforms"]["osx-arm64"]["sha256"] = "0" * 64
        self.write_lock()
        self.run_fetch("--force", success=False)
        self.assertEqual((self.output / "MTShared.dll").read_bytes(), self.patched)
        self.assertEqual((self.output / "LiteNetLib.dll").read_bytes(), self.transport)

    def test_partial_extraction_cannot_replace_existing_pair(self):
        self.run_fetch()
        self.write_archive(missing_transport=True)
        self.lock["platforms"]["osx-arm64"]["sha256"] = digest(self.archive.read_bytes())
        self.write_lock()
        self.run_fetch("--force", success=False)
        self.assertEqual((self.output / "MTShared.dll").read_bytes(), self.patched)
        self.assertEqual((self.output / "LiteNetLib.dll").read_bytes(), self.transport)

    def test_offline_cold_start_fails_with_actionable_error(self):
        result = self.run_fetch("--offline", success=False)
        self.assertIn("offline", (result.stdout + result.stderr).lower())

    def test_target_detection_handles_supported_systems_and_rejects_unknown_hosts(self):
        for system, machine, expected in (
            ("darwin", "arm64", "osx-arm64"), ("darwin", "x86_64", "osx-x64"),
            ("linux", "aarch64", "linux-arm64"), ("linux", "x86_64", "linux-x64"),
            ("win32", "AMD64", "win-x64"),
        ):
            with self.subTest(system=system, machine=machine):
                with mock.patch.object(fetch.sys, "platform", system), mock.patch.object(fetch.platform, "machine", return_value=machine):
                    self.assertEqual(fetch.detect_rid(), expected)
        for system, machine in (("win32", "ARM64"), ("linux", "i686"), ("freebsd", "x86_64")):
            with mock.patch.object(fetch.sys, "platform", system), mock.patch.object(fetch.platform, "machine", return_value=machine):
                with self.assertRaises(ValueError):
                    fetch.detect_rid()

    def test_patch_is_idempotent_and_rejects_invalid_headers_without_writing(self):
        path = self.root / "patch.dll"
        path.write_bytes(self.raw)
        patcher.patch(path, 0xAA64)
        self.assertEqual(path.read_bytes(), self.patched)
        patcher.patch(path, 0xAA64)
        self.assertEqual(path.read_bytes(), self.patched)
        patcher.patch(path, 0x8664)
        self.assertEqual(path.read_bytes(), self.raw)
        for invalid in (b"invalid", b"XX" + self.raw[2:], pe(0x14c)):
            path.write_bytes(invalid)
            with self.assertRaises(ValueError):
                patcher.patch(path, 0xAA64)
            self.assertEqual(path.read_bytes(), invalid)

    def test_concurrent_fetches_publish_one_verified_pair(self):
        command = [sys.executable, str(self.root / "scripts/fetch_vendor_libs.py"), "--rid", "osx-arm64"]
        processes = [subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE) for _ in range(3)]
        for process in processes:
            stdout, stderr = process.communicate(timeout=30)
            self.assertEqual(process.returncode, 0, (stdout + stderr).decode())
        self.assertEqual((self.output / "MTShared.dll").read_bytes(), self.patched)
        self.assertEqual((self.output / "LiteNetLib.dll").read_bytes(), self.transport)


if __name__ == "__main__":
    unittest.main()
