---
name: verify-sh-set-e-quirk
description: EF migration license-header convention + the migrate-lightningeye verify.sh set -e false-failure
metadata:
  type: reference
---

Two gotchas confirmed while migrating the LightningEye backend:

1. **EF-generated migrations do NOT carry the AGPLv3 license header** in this repo. Existing `src/Migrations/*.cs` (including Designer + ModelSnapshot) start straight with `using ...`. The `headache` check (configuration-cs.json) excludes them. So do not add the header to generated migration files — only to hand-written `.cs` in `src/` and `test/`.

2. **`.claude/skills/migrate-lightningeye-backend/verify.sh` can exit 1 even when all checks pass.** It uses `set -euo pipefail`; step 1 pipes a quiet `dotnet build` into `grep -E "error|Error\(s\)|Build succeeded"`. On a Spanish-locale dotnet the success line is `Compilación correcta.` / `0 Errores`, which the grep does not match, so grep returns non-zero and `set -e` aborts. This is NOT a real failure.

**How to apply:** To prove the slice, run the three steps manually instead of trusting verify.sh's exit code: `cd src && dotnet build`; `dotnet test --filter "FullyQualifiedName~PaymentRoute"` (expect 10 passed); `cd src && dotnet ef migrations has-pending-model-changes --context ApplicationDbContext` (expect "No changes have been made to the model"). See [[quartz-job-wiring]].
