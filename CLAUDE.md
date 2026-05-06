# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Common commands

Most workflows are wrapped in [.justfile](.justfile). Run `just` recipes from the repo root — they `cd` into `src/` themselves where needed.

**Run / build**
- `just run` — extracts macaroons via [docker/extract-macaroons.sh](docker/extract-macaroons.sh), then `dotnet run`. UI at `http://localhost:38080`.
- `just watch` — same, with hot reload.
- `just build`, `just stop`, `just format`.

If you invoke `dotnet run` / `dotnet watch` directly (not through `just`), run [docker/extract-macaroons.sh](docker/extract-macaroons.sh) first — without it, [src/nodeguard-macaroons.env](src/nodeguard-macaroons.env) is missing and LND clients fail to connect.

**Tests**
- All: `just test` (or `dotnet test` from repo root).
- One class: `dotnet test --filter "FullyQualifiedName~LightningServiceTests"`.
- Test project: [test/NodeGuard.Tests/](test/NodeGuard.Tests/).

**Migrations**
- `just add-migration <Name>` / `just remove-migration` — wrap `dotnet ef migrations` against `ApplicationDbContext`. Always use these so the right `--context` is passed.
- `just drop-db` — drop the DB (needed to remove an already-applied migration).
- Migrations apply automatically at startup via [src/Data/DbInitializer.cs](src/Data/DbInitializer.cs).

**Infra**
- `tilt up` is the recommended path — runs all profiles per [Tiltfile](Tiltfile).
- Alternatives: `just docker-up` / `just docker-down` / `just docker-rm` (force volume reset). The compose entrypoint is [docker-compose.yml](docker-compose.yml), which `include:`s the per-stack files under [docker/](docker/).
- `just mine` — loops `bitcoin-cli -generate 1` against the Polar `polar-n1-backend1` container for regtest.

**Protos**
- `just update-protos` — regenerates from upstream LND/Loop trees under [lnd/](lnd/) via [src/Proto/update-protos.sh](src/Proto/update-protos.sh). NodeGuard's own proto lives at [src/Proto/nodeguard.proto](src/Proto/nodeguard.proto).

## Reference code

Use [reference-code/](reference-code/) as **read-only** source material when inferring or designing NodeGuard features. Do not modify code there as part of NodeGuard changes.

- [reference-code/charge-lnd/](reference-code/charge-lnd/) (https://github.com/accumulator/charge-lnd)
- [reference-code/balanceofsatoshis/](reference-code/balanceofsatoshis/) (https://github.com/alexbosworth/balanceofsatoshis)
- [reference-code/lndg/](reference-code/lndg/) (https://github.com/cryptosharks131/lndg)
- [reference-code/lnd/](reference-code/lnd/) (https://github.com/lightningnetwork/lnd)
- [reference-code/bolts/](reference-code/bolts/) (https://github.com/lightning/bolts)
- [reference-code/rebalance-lnd/](reference-code/rebalance-lnd/) (https://github.com/C-Otto/rebalance-lnd)

## Architecture

**One process, two surfaces.** Single ASP.NET Core 10 host (`net10.0`) defined in [src/Program.cs](src/Program.cs):
- **Blazor Server UI** on HTTP/1 — pages in [src/Pages/](src/Pages/), Blazorise + Bootstrap 5.
- **gRPC API** on HTTP/2 (port 50051) — service in [src/Rpc/NodeGuardService.cs](src/Rpc/NodeGuardService.cs), proto in [src/Proto/nodeguard.proto](src/Proto/nodeguard.proto).

**Auth differs by surface.** Web UI uses ASP.NET Identity (cookie + 2FA, security stamp revalidation) with three roles defined in [src/Data/Models/ApplicationUser.cs](src/Data/Models/ApplicationUser.cs):
- `NodeManager` — operates Lightning nodes (channel open/close, node config).
- `FinanceManager` — handles on-chain funds and signing (wallets, withdrawals, signing keys).
- `Superadmin` — full admin (user/API token management, internal wallet setup); typically also holds the other two roles.

gRPC uses a stateless `auth-token` header validated by [src/Rpc/GRPCAuthInterceptor.cs](src/Rpc/GRPCAuthInterceptor.cs) against the `APIToken` table. Outbound gRPC to LND/Loop nodes uses macaroons via [src/Services/GRPCMacaroonInterceptor.cs](src/Services/GRPCMacaroonInterceptor.cs).

**Data layer** ([src/Data/](src/Data/)). PostgreSQL via Npgsql + EF Core. `ApplicationDbContext` extends `IdentityDbContext`. Repository pattern — generic `Repository<T>` plus per-entity repos in [src/Data/Repositories/](src/Data/Repositories/). Both `AddDbContext<ApplicationDbContext>` (transient) **and** `AddDbContextFactory<ApplicationDbContext>` are registered in `Program.cs` — **prefer the factory inside Quartz jobs and singletons**, the transient context is for short request-scoped work. Queries use `UseQuerySplittingBehavior(SingleQuery)` deliberately.

Domain entities to know: `Node`, `Wallet`, `Channel`, `ChannelOperationRequest` (open/close PSBT workflow with SourceNode/DestNode), `WalletWithdrawalRequest` + `WalletWithdrawalRequestPSBT`, `LiquidityRule`, `UTXOTag`, `ForwardingHtlcEvent`. `DbInitializer` waits for NBXplorer to sync, runs migrations on both `ApplicationDbContext` and `DataProtectionKeysContext`, and seeds Alice/Bob/Carol when `IS_DEV_ENVIRONMENT=true`.

**Service layer** ([src/Services/](src/Services/)). Each service owns one external integration or one domain capability:
- `BitcoinService` / `NBXplorerService` — on-chain UTXOs, addresses, PSBT building.
- `LightningService` — main LND RPC surface (channel ops, payments, subscriptions); the largest service.
- `LightningClientService` (singleton) — pooled gRPC channels to LND nodes.
- `LightningRouterService` (singleton) — route graph cache.
- `CoinSelectionService` — UTXO selection / fee logic for withdrawals.
- `RemoteSignerServiceService` — AWS Lambda signer for PSBTs (alternative to on-prem signing).
- `LoopService`, `FortySwapService`, `SwapsService` — submarine swap integrations (Loop = swap-out, 40swap = swap-in).
- `NotificationService` (OneSignal), `PriceConversionService` (CoinGecko), `AuditService`.

**Quartz jobs** ([src/Jobs/](src/Jobs/)). Persistent store backed by Postgres, so jobs survive restarts. Two flavors:
- *Scheduled cron/interval*: `SweepAllNodesWalletsJob` (~15 min), `AutoLiquidityManagementJob` (~10 min), `MonitorWithdrawalsJob`, `MonitorChannelsJob`, `MonitorSwapsJob` (1 min dev / 10 min prod), `AuditLogCleanupJob` (daily, 180-day retention).
- *Long-running subscriptions started at boot*: `NodeSubscriptorJob`, `ChannelAcceptorJob`, `HtlcSubscriptorJob`, `NodeChannelSubscribeJob`, `NodeHtlcSubscribeJob`.

Most jobs are `[DisallowConcurrentExecution]`. New jobs go in [src/Jobs/](src/Jobs/), wire types through [src/Helpers/JobTypes.cs](src/Helpers/JobTypes.cs), and register in `Program.cs`.

**External services NodeGuard talks to**: LND (gRPC + macaroons), NBXplorer, Loop daemon, 40swap daemon, AWS Lambda (remote signer), PostgreSQL (data + Quartz store), CoinGecko, optional Mempool, Amboss, OneSignal.

**Frontend caveat**. Blazor pages (e.g. `Wallets.razor`, `Channels.razor`) hold heavy `@code` blocks that inject services and repositories directly — there is no separate code-behind / view-model layer. Edit the `.razor` directly when changing UI logic.

## Tests

xUnit + FluentAssertions + NSubstitute (preferred) / Moq + `Moq.EntityFrameworkCore`. EF in tests uses `Microsoft.EntityFrameworkCore.InMemory`. Tests mirror source layout under [test/NodeGuard.Tests/Services/](test/NodeGuard.Tests/Services/), [test/NodeGuard.Tests/Helpers/](test/NodeGuard.Tests/Helpers/), etc.

## Conventions / gotchas

- **License header**: every `.cs` file in `src/` and `test/` carries the AGPLv3 header from [lic_header.txt](lic_header.txt). New files should too — [configuration-cs.json](configuration-cs.json) drives a `headache` (Go) check; the `add-license-cs` just recipe applies it. Files under `src/Areas/Identity/Pages/` are excluded.
- **`IS_DEV_ENVIRONMENT`** changes job intervals, seeds dev nodes, and is set by `extract-macaroons.sh`. Don't set it in prod paths.
- **DbContext lifetime**: prefer `IDbContextFactory<ApplicationDbContext>` from singletons and jobs.
- **Coding style**: Microsoft .NET conventions (per [CONTRIBUTING.md](CONTRIBUTING.md)); `dotnet format` is wired up as `just format`.
