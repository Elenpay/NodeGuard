# Containerized e2e suite

A true end-to-end test against a **live NodeGuard**: a .NET runner drives it over gRPC — opens the source
channel via NodeGuard's `OpenChannel` API, mines via NBitcoin's `RPCClient`, runs a circular rebalance
Alice→Bob→Carol→Alice, and exercises the dynamic fee engine (smoke + a live SINK→SOURCE flow) — all in one
ordered `dotnet test` pass.

`just test-e2e` brings the stack up once and runs `E2ESuiteTests` end-to-end, its three scenarios pinned in
order by `PriorityOrderer`: **(1)** rebalance → **(2)** fee-engine smoke (fee applied, then stopped on
disable) → **(3)** fee-engine flow (SINK→SOURCE, driving its own LND traffic in-process via `LndTestClient`).
Ordering matters: (3) reuses (1)'s Alice→Bob, and its traffic would starve (1)'s route if it ran first.

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

Tests live in `test/NodeGuard.Tests/E2E/`: `E2ESuiteTests` (three ordered scenarios, split across
`.Rebalance.cs` / `.FeeEngineSmoke.cs` / `.FeeEngineFlow.cs`), plus `E2ETestBase` (shared plumbing),
`LndTestClient` (direct LND gRPC driver for the flow scenario), and `PriorityOrderer` (scenario order).
Gated by `[E2EFact]` (runs when NodeGuard gRPC is reachable, or `RUN_E2E_TESTS=1`).

## Notes

- **Clean slate**: `DbInitializer` only funds the dev wallets on an empty DB, so `just test-e2e` and CI
  `down -v` first — a stale postgres volume leaves the wallet unfunded ("no UTXOs" on `OpenChannel`).
- **Ordering**: `nodeguard` starts only after `setup-e2e` completes (it funds its hot wallet via bitcoind
  RPC, which needs the wallet loaded); `depends_on: service_completed_successfully` enforces it.
- **App config env**: the published image doesn't read `launchSettings.json`, so `Constants`' required vars
  are set explicitly on the `nodeguard` service — keep them in sync with the dev launch profile.
- **Flow scenario LND access**: scenario (3) drives alice/bob/carol's LND directly, so it needs
  `{NODE}_HOST` + `{NODE}_MACAROON` (from `extract-env`). `LndTestClient` reads them from the process env
  or, failing that, straight from `nodeguard-macaroons.env` on the mounted `e2e_env` volume — so it works
  without the runner entrypoint exporting them.
