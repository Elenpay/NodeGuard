# Containerized rebalance e2e (option B)

A true end-to-end test of Lightning rebalancing: a .NET test runner drives a **live NodeGuard**
entirely over gRPC — it **opens the source channel via NodeGuard's `OpenChannel` API** (so the e2e
also covers channel opening), mines via NBitcoin's `RPCClient`, then performs a circular rebalance
Alice→Bob→Carol→Alice and asserts success.

## Run it

```bash
just test-e2e          # or: COMPOSE_PROFILES=polar,e2e docker compose run --rm --build e2e-runner
```

Everything is profile-gated (`e2e`), so a normal `tilt up` / `docker compose up` ignores it. In the
Tilt UI the e2e services appear under the `e2e` label, disabled (manual trigger only).

## Pieces

| File | Role |
|------|------|
| `setup-e2e.sh` | Loads the bitcoind wallet, funds the LND nodes, opens **only** Bob→Carol + Carol→Alice (NodeGuard opens Alice→Bob). docker.sock pattern, like the polar `setup` service. |
| `extract-env.sh` | Writes `nodeguard-macaroons.env` (LND host/macaroon/pubkey) from the mounted LND data volumes + a network `lncli getinfo`. The LND certs include the service-name SANs (`alice`/`bob`/`carol`), so TLS verifies. |
| `nodeguard-entrypoint.sh` | Sources that env file, then launches NodeGuard. |
| `Dockerfile.runner` | .NET SDK image that runs `dotnet test --filter Category=E2E`. The test does the gRPC + mining itself — no grpcurl/curl. |
| `docker-compose.yml` | Wires `setup-e2e` + `extract-env` → `nodeguard` → `e2e-runner` with the right `depends_on` ordering. |

The test itself is `test/NodeGuard.Tests/E2E/RebalanceE2ETests.cs`, gated by `[E2EFact]` (runs when a
NodeGuard gRPC is reachable on `NODEGUARD_GRPC_ENDPOINT`, or `RUN_E2E_TESTS=1`).

## Status / shakeout notes

Every **runtime step** was validated manually against a live NodeGuard (open channel via gRPC →
`ONCHAIN_CONFIRMED` → rebalance `Succeeded`, 500 sat fee). The **compose wiring** has not yet been run
as a full stack in CI; watch these on the first CI run:

- **App config env (fixed):** the published image (`dotnet NodeGuard.dll`) does NOT read
  `launchSettings.json`, so `Constants`' required vars (`MINIMUM_CHANNEL_CAPACITY_SATS`,
  `TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS`, `ANCHOR_CLOSINGS_MINIMUM_SATS`, `DEFAULT_DERIVATION_PATH`,
  `NBXPLORER_URI`, …) are set explicitly on the `nodeguard` service. Keep them in sync with the dev
  launch profile.
- **Clean slate required (fixed):** `DbInitializer` only funds the dev wallets when the DB has none
  (`!Wallets.Any()`). A stale postgres volume from an interrupted run leaves the wallet rows present
  but unfunded against the fresh chain → `OpenChannel` fails with "Error generating template PSBT"
  (no UTXOs). `just test-e2e` and the CI job now `down -v` before running.
- **Critical ordering:** `nodeguard` must start only after `setup-e2e` completes — NodeGuard funds its
  hot wallet via bitcoind RPC, which fails ("No wallet is loaded") if the bitcoind wallet isn't loaded
  yet. The `depends_on: service_completed_successfully` enforces this.
- **Shared volumes** (`alice_lnd_data`/`bob_lnd_data`/`carol_lnd_data`) are declared in the polar
  compose; they're referenced here by name within the same merged project.
- **`nonroot` entrypoint:** the NodeGuard image runs as uid 65532; the entrypoint wrapper + `/shared`
  mount must be readable by it.
- **docker.sock** must be available to `setup-e2e` (it is on GitHub-hosted runners).
- **Hot wallet id** is assumed to be `3` (the dev single-sig wallet); override via `E2E_HOT_WALLET_ID`.
