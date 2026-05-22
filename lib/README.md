# lib/

Native dependencies required by `MTTextClient`:

- `MTShared.dll` — MoonTrader wire types, AES handshake, and the
  `UDPClient` class that `Core/CoreConnection.cs` invokes.
- `LiteNetLib.dll` — UDP transport (the wire used between mt-text-client
  and MTCore).

Both are managed assemblies vendored from the published MoonTrader build.

## Layout

```
lib/
  MTShared.dll              ← committed baseline (current API surface)
  LiteNetLib.dll            ← committed baseline
  <rid>/MTShared.dll        ← per-RID copy (when produced by the fetch script)
  <rid>/LiteNetLib.dll      ← per-RID copy
  .cache/                   ← downloaded vendor tarballs (gitignored)
```

The csproj prefers `lib/<rid>/MTShared.dll` when it exists for the host
runtime identifier and falls back to the top-level committed file otherwise.

## Supported RIDs

| RID | Channel | Archive | Notes |
|---|---|---|---|
| `osx-arm64`   | `macosx-x86_64` | `MoonTrader-version_<v>.tar.xz` | No native macosx-arm64 vendor build exists; the fetch script PE-patches the x64 dll to ARM64 (`Machine` field flip; managed IL is AnyCPU) |
| `osx-x64`     | `macosx-x86_64` | `MoonTrader-version_<v>.tar.xz` | dll lives inside `MoonTrader.app/Contents/Resources/Data/Managed/` |
| `linux-x64`   | `linux-x86_64`  | `MTCore-version_<v>.tar.xz`     | smaller headless tarball |
| `linux-arm64` | `linux-arm64`   | `MTCore-version_<v>.tar.xz`     | smaller headless tarball |

Vendor source: `https://cdn3.moontrader.com/beta/<channel>/`. Each channel
publishes a `version.txt` manifest with SHA-256 sums; the fetch script
verifies the download against it.

## Refreshing the vendor libs

```bash
python3 scripts/fetch_vendor_libs.py             # detects host RID
python3 scripts/fetch_vendor_libs.py --rid linux-x64
python3 scripts/fetch_vendor_libs.py --force     # ignore cache, re-download
```

The script downloads the tarball into `lib/.cache/`, verifies the SHA-256
against the vendor manifest, extracts `MTShared.dll` and `LiteNetLib.dll`
into `lib/<rid>/`, and PE-patches the dll to ARM64 when the RID is
`osx-arm64`.

The pinned vendor version is set by `VENDOR_VERSION` in
`scripts/fetch_vendor_libs.py`. Bump it together with any coordinated
wire-protocol updates to `Core/CoreConnection.cs`.

## Build integration

The csproj auto-runs the fetch script when `MTCoreVendorRid` resolves to
a non-default platform AND the RID-specific files are missing. To force
a refresh as part of the build:

```bash
dotnet build -c Release -p:FetchVendorLibs=true
```

To skip the auto-fetch (useful when working offline against the committed
baseline):

```bash
dotnet build -c Release -p:FetchVendorLibs=false
```

## When to expect platform problems

The committed baseline dll is PE-patched for `osx-arm64`. Building on
other platforms with that committed dll alone may fail at load time
because the dll's PE Machine field claims ARM64. Run the fetch script
for your host RID once before building, or pass `-p:FetchVendorLibs=true`
on the first build.

When the project's wire layer is upgraded to track vendor `0.7.23902`'s
API surface, the committed top-level `MTShared.dll` will be removed; the
per-RID layout becomes the single source of truth and the fetch script
will be the only way to populate `lib/`.
