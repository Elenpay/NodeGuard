# Containerized e2e suite

A true end-to-end test against a **live NodeGuard**: a .NET runner drives it over gRPC — opens the source
channel via NodeGuard's `OpenChannel` API, mines via NBitcoin's `RPCClient`, runs a circular rebalance
Alice→Bob→Carol→Alice, and exercises the dynamic fee engine (smoke + a live SINK→SOURCE flow) — all in one
`dotnet test` pass.

`just test-e2e` brings the stack up once and runs the entire `Category=E2E` suite in one `dotnet test` pass.
Its rebalance/fee-engine core is three scenarios — **(1)** rebalance (`RebalanceE2ETests`), **(2)** fee-engine
smoke (`FeeEngineE2ETests`), **(3)** fee-engine flow (`FeeEngineFlowE2ETests`; SINK→SOURCE, driving its own
LND traffic in-process via `LndTestClient`) — and the same pass also runs the wallet/UTXO e2e tests
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

Tests live in `test/NodeGuard.Tests/E2E/`. The rebalance/fee-engine scenarios are `RebalanceE2ETests`,
`FeeEngineE2ETests`, and `FeeEngineFlowE2ETests` (on `E2ETestBase` / `FeeEngineE2EBase`), with `LndTestClient`
(direct LND gRPC driver for the flow scenario); the wallet/UTXO tests (`DustUtxoWithdrawalE2ETests`,
`GetNewWalletAddressE2ETests`) sit alongside them. All are `[Collection("E2E")]` and gated by `[E2EFact]`
(they run when NodeGuard gRPC is reachable, or `RUN_E2E_TESTS=1`).

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
- **Adding an e2e test**: put it in `[Collection("E2E")]` (so it serialises with the others on the one
  regtest chain — without it the class runs in parallel and they interfere), have it provision the
  resources it needs and reset its own state, and gate it with `[E2EFact]`.
