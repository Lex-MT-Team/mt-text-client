# MoonTrader assemblies

`MTShared.dll` supplies MoonTrader protocol types and the encrypted UDP client.
`LiteNetLib.dll` supplies its UDP transport. Both are managed assemblies restored
from the public MoonTrader CDN; they are no longer committed to this repository.

The client is pinned to MTCore **0.7.25589** (archive version **725589**) in
[`vendor.json`](vendor.json). Builds download the pinned release automatically.
They do not follow the CDN's moving `version.txt` on every build: a newer release
can change the protocol and require source changes.

## Build prerequisites and selection

Install .NET 8 and Python 3.9 or newer (with the standard `lzma` module). Windows
uses `python`; macOS and Linux use `python3`. Override the executable with
`-p:VendorPython=/path/to/python`. Windows archive extraction also requires
libarchive `tar` (included with current Windows), `bsdtar`, or `7z`/`7zz` on PATH.
The self-extracting `.exe` is opened as an archive, never executed.

```sh
dotnet build MTTextClient.sln -c Release
dotnet test MTTextClient.sln -c Release --no-build --filter "Category=Static|Category=Unit"
```

`Directory.Build.targets` restores before assembly resolution and supplies the
same references to the application, tests, and both generators. A cold first
build works without manually running the fetch script. Invalid or missing DLLs
fail the build if restoration cannot recover them; there is no bundled fallback.

RID selection, in order: `-p:MTCoreVendorRid`, `RuntimeIdentifier` (including
`dotnet build -r`), `MTC_VENDOR_RID`, then the OS and architecture of the .NET
build process. Standalone Python uses `--rid`, `MTC_VENDOR_RID`, then its host.
Use an explicit RID when the Python and .NET processes have different architectures.

| RID | CDN channel | Archive |
|---|---|---|
| `osx-arm64` | `macosx-x86_64` | `MoonTrader-version_725589.tar.xz` |
| `osx-x64` | `macosx-x86_64` | `MoonTrader-version_725589.tar.xz` |
| `linux-x64` | `linux-x86_64` | `MTCore-version_725589.tar.xz` |
| `linux-arm64` | `linux-arm64` | `MTCore-version_725589.tar.xz` |
| `win-x64` | `windows-x86_64` | `MTCore-version_725589.exe` |

Other targets fail explicitly, including native Windows ARM64. Selecting a target
for a cross-build verifies and compiles its assemblies; running its tests still
requires that OS and architecture. The macOS ARM64 entry uses the published Mac
assembly with its PE Machine field normalized to ARM64. Final normalized bytes
are hash-pinned; this is not a general claim that other vendor DLLs are portable.

## Cache and offline use

Restored DLLs live in gitignored `lib/<rid>/`; archives live in
`lib/.cache/<archive-sha256>/`. Separate archive hashes prevent identical Linux
archive names from colliding. Every build checks **both** final DLL hashes. If
one is missing or corrupt, a verified cached archive can repair the pair offline.
Downloads and extraction are staged; both DLLs must validate before publication.
A per-RID process lock coordinates concurrent project builds.

```sh
# Use verified DLLs or a verified cached archive, without CDN access.
dotnet build MTTextClient.sln -c Release -p:VendorOffline=true
# Populate another target; use python instead of python3 on Windows.
python3 scripts/fetch_vendor_libs.py --rid win-x64
# Re-download the pinned release, rather than change its version.
python3 scripts/fetch_vendor_libs.py --force
```

`-p:FetchVendorLibs=true` also forces downloading, once per project restore;
prefer the standalone `--force` command to refresh a solution once.
`FetchVendorLibs=false` does not disable validation or required restoration.
Use `VendorOffline=true` to forbid vendor network access. NuGet restoration is
separate; `--no-restore` additionally avoids a NuGet restore when assets exist.

## Upgrading the pin

1. Read each supported channel's public
   `https://cdn3.moontrader.com/beta/<channel>/version.txt`. Select a common release
   and record each archive URL and vendor-published SHA-256 in `vendor.json`.
2. Download and verify those archives. Extract the two declared members and
   record their SHA-256 values, after the explicit MTShared Machine normalization
   for each RID. LiteNetLib is not patched. Inspect assembly references and the
   new wire types before accepting the pin.
3. Update `core_build`, `version`, `CoreStatusStore.ExpectedCoreBuild`, and the
   affected protocol handlers together. See
   [`mtcore-25589-protocol.md`](../docs/mtcore-25589-protocol.md) for this migration.
   The exact DLL hashes invalidate obsolete cached pairs automatically.
4. Run cold and cached builds and the Static/Unit gate on target systems, then
   separately validate the real-core protocol in an authorized test environment.
   Compilation alone does not establish wire compatibility.

No floating-latest fallback is used when an archive disappears. Preserve a
verified local cache or explicitly review and upgrade the pin.
