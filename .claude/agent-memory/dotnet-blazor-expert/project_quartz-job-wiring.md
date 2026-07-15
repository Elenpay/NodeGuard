---
name: quartz-job-wiring
description: How Quartz jobs are actually registered in NodeGuard, and a stale CLAUDE.md/skill claim to ignore
metadata:
  type: project
---

Quartz jobs are registered ONLY in `src/Program.cs` inside the `builder.Services.AddQuartz(q => { ... })` block, as paired `q.AddJob<T>(...)` + `q.AddTrigger(...)` calls. There is no job-type registry/enum to update.

**Why:** CLAUDE.md and the migrate-lightningeye-backend skill both say to "wire the type through `src/Helpers/JobTypes.cs`". That is stale — `JobTypes.cs` contains only the `SimpleJob` / `RetriableJob` / `JobAndTrigger` helper classes (identical content to `SimpleJob.cs`), no enum or type map. Adding a job there is unnecessary and there is nothing to add.

**How to apply:** When adding a scheduled monitor job, model it on `MonitorSwapsJob` (single `IJob` execution that iterates `INodeRepository.GetAllManagedByNodeGuard(false)` and injects repos/services directly), NOT `MonitorChannelsJob` (which fans out per-node sub-jobs via `SimpleJob.Create`). For dev/prod interval, the inline `if (Constants.IS_DEV_ENVIRONMENT) WithIntervalInMinutes(1) else WithIntervalInMinutes(10)` pattern (as in MonitorSwapsJob) is self-contained and additive — no new `Constants.*_CRON` needed. Mark `[DisallowConcurrentExecution]` on the class and also `opts.DisallowConcurrentExecution()` at registration. See [[verify-sh-set-e-quirk]].
