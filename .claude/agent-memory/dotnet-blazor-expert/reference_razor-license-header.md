---
name: razor-license-header
description: .razor files carry NO AGPLv3 header in this repo; configuration-cs.json includes only .cs
metadata:
  type: reference
---

`.razor` files do NOT get the AGPLv3 license header in NodeGuard. `configuration-cs.json`
(the `headache` config driving `just add-license-cs`) has `includes: ["src/**/*.cs", "test/**/*.cs"]`
— only `.cs`, not `.razor`. Sampled existing pages (AuditTrail, Channels, Wallets, Nodes) all
start directly with `@page`, no header.

**Why:** The header check tool only scans `.cs`. Running `just add-license-cs` is a no-op for
`.razor` and would re-touch every `.cs` header (churn).

**How to apply:** When creating a new `.razor` page, do NOT add a license header and do NOT run
`just add-license-cs` for it. Only new `.cs` files under `src/`/`test/` (outside
`src/Areas/Identity/Pages/`) need the header. This corrects skill/template claims that
".razor files are covered too" — they are not. Complements [[migration-header-and-verify]].
