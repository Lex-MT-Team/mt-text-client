# Security Policy

## Reporting a vulnerability

If you discover a security issue in `MTTextClient`, please **do not** open a
public GitHub issue.

Instead, email the maintainers privately:

```
security@moontrader.com
```

Include in the report:

* a description of the issue and its impact,
* the affected versions / commit SHAs,
* a minimal reproduction (commands, profile fragments, request payloads),
* whether you would like to be credited in the eventual advisory.

We aim to:

* acknowledge the report within **2 business days**,
* triage and confirm within **7 business days**,
* ship a fix or mitigation within **30 days** of confirmation for
  high-severity issues, longer for lower severity if a workaround exists.

## Scope

In scope:

* The `MTTextClient` codebase in this repository (REPL, MCP server, dashboard
  shipped under `web/`).
* Packaging, default configuration, and documented integration paths
  (stdio MCP, SSE proxy, web dashboard against localhost).

Out of scope:

* MTCore itself (report to MoonTrader directly).
* Third-party MCP clients integrating with this server.
* Vulnerabilities that require a pre-compromised local user, an attacker
  with arbitrary read on `~/.config/mt-textclient/`, or a co-resident
  malicious process — these are explicitly part of the trust boundary.
* Findings only reachable when CORS / `--disable-web-security` is
  intentionally enabled for local development.

## Hardening defaults to be aware of

* `profiles.json` is written at `0600` under a `0700` parent directory.
  If looser permissions are detected they are tightened on load and a
  warning is printed.
* Destructive MCP tools refuse to execute without `confirm:true`. Do not
  patch this gate out in downstream forks without re-implementing
  equivalent confirmation handling.
* The MCP gateway sanitizer rejects `\r` / `\n` in argument strings and
  enforces `inputSchema.required`. Both are part of the security contract.
* The web dashboard ships in development mode and expects a localhost SSE
  proxy. Do not expose it to untrusted networks without an authenticating
  reverse proxy.

## Supported versions

Only the latest minor release line receives security fixes. The current
supported line is `0.9.x`.

| Version | Supported          |
| ------- | ------------------ |
| 0.9.x   | :white_check_mark: |
| 0.8.x   | security fixes only until 2026-08 |
| < 0.8   | :x:                |
