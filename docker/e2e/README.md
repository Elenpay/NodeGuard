# Containerized e2e suite

A true end-to-end test against a **live NodeGuard**: a .NET runner drives it over gRPC — opens the source
channel via NodeGuard's `OpenChannel` API, mines via NBitcoin's `RPCClient`, runs a circular rebalance
Alice→Bob→Carol→Alice, and exercises the routing engine end to end (dynamic fees: smoke + a live SINK→SOURCE
flow; automatic rebalancing: a shaped imbalance the engine detects and corrects on its own) — all in one
`dotnet test` pass.

`just test-e2e` brings the stack up once and runs the entire `Category=E2E` suite in one `dotnet test` pass.
Its rebalance/routing-engine core is four scenarios — **(1)** manual rebalance (`RebalanceE2ETests`),
**(2)** fee-engine smoke (`FeeEngineE2ETests`), **(3)** fee-engine flow (`FeeEngineFlowE2ETests`;
SINK→SOURCE, driving its own LND traffic in-process via `LndTestClient`), **(4)** automatic rebalancing
(`AutoRebalanceE2ETests`; shapes an imbalance, switches the node's rebalancer on and asserts on what
`AutoRebalanceJob` decided) — and the same pass also runs the wallet/UTXO e2e tests
(`DustUtxoWithdrawalE2ETests`, `GetNewWalletAddressE2ETests`). Every e2e class is **order-agnostic** (it
provisions its own channels and resets its own state) and they run **serially** via the shared
`[Collection("E2E")]` — one regtest chain can't take concurrent channel opens/traffic.

## Run it

```bash
just test-e2e          # or: COMPOSE_PROFILES=polar,e2e docker compose run --rm --build e2e-runner
```

Everything is profile-gated (`e2e`), so a normal `tilt up` / `docker compose up` ignores it.

## Pieces

| File | Role |
|------|------|
| `setup-e2e.sh` | Loads the bitcoind wallet, funds the LND nodes, opens **only** Bob→Carol + Carol→Alice (NodeGuard opens Alice→Bob). docker.sock, like the polar `setup` service. |
| `extract-env.sh` | Writes `nodeguard-macaroons.env` (LND host/macaroon/pubkey) from the LND data volumes; the certs carry service-name SANs so TLS verifies. |
| `nodeguard-entrypoint.sh` | Sources that env file, then launches NodeGuard. |
| `Dockerfile.runner` | .NET SDK image that runs `dotnet test --filter Category=E2E`. The tests do the gRPC + mining + Postgres reads themselves — no grpcurl/curl. |
| `docker-compose.yml` | Wires `setup-e2e` + `extract-env` → `nodeguard` → `e2e-runner`. |

Tests live in `test/NodeGuard.Tests/E2E/`. The rebalance/routing-engine scenarios are `RebalanceE2ETests`,
`FeeEngineE2ETests`, `FeeEngineFlowE2ETests` and `AutoRebalanceE2ETests`, layered on `E2ETestBase` →
`RoutingEngineE2EBase` (Postgres + routing-state plumbing) → `FeeEngineE2EBase` (fee-state helpers), with
`LndTestClient` (direct LND gRPC driver used by the flow and auto-rebalance scenarios); the wallet/UTXO
tests (`DustUtxoWithdrawalE2ETests`, `GetNewWalletAddressE2ETests`) sit alongside them. All are
`[Collection("E2E")]` and gated by `[E2EFact]` (they run when NodeGuard gRPC is reachable, or
`RUN_E2E_TESTS=1`).

## Notes

- **Clean slate**: `DbInitializer` only funds the dev wallets on an empty DB, so `just test-e2e` and CI
  `down -v` first — a stale postgres volume leaves the wallet unfunded ("no UTXOs" on `OpenChannel`).
- **Startup order**: `nodeguard` starts only after `setup-e2e` completes (it funds its hot wallet via
  bitcoind RPC, which needs the wallet loaded); `depends_on: service_completed_successfully` enforces it.
- **App config env**: the published image doesn't read `launchSettings.json`, so `Constants`' required vars
  are set explicitly on the `nodeguard` service — keep them in sync with the dev launch profile.
- **Flow scenario LND access**: scenario (3) drives alice/bob/carol's LND directly, so it needs
  `{NODE}_HOST` + `{NODE}_MACAROON` (from `extract-env`). `LndTestClient` reads them from the process env
  or, failing that, straight from `nodeguard-macaroons.env` on the mounted `e2e_env` volume — so it works
  without the runner entrypoint exporting them.
- **NBXplorer must serve `POST selectutxos`**: NodeGuard sends the coin-selection exclusion list in a
  request body, so a GET-only NBXplorer answers 405 to every `selectutxos` call and coin selection degrades
  for every wallet, not just the ones this test covers. It probes `NBXPLORER_URI` first
  (`Allow: GET` = pre-fix, `Allow: GET, POST` = patched) and fails with that diagnostic rather than looking
  like a coin-selection bug — without the probe the test is red identically pre-fix and post-fix, because
  `GetAvailableUtxos` swallows the 405 into an empty selection. The image is pinned in
  `docker/docker-compose.dev.yml`, which must point at a build carrying the route.
- **Auto-rebalance scenario**: it asks NodeGuard for nothing — it shapes alice's liquidity (one opted-in
  Alice→Bob channel too local, her carol channels drained too remote), wipes the derived routing state so the
  signal re-seeds on those balances, then flips `Node.AutoRebalanceEnabled` and lets `AutoRebalanceJob` plan
  and dispatch on its own cadence (1 min in dev). It pins alice's outbound ppm on the carol channels because
  that is the earn rate the profitability gate prices the plan against, and it reads
  `ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS` / `ROUTING_ENGINE_REBALANCE_DEADBAND` from its own env — **keep
  those two in sync between the `nodeguard` and `e2e-runner` services**, or the test asserts against numbers
  NodeGuard didn't plan with.
- **Adding an e2e test**: put it in `[Collection("E2E")]` (so it serialises with the others on the one
  regtest chain — without it the class runs in parallel and they interfere), have it provision the
  resources it needs and reset its own state, and gate it with `[E2EFact]`.
