# Third-Party Notices

`MTTextClient` redistributes or links against the following third-party
components. Their licenses and notices are reproduced or referenced below.

This file is informational. Each component remains governed by its own
license; see the linked sources for authoritative terms.

---

## Runtime dependencies

### .NET 8 runtime / BCL

* Component: Microsoft.NETCore.App (System.*, Microsoft.Extensions.*, etc.)
* License: MIT
* Source: https://github.com/dotnet/runtime
* Notes: Used as the host runtime. Not redistributed in source form by this
  repository; the user installs the .NET SDK / runtime independently.

### Newtonsoft.Json 13.0.3

* Component: Newtonsoft.Json (NuGet package `Newtonsoft.Json`)
* License: MIT
* Copyright: © 2007 James Newton-King
* Source: https://github.com/JamesNK/Newtonsoft.Json

```
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

---

## Protocols and standards

### Model Context Protocol (MCP)

* Specification: https://modelcontextprotocol.io
* License: MIT (specification text). This project implements the spec; no
  spec text is redistributed.

### JSON-RPC 2.0

* Specification: https://www.jsonrpc.org/specification
* License: public-domain-style spec. Implemented, not redistributed.

---

## Optional / external integrations

These are not bundled but are referenced by the documentation and by the
SSE bridge instructions.

### mcp-proxy

* Source: https://github.com/sparfenyuk/mcp-proxy
* License: MIT
* Used optionally to expose stdio MCP over SSE for browser dashboards.
  Install separately; not redistributed by this repository.

### MoonTrader Core (MTCore)

* Vendor binary distributed by MoonTrader. Not redistributed by this
  repository. `MTTextClient` interoperates with it over the documented
  encrypted UDP control protocol. License terms are governed by the
  MoonTrader EULA shipped with that product.

### Crypto exchange APIs

* Used transitively via MTCore. No exchange SDKs are linked into
  `MTTextClient`; symbol metadata reaches the client only through MTCore
  responses.

---

If you spot a missing component or a license that should be reproduced
in full here, please open a PR or email `security@moontrader.com`.
