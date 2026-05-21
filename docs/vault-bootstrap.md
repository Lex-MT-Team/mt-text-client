# Vault Bootstrap

This document explains how to set up a local HashiCorp Vault for the
`mt_vault_*` MCP tool family, and how to wire the dev token into the
test suite and into mt-text-client at runtime.

## Tools

| Tool                       | Purpose                                                   |
|----------------------------|-----------------------------------------------------------|
| `mt_vault_list_profiles`   | List stored exchange API profile names.                   |
| `mt_vault_store_profile`   | Store `{api_key, api_secret}` under a profile name.       |
| `mt_vault_get_profile`     | Retrieve `{api_key, api_secret, stored_at}` for a profile. |
| `mt_vault_delete_profile`  | Permanently destroy a profile (KV v2 metadata + all versions). Requires `confirm=true`. |

## 1. Run Vault in dev mode

The repo's docker-compose currently runs a service named `nexus-vault-1`
using the upstream `hashicorp/vault:latest` image in `-dev` mode with a
fixed root token `nexus-dev-token`.  If it isn't running, start it:

```bash
# If the container already exists:
docker start nexus-vault-1

# If you need to create it (matching the existing configuration):
docker run -d --name nexus-vault-1 \
  --cap-add=IPC_LOCK \
  -e VAULT_DEV_ROOT_TOKEN_ID=nexus-dev-token \
  -e VAULT_DEV_LISTEN_ADDRESS=0.0.0.0:8200 \
  -p 8200:8200 \
  hashicorp/vault:latest
```

Sanity check:

```bash
curl -s -m 3 http://127.0.0.1:8200/v1/sys/health | jq .
```

`initialized: true`, `sealed: false` means it's ready.

## 2. Provide the token to mt-text-client

Two environment variables are read at request time by the Vault
handlers in `MCP/McpServer.cs`:

- `VAULT_ADDR` — defaults to `http://127.0.0.1:8200` if unset.
- `VAULT_TOKEN` — no default; without this, every call will return
  `Vault HTTP 403`.

For local development, export the dev token in your shell:

```bash
export VAULT_TOKEN=nexus-dev-token
```

Every `mt_vault_*` call also accepts inline `vault_token` and
`vault_addr` arguments that override the env vars — useful when a
caller wants to point at a different Vault instance without restarting
the MCP subprocess.

## 3. Provide the token to the test suite

The xunit Vault tests (`tests/MTTextClient.Tests/Tools/VaultTests.cs`
and `tests/MTTextClient.Tests/LiveTrade/VaultProfileRoundtripLiveTradeTests.cs`)
resolve the token from either `MTC_VAULT_TOKEN` or `VAULT_TOKEN`, in
that order.  Tests skip cleanly with a clear message when neither is
set, so a fresh checkout doesn't fail.  To run them:

```bash
export MTC_TESTING_ENV=1
export MTC_VAULT_TOKEN=nexus-dev-token

dotnet test tests/MTTextClient.Tests/MTTextClient.Tests.csproj \
  -c Release --no-build \
  --filter "FullyQualifiedName~VaultTests"
```

For the LiveTrade round-trip:

```bash
export MTC_LIVE_TRADES=1
dotnet test tests/MTTextClient.Tests/MTTextClient.Tests.csproj \
  -c Release --no-build \
  --filter "FullyQualifiedName~VaultProfileRoundtripLiveTradeTests"
```

## 4. KV v2 layout (informational)

The store handler writes to `secret/data/mt/profiles/{name}` and the
list handler reads from `secret/metadata/mt/profiles/` (KV v2's
list-via-metadata path).  Delete uses
`DELETE /v1/secret/metadata/mt/profiles/{name}` which removes every
version and the metadata itself — there is no soft-delete path.

A successful store envelope looks like:

```json
{ "status": "ok", "profile": "<name>" }
```

A successful get envelope:

```json
{
  "name":       "<name>",
  "api_key":    "<key>",
  "api_secret": "<secret>",
  "stored_at":  "<iso-utc>",
  "version":    1
}
```

A successful delete envelope:

```json
{ "status": "deleted", "profile": "<name>" }
```

A "profile not found" envelope (returned by `get` after delete, or by
`get` against a name that never existed):

```json
{ "error": "profile_not_found: '<name>' has no record in Vault at secret/mt/profiles/<name>" }
```

## 5. Beyond dev mode

For non-dev Vault, the only change is the token: provision a token
with `read+create+update+delete` on `secret/data/mt/profiles/*` and
`list` on `secret/metadata/mt/profiles/`.  Everything else continues
to work without code changes.
