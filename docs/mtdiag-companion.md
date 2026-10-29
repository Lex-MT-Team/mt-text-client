# MTDiag companion operator guide

`MTDiag.Companion` is an optional, read-only diagnostic listener. It serves a
separate diagnostic port for each explicitly configured saved profile and does
not change ordinary MTCore connections. It reads an existing `core.auth` file
only to derive the native connection seed transiently; it has no credential
store, token copier, or alternate authentication scheme.

This guide is an operator procedure, not an installer. Do not run its lifecycle
commands until the configuration and every file permission have been reviewed
for the intended host.

## Scope and delivery status

The companion is a standalone C# executable that uses the repository's pinned
MTShared and LiteNetLib assemblies and the same pinned Newtonsoft.Json version
as its companion project. It does not require a Go installation or introduce a
new runtime package for a self-contained candidate.

Self-contained means the .NET runtime is included; it does not remove native
operating-system dependencies. The companion's file reader supports macOS and
Linux x64/arm64 and refuses unsupported platforms. Verify the selected Linux
distribution's system libraries, including its compatible C runtime, OpenSSL,
and ICU, before adoption. See Microsoft's [Linux dependency guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

Local checkpoint export (`diag history collect`) also requires a supported
POSIX host because its private-file guards and exclusive writer lock use POSIX
file APIs. Remote page and fleet calls do not require a local companion.

Target-platform operating-system prerequisites and execution evidence remain
operator validation work. In particular, cross-published artifacts must not be
treated as executed on their target platform until they have been tested there.
This guide makes no claim about candidate size, startup time, memory use, CPU
use, or Linux execution.

## Historical investigation: an earlier incident

The `mt_diag_investigation_*` tools remain finite current snapshots. They cannot
reconstruct eight hours ago. Use history for retained Core logs and Guardian
events, and use snapshots separately for current health/context. No diagnostic
reader can recover deleted logs or prove activity that was never recorded.

History uses the same native authentication and separate companion port. New
companions advertise `extensions.history_v1: true` without adding names to the
legacy `capabilities` array. Old clients still negotiate their original five
operations; a new history client explicitly reports unsupported capability when
connected to an old companion.

For an incident eight hours ago, choose an explicit UTC interval with context
before and after the incident. Start one query per profile **and source**. For
example, an MCP client can call `mt_diag_history_page` with:

```json
{"profile":"saved-profile","source":"logs","since":"2026-01-01T06:00:00Z","until":"2026-01-01T10:00:00Z","query_id":"0123456789abcdef0123456789abcdef","page_lines":200,"diag_port":4343}
```

Generate a fresh random 32-hex `query_id` for each real investigation. Persist
that ID before the first request. Keep source, since, until, page_lines, unknown_time and ID
unchanged; use the returned `cursor` and `next_sequence` for continuation.
Retrying an uncertain response uses the **same** query and sequence and replays
the last page; advancing the sequence acknowledges the previous page. Only the
last page is replayable. Do not run two concurrent consumers of one cursor.
Keep paging through empty pages while `has_more` is true. End when false, inspect
coverage, and release the cursor with `mt_diag_history_close` using its original
query and cursor. A timeout must not silently start a new query.

`mt_diag_history_fleet` accepts an explicit `requests` array of these query
objects, including each `profile`, `page_lines`, `cursor` (null initially) and
`sequence` (zero initially). Each invocation obtains at most one page per
selected profile with bounded concurrency (default 8). Persist successful pages
and each profile's continuation separately, and repeat only pending profiles.
A successful fleet call means validated pages, **not completed time windows**.
Failures retain individual outcomes and produce `success: false`. There is no
automatic retry. Apply backoff to `busy` without advancing sequence; a lost
reply can be replayed. Do not put an entire multi-day fleet history into an LLM
context at once: retain pages externally, correlate timestamps and stable IDs,
and supply relevant spans with their coverage/gap metadata.

For local streaming, the CLI writes one sanitized NDJSON page per file and an
atomic manifest instead of accumulating the whole window:

```sh
MTTextClient diag history collect saved-profile --source logs --since 2026-01-01T00:00:00Z --until 2026-01-03T00:00:00Z --query-id 0123456789abcdef0123456789abcdef --page-lines 200 --diag-port 4343 --output /private-history/incident-core --deadline-ms 60000
```

Use a new, empty, owner-only output directory with no symlink path components;
new directories/files are created with 0700/0600 modes. POSIX macOS/Linux are
supported. The manifest contains sanitized query/progress and an endpoint hash,
not a token or raw endpoint. Each page is committed before the manifest, and a
repeat of the identical command adopts an already committed page after a crash.
One process holds a nonblocking filesystem writer lock. Ctrl+C, timeout or a
per-invocation deadline retains completed pages; repeat the command to resume.
Each invocation lasts at most 60 seconds plus local file/cleanup overhead;
there is **no total page, byte, record, file-count or elapsed-history cap**.
`complete: false` exits 1, including a resumable deadline or terminal gaps.
Timeouts consistently report `stop_reason: "deadline-exceeded"`. `stop_scope`
is `collection` when the invocation budget expires, or `request` when a shorter
per-request budget expires; it is `caller` for cancellation and null for other
stops. Both deadline outcomes preserve the same explicit resume procedure.
The CLI names out-of-range numeric options and missing/empty `--output` values.
A new output directory is created owner-only; filesystem/checkpoint failures
name the output/checkpoint category without printing a path or exception.
Manifests tolerate process interruption; power-loss durability is filesystem
specific. Do not edit the manifest or concatenate pages out of sequence.

The cursor has a renewable five-minute idle lease and lives in the companion,
not the CLI process. After companion restart, expiry or `history-source-changed`,
preserve the output and explicitly start a fresh query in a new directory.
Record IDs identify device/inode and byte offset within a profile/source;
use them to compare overlapping traversals while also checking mutation gaps.
They are not a durable archival identity across inode reuse/copy-truncation.
No query can extend retention or reconstruct an overwritten generation.

### Coverage and bounded work

The half-open window is `[since, until)`. Each page states observed returned
and scanned timestamp ranges, cumulative scanned/returned bytes and records,
unknown times, omissions, files opened, gaps and source exhaustion. `complete`
means configured retained sources were exhausted without an observed gap.
`requested_window_complete` remains null and `retention_attested` false: a
reader cannot prove that all historical files still exist. `start_boundary` distinguishes
no timestamped records, an observed record at/before `since`, and an unproven
period before the oldest observed record. Even the first case with an old record
does not attest continuity or retention. No-result and
unknown-time records must not be described as proof of no activity.

Files are read up to their size when opened. Discovery is incremental, not a
global atomic snapshot. Growth beyond a plain file's captured boundary adds
`file_grew_after_open_boundary` to `coverage.warnings` and is excluded from that
query. It does not alone mark the captured prefix incomplete. Growth is not
proof that the prefix was unchanged: simultaneous overwrite plus append cannot
be ruled out. Other observed modifications, shrinkage and directory changes
remain gaps; rename/delete of an open file retains its descriptor.
Concurrent replacements, copy-truncation and changes after a directory has
already been exhausted cannot provide a globally consistent historical view.
Reconcile gaps with operator retention/rotation evidence before drawing a
complete incident conclusion. Results follow source traversal order; correlate
by timestamps rather than assuming global chronological ordering. No mtime
or first out-of-range timestamp shortcut discards older/nonmonotonic records.
Cold queries still scan configured retained files; a one-minute window is not
guaranteed to finish within one 60-second invocation. Resume the checkpoint as
needed. `extensions.history_index_v1` advertises a bounded, memory-only index
learned during completed file reads. Warm queries can skip whole unchanged
files whose verified timestamp range is outside the window, including gzip
archives, and seek past non-overlapping blocks in plain files. Every record's
time contributes to the range; neither filename dates nor timestamp ordering
are trusted. Unknown-time records, omissions, incomplete scans and unverified
archives prevent caching that file.

The index keeps at most 128 files and 8,192 blocks per configured profile, with
at most 1,024 blocks per file. Larger plain files retain only a whole-file
summary. It stores metadata and time ranges, not log text, and creates no disk
index or background job. Restart, eviction or any descriptor generation change
(device/inode, size, permissions, links, modification/change time including
nanoseconds) causes a cold fallback. Changes observed during an indexed read
invalidate that cursor. These filesystem checks are not a cryptographic or
atomic retention guarantee. Frequently appended files may remain cold.

Coverage totals include both freshly processed input and validated index reuse.
`scan_accounting=source_reads_plus_validated_index` makes this explicit:
subtract `index_bytes_reused` and `index_records_reused` from the corresponding
totals to obtain freshly processed logical input. These are not physical disk
I/O counters. `page_index_records_reused`, `indexed_files_skipped` and
`gzip_verifications_reused` identify reuse on the page, whole-file skipping and
previously verified archives. An overlapping gzip member still requires
sequential decompression; there is no within-member gzip seek index or latency
guarantee independent of retained volume.

Per page: at most 500 records, 512 discovered entries, approximately
38 KiB serialized records inside the existing 48 KiB response limit, and about
1.2 seconds of traversal work. Scan-only pages can consume up to 4 MiB of logical
input to traverse old prefixes efficiently; once records are returned, the loop
target is 256 KiB, with a single physical-record scan still bounded by 4 MiB.
These bounds can yield progress-only pages.
Physical records over 16 KiB or invalid UTF-8 are explicitly omitted with a gap;
secrets cannot leak through split fragments. Multiline stack continuations
inherit a known preceding time; orphan or ambiguous records remain unknown.
Filesystem nesting beyond 64 active directories becomes an explicit resource
gap. Up to four active cursors per listener retain a single current file each;
terminal pages release descriptors immediately. Up to 16 total active/replay
entries retain last-page replay for the idle lease; completed entries may be
evicted under pressure and no longer occupy active cursor slots. Explicit close
also releases the replay entry. History has a separate 6000-request/minute
listener budget, capabilities 1200/minute; existing tail operations retain their
30/minute budget and all operations share existing in-flight admission limits.
These are resource limits, not total history depth limits.

Offsetless Core timestamps require explicit startup `historyTimeZone` (for
example `UTC` only when the source is actually UTC). Missing zones and ambiguous
or nonexistent DST wall times are returned as unknown, never guessed. Guardian
JSON `ts` timestamps with explicit offsets are interpreted directly. Optional
`guardianHistoryRoots` plus `guardianHistoryGlobs` allow retained rotated Guardian
files; both must be configured together. Only configured roots/globs and the
explicit `guardianEventPath` can be read; callers never supply remote paths.

History also discovers `.gz` counterparts of allowed filename patterns: `*.log`
permits `*.log.gz` within the same approved roots. It streams gzip through .NET's
built-in library without extraction files, new packages or runtime installers.
Record `source_encoding` is `gzip` and `offset_kind` is `uncompressed_bytes` for
archives; ordinary files use `utf8` and `file_bytes`. The uncompressed offset and
file identity remain stable during replay. `total_bytes_scanned` counts logical
bytes, and `compressed_files_opened` counts accepted archive streams. Bad gzip,
CRC/size disagreement, truncated streams and excessive compressed-input work
produce explicit gaps. The integrity check supports single-member archives;
concatenated members can be read but are marked unverified, never complete.
`gzip_files_verified` counts archives whose full decoded stream passed the final
CRC/size check. `gzip_integrity_failures` counts invalid, failed or unsupported
archive validation. Before that end check, decoded archive rows are provisional;
consume terminal coverage before calling the archive verified. Failed end checks
report `gzip_integrity_failed_or_unsupported`: truncation and unsupported valid
concatenation are deliberately not conflated with verified single-member files.
An unfinished final record at an unverified gzip end is omitted with
`content_omitted: true` and `gzip_unverified_partial_record`; its broken text is
never emitted. A verified archive may legitimately end without a newline, and
that complete record is retained. Earlier decoded rows remain available with
explicit final integrity gaps; no already-committed page is silently rewritten.
Each page additionally limits compressed input to 8 MiB per open archive.

Unknown-time rows default to `unknown_time: "include"`. For a focused window,
A caller may explicitly choose `unknown_time: "report-only"` (CLI
`--unknown-time report-only`): those rows are counted in `unknown_time_records`
and `unknown_time_excluded` but their unrelated text is not returned. Their
coverage gap remains visible; this option cannot manufacture complete evidence.
The choice is immutable for the cursor/checkpoint. It requires authenticated
`extensions.history_options_v1`; default requests omit the option and remain
compatible with earlier history companions. `extensions.history_gzip_v1` signals
archive support; absence means older companions may omit compressed retention.

A single client process reuses authenticated history connections and negotiates
capabilities once per connection. Requests for one profile/route/credential and
identity-evidence binding serialize, each with a fresh deadline including queue
wait. Changing credentials, route or identity evidence requires a new session.
There are at most 128 cached bindings. Idle sessions close after 20–40 seconds,
and CLI/MCP shutdown closes owned sessions. Timeouts, cancellation and invalid
replies discard the uncertain connection without retrying a history request.
Tail/snapshot calls retain their existing short-lived sessions. Independent
processes still compete for the companion's four peer slots: use one persistent
MCP process for fleet history and back off on admission refusal.

## Configure an explicit listener

The executable starts with an explicit configuration:

```sh
MTDiag.Companion --config /etc/mtdiag/companion.json
```

`MTDiag.Companion --help` prints this usage and exits without loading a
configuration. It is suitable for checking a copied candidate's basic startup
path, but it does not prove a configured listener is healthy.

The configuration is strict JSON. Its root has only `profiles`; it accepts one
through 128 profile objects. Each object permits only these keys:

| Key | Required | Meaning |
| --- | --- | --- |
| `name` | yes | Saved-profile label; letters, digits, `.`, `_`, and `-` only. |
| `exchange` | yes | Existing MTShared exchange name. |
| `corePort` | yes | Saved profile's MTCore UDP port. |
| `diagnosticPort` | yes | Separate companion UDP port. It must not equal any configured Core port. |
| `coreAuthPath` | yes | Path to an existing file named `core.auth`. |
| `logRoots` | yes | Up to 16 allowed log-root paths. |
| `logGlobs` | yes | One through 16 filename-only patterns permitted below those roots. |
| `historyTimeZone` | no | Explicit timezone ID for offsetless historical timestamps; no assumed default. |
| `guardianHistoryRoots` | no | Allowed rotated Guardian history roots; requires matching globs. |
| `guardianHistoryGlobs` | no | Filename-only patterns for Guardian history; requires matching roots. |
| `guardianSnapshotPath` | no | One approved Guardian snapshot file. |
| `guardianEventPath` | no | One approved Guardian event file. |
| `guardianProcessName` | no | One approved Guardian process name. |

Relative paths are resolved from the configuration directory; absolute paths are
allowed for reviewed inputs. The configuration and its referenced `core.auth`
file must be regular files, and the configured inputs must not cross a symlink
component. The only narrow exception is a platform's canonical temporary-root
alias used by local tooling; arbitrary symlink ancestors remain rejected. On non-Windows hosts,
`core.auth` cannot grant group or other permissions. Keep the configuration and
every referenced input readable only by the account that runs the companion.

For a configuration, auth-validation, regular-file, symlink, or owner-only-permission rejection,
startup prints a fixed safe category rather than a path, raw exception, or
credential detail. Correct the reviewed input; for example, an auth file with
mode `0664` must be returned to owner-only access rather than made more broadly
readable.

The following is synthetic documentation data, not a reusable credential or
environment definition. The port values are examples only; choose unused ports
that do not collide with a saved MTCore endpoint.

```json
{
  "profiles": [
    {
      "name": "example-profile",
      "exchange": "BINANCE",
      "corePort": 41001,
      "diagnosticPort": 41002,
      "coreAuthPath": "/srv/moontrader/example/core.auth",
      "logRoots": ["/srv/moontrader/example/logs"],
      "logGlobs": ["*.log"],
      "guardianSnapshotPath": "/srv/moontrader/example/guardian.snapshot",
      "guardianEventPath": "/srv/moontrader/example/guardian.events",
      "guardianProcessName": "example-guardian"
    }
  ]
}
```

Never put a token, decoded auth content, connection seed, or copied credential
in this file. Reference the existing owner-controlled `core.auth` file instead.
Do not add legacy transport fallbacks or remote-control configuration.

Before starting a listener, run this local check as the existing auth-file owner:

```sh
/opt/mtdiag/MTDiag.Companion --check-config /etc/mtdiag/companion.json
```

The check validates configuration, file guards and every configured auth seed,
then exits without binding a port, contacting MTCore, printing credentials or
changing files. Success does not establish network reachability, log readability
or Guardian liveness. Relative paths resolve against the configuration folder;
explicit absolute paths may be outside it. There is no need to put configuration
at the filesystem root.

On supported POSIX hosts, `core.auth` must be owner-readable with no group or
other permissions, normally `0400` or `0600`. Existing `0644`, `0640` and `0664`
files are deliberately refused. Have the operator review the existing owner and
all legitimate readers before tightening the original file's permissions; run
the companion as that owner. Do not copy credentials, widen permissions, change
ownership blindly or use a second credential store to bypass this guard. The
check never performs `chmod` itself. Configuration/auth rejection exits with
status `78`; invalid command-line usage exits with status `2`.

## Route from MTTextClient

MTTextClient always uses the address of the selected saved profile. It does not
accept an arbitrary diagnostic host. Choose one of these route sources:

1. Supply `--diag-port` on the read-only diagnostic command. This explicit value
   takes precedence.
2. Set the saved profile's `mtdiag_port` tag to the companion port.

One-shot `MTTextClient diag ...` commands emit the complete JSON result without
the interactive banner or table-cell truncation. MCP uses the same complete
result. In an interactive REPL, select `output json` before inspecting nested
diagnostic evidence; table mode is a compact presentation, not an evidence export.
One-shot diagnostics exit `1` when the command fails or a fleet result contains
an unvalidated requested row, and `0` otherwise. The complete JSON is still
returned on failure. A zero exit status does not assert source freshness,
availability or Core/Guardian liveness; inspect those evidence fields separately.
Fleet JSON `success` is false whenever a requested row is unvalidated, and MCP
sets `isError` accordingly. All per-profile rows and coverage remain available
in the structured result, including successful rows from a partial collection.

The client refuses a diagnostic port equal to that profile's Core port and
refuses a port that is already a saved MTCore endpoint at the same address.

Before requesting any evidence, prove the native route and negotiated surface
with capabilities. This synthetic command uses an explicit port:

```sh
dotnet run --project MTTextClient.csproj -- diag capabilities example-profile --diag-port 41002
```

After a successful capabilities response, use only the operations advertised by
that response. The available read-only operations are `health`, `logs`,
`guardian-status`, and `guardian-events`; log and event reads require a bounded
`--lines` value from 1 through 500. Log reads may also use a bounded `--grep`
text filter. A capabilities response establishes the companion's negotiated
surface at that moment; it cannot preflight later packet reachability. Fleet
collection has one shared deadline and returns a row for every requested saved
profile, so an expired deadline is partial evidence rather than proof that a
port is closed. In fleet coverage, `rows_returned` counts all attributed rows;
`completed` counts profiles for which at least one exchange attempt finished
(including failed or timed-out attempts); `not_attempted` counts profiles with
no attempt, including local refusals and entries that expired in the queue.
`attempted` is the total exchange count, including retries; each row also carries
an `attempted` boolean and its attempt list. Use `validated` and source-availability
counters to assess evidence, rather than interpreting `completed` as success.
The matching MCP surface is `mt_diag_capabilities`,
`mt_diag_health`, `mt_diag_logs`, `mt_diag_guardian_status`, and
`mt_diag_guardian_events`; each requires a saved profile and never accepts a
caller-provided source path or host.

Successful replies remain bound to the selected saved profile. If an otherwise
valid authenticated request names a different bounded profile label, the
companion returns a correlated `profile-unbound` refusal with no source data or
metadata. An explicit native transport rejection is returned promptly as
`native-connection-rejected`, without reflecting rejection bytes or retrying it.
Listener capacity, credentials/exchange and network policy can cause admission
refusal. Reduce competing clients and back off before investigating credentials;
rejection is not authenticated proof of endpoint identity or a bad token.
If no rejection or valid reply arrives, a malformed request, wrong token,
unavailable companion and filtered UDP can still be timeout-ambiguous. Fixed
`next_action` guidance is included on single and fleet failure output. A row
with no attempt makes no claim about remote reachability.

Authorize the separate diagnostic UDP port from the intended caller in both
network and host policy, and validate capabilities from that caller before
requesting fleet evidence. If the selected diagnostic port is `4343`, use
`--diag-port 4343` or the matching saved tag consistently. Opening TCP does not
enable UDP. Never reuse MTCore's port or infer fleet reachability from a local
call on the target host.

## Read evidence correctly

`health` is an observation about the companion listener, not a statement that
any Guardian or Core process is live. Log results describe bounded, allowlisted
source data and may be redacted, truncated, unavailable, or permission-denied.
They are not a general file reader. A log response keeps the newest fitting
entries when its response budget is reached. Matching files are considered by
newest modification time first (filename order breaks ties), and the requested
tail is retained in each selected file's source order; output is therefore not
a globally chronological merge. A `grep` is evaluated against bounded file data
before the requested line tail is selected. `searchTruncated` is true when the
search did not cover all configured input, but an empty result never proves an
absence outside those bounds.

Guardian snapshot content, Guardian event time or age, and any process
observation are separate claims. A recent file does not prove a process is
alive; a process observation does not prove a snapshot is current. A
`not_observed` process result means only that the configured operating-system
process name was not found; it does not prove Guardian is stopped. Guardian
event entries preserve their configured file's source order, and a response
budget trims their oldest retained entries first. Preserve each returned
observation and timestamp as separate evidence when investigating freshness.
Explicit Guardian event reads accept the same 1–500 range as the client and
protocol; the status operation retains its default 100-entry history. Event
tail limits and oversized entries set `eventHistory.truncated`; byte-budget
trimming also sets `responseTruncated`, keeping the newest fitting events.

Redaction preserves bare PascalCase dotted diagnostic identifiers such as
`WS.State` and method symbols in `at namespace.method(...)` stack frames.
Explicit URLs, labelled host/address/endpoint fields, host:port values, IP
addresses and known credentials remain redacted. Bare dotted text can be
ambiguous: PascalCase symbols are treated as code rather than inferred network
addresses, without DNS lookups. A known sensitive token is always redacted even
when its text has the shape of a code identifier.
Generic-arity segments in stack frames and versioned relative API routes such
as `POST v5/order/create` are preserved. Query strings and fragments remain
masked. The full remainder of a line labelled `Internal Core Heap Data:` is
masked as opaque evidence; no fragment of its unknown payload is exposed.

Current log and Guardian reads return a bounded newest batch. Repeating a read
does not page backward. Investigation tools collect current snapshots (50 log
lines and 50 Guardian events per collection), with bounded event follow-ups;
they do not retrieve a historical time window. Use the separate
`mt_diag_history_*` tools and the history procedure above for retained windows.

## Optional one-process multi-profile service

One companion process may contain multiple explicit profile entries, with a
different diagnostic port for each. This is optional; it does not discover
profiles or listen on unspecified ports. Each profile retains its own existing
`core.auth` reference and evidence allowlists.

[`mtdiag-companion.service.example`](../Core/Diagnostics/Companion/packaging/mtdiag-companion.service.example)
is a synthetic systemd unit for that arrangement. It assumes that the existing
unprivileged `moontrader` account already owns the configured owner-only
`core.auth` file and can read the listed evidence inputs. Keep that existing
credential custody: do not create a second reader account, copy the auth file,
or widen its group or world permissions. Review and replace every synthetic path
together; do not broaden the account to a service directory or home directory.

The unit's `ReadOnlyPaths` entries make the listed mounted paths read-only. They
do not grant file-system read permission and do not make an unrestricted source
reader safe; the strict companion configuration is the source allowlist. Its
Guardian file entries use the systemd optional-path prefix so an omitted optional
Guardian input does not prevent the companion from starting. Remove the matching
configuration key and its service-path entry when that evidence source is not
configured. `ProtectHome=read-only` keeps reviewed home-directory inputs
readable without making them writable. The example limits automatic restarts to
three attempts in 60 seconds; it is not a health assertion for a target host.
`RestartPreventExitStatus=2 78` prevents automatic restarts for usage and
configuration/auth errors; correct those before starting again. Other immediate
failures, such as an incompatible replacement executable, remain start-limited.
These directives follow the [systemd restart policy](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html#RestartPreventExitStatus=).

The unit starts only `MTDiag.Companion --config ...`. It does not install,
restart, stop, configure, or otherwise manage MTCore or Guardian.

Self-contained single-file candidates extract native runtime files at startup.
The example uses systemd's `StateDirectory=mtdiag` with mode `0700` and an
explicit `DOTNET_BUNDLE_EXTRACT_BASE_DIR` below it. This gives the existing
service account a private writable cache while keeping the rest of the
filesystem protected. Do not use a shared temporary directory or rely on an
unavailable service home. See Microsoft's [single-file deployment guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

## Operator-controlled lifecycle

The following commands are instructions for an authorized operator; this guide
does not execute them or install a unit automatically.

### Install and verify

1. Place the reviewed executable and configuration in the locations selected by
   the operator. Set the companion configuration and `core.auth` permissions so
   only their intended owner can read them. Run `--check-config` as that existing
   owner before starting the unit; repeat it after an auth or configuration change.
2. Copy the reviewed example unit to the systemd unit directory, after replacing
   its synthetic paths with the exact configured input paths.
3. Ask systemd to reload unit definitions, then enable and start this one unit:

   ```sh
   systemctl daemon-reload
   systemctl enable --now mtdiag-companion.service
   ```

4. Verify the unit status and perform the capabilities-first command above. A
   failed diagnostic request is not a reason to restart or reconfigure MTCore or
   Guardian.

### Upgrade

1. Record the known-good companion artifact identity and retain it for rollback.
2. Stop only `mtdiag-companion.service`:

   ```sh
   systemctl stop mtdiag-companion.service
   ```

3. Replace the companion artifact, and
   review the configuration against the strict schema before starting that same
   unit again.

   ```sh
   systemctl start mtdiag-companion.service
   ```

4. Re-run capabilities before any bounded evidence request. If it fails, use
   rollback rather than changing credential custody or Core/Guardian ownership.

### Roll back

1. Stop only `mtdiag-companion.service`.
2. Restore the retained known-good companion artifact and its reviewed unit
   definition, then reload systemd definitions if the unit changed.

   ```sh
   systemctl daemon-reload
   systemctl start mtdiag-companion.service
   ```

3. If a deliberate rapid lifecycle test reached the start limit, first verify the
   restored binary with `--check-config` as its owner. Then run `systemctl
   reset-failed mtdiag-companion.service` followed by `systemctl start
   mtdiag-companion.service`. Confirm capabilities using the same explicit route.
   Do not automatically reset a failing service or weaken the restart budget.
   Space planned stop/start tests or apply this recovery after the cause is fixed.

### Idle-resource verification

Validate an idle candidate on the intended Linux target before relying on any
resource figure. Measure process CPU-time delta against wall time and compare
allocated bytes, collection counts, and retained working state after warmup and
again after a bounded idle interval. A macOS measurement, including a successful
startup check, does not establish Linux CPU or RSS behavior.

### Remove

1. Stop and disable only `mtdiag-companion.service`:

   ```sh
   systemctl disable --now mtdiag-companion.service
   ```

2. Remove the companion artifact, its unit file, and the companion configuration
   only after preserving evidence required by the operator. Do not remove or
   alter MTCore, Guardian, their configurations, or their existing credential
   files.

   ```sh
   rm -f /etc/systemd/system/mtdiag-companion.service
   systemctl daemon-reload
   ```

3. After reloading systemd definitions, confirm that
   diagnostic requests are unavailable while the ordinary MTCore service remains
   unchanged.
