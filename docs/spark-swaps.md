# Spark swap-outs

Spark is a swap-out provider next to Loop and 40swap. A node moves Lightning liquidity to a NodeGuard on-chain wallet through NodeGuard's own [Spark](https://www.spark.money/) wallet:
1. The node pays an invoice of the Spark wallet.
2. The Spark wallet exits on-chain to an address reserved in the destination wallet, in one cooperative exit brokered by the Spark Service Provider (SSP).

NodeGuard is not a Spark wallet product. The Spark wallet is transit only: each swap's exit drains it, and the guardrails below alert when sats stay behind.

The Spark client is the [NSpark](https://github.com/orklabs/nspark) .NET SDK (orklabs), in `vendor/nspark` (a git submodule). By default, on mainnet, the operators are Lightspark, Breez and Flashnet (2-of-3) and the SSP is Lightspark's.

## How a swap runs

**Creation.** Swaps are created by auto-liquidity, the New Swap dialog, or the `RequestSwapOut` RPC.
1. Only one Spark swap can be in flight. An in-process lock and a partial unique index on `SwapOuts(Provider) WHERE Provider = 2 AND Status = Pending` enforce it.
2. A swap that would bring the wallet over `SPARK_MAX_BALANCE_SATS` is refused.
3. The Spark wallet issues an invoice through the SSP.
4. The swap is recorded before anything is paid: its payment hash, destination address and the Spark identity that will receive it.
5. The node pays the invoice. The payment is not cancellable. A definitive failure fails the swap. An unknown outcome, such as a broken payment stream, is left to the monitor.

**Monitoring.** `MonitorSwapsJob` runs every 10 minutes, or every minute in a dev environment.
1. **Payment.** It looks the payment up on the node by its hash. The swap fails if the payment failed, or if the node never saw it and the invoice has expired.
2. **Exit.** It claims the transfer and exits the whole wallet to the destination address. The exit fee is capped by `SPARK_MAX_EXIT_FEE_SATS`. The exit's txid and fee are saved right away.
3. **Completion.** The swap completes when the destination wallet sees a confirmed payout to the reserved address. Matching on the address, rather than the exit's txid, survives the SSP fee-bumping the exit. The payout is checked before exiting, so an exit whose txid was not saved before a crash never runs twice.

**Fees.**
- The Lightning routing fee is the swap's off-chain fee.
- The SSP's fee is the amount minus the payout, and includes the exit's miner fee. It is the swap's service fee.
- The on-chain fee is 0.

## Trust model

Spark swaps are trust-minimized, not trustless the way Loop's are. A Loop swap is an on-chain HTLC: either the node's Lightning payment fails or the destination is paid on-chain, and only the Loop server's liveness is trusted. A Spark swap goes through three steps:

1. **Lightning into Spark is atomic.** The operators hold shares of the invoice's preimage and release it to the SSP only when the SSP transfers the Spark leaves to NodeGuard's identity. A threshold of operators (two of the three on mainnet: Lightspark, Breez and Flashnet) could release it without that transfer.
2. **While the sats are on Spark**, from the payment until the exit confirms (minutes), they rely on the same operator threshold, which co-signs every leaf transfer. If the operators disappear, a unilateral exit with the leaves' pre-signed refund transactions still recovers the sats, but it is slow (timelocks) and costs on-chain fees. NSpark can snapshot what that exit needs (`RecoveryService.GetRecoverySnapshotAsync`); NodeGuard doesn't automate it.
3. **The exit to on-chain is atomic.** Before signing anything, NSpark checks that the SSP's exit transaction pays the reserved address at least the amount minus the fee cap, and that the connector transaction spends it. The operators release the leaves to the SSP only once that exit transaction confirms.

So the exposure is the sats in transit, while they are on Spark. That is why the wallet is transit only: `SPARK_MAX_BALANCE_SATS` caps it, and the guardrails alert when sats get stuck or an exit is overdue.

## Signing

The Spark keys come from one existing seed: the seed with master fingerprint `SPARK_SEED_FINGERPRINT`, at Spark account `SPARK_ACCOUNT` (`m/8797555'/account'/…`). There are two modes, the same two as for PSBT signing:

- **Remote** (`ENABLE_REMOTE_SIGNER=true`). The keys stay in the remote signer Lambda, which serves them under its `/spark/` routes; see the [remote signer README](https://github.com/Elenpay/Nodeguard-Remote-Signer#spark-signing). NodeGuard calls `/spark/info` at startup. It uses the signer only if the signer reports the same contract version, the pinned identity (`SPARK_IDENTITY_PUBKEY`), and a policy for exactly NodeGuard's operators, threshold and SSP.
- **Embedded.** The keys are derived in NodeGuard from the internal wallet with that fingerprint. In a dev environment the default is the current internal wallet.

If the signer is refused or unreachable, Spark is unavailable and the reason is logged. Spark is then hidden in the New Swap dialog, skipped by auto-liquidity and refused by the RPC. NodeGuard retries at most once a minute.

## Configuration

With `SPARK_ENABLED=true`, invalid settings stop NodeGuard at startup.

| Variable | Default | Meaning |
|---|---|---|
| `SPARK_ENABLED` | `false` | Enables the Spark provider |
| `SPARK_SEED_FINGERPRINT` | required (except embedded in dev) | Master fingerprint of the seed holding the Spark keys |
| `SPARK_ACCOUNT` | `0` | Spark account index; the same as the signer's |
| `SPARK_IDENTITY_PUBKEY` | required with the remote signer | The Spark identity the signer must serve (seed ceremony: `verify --spark-account`) |
| `SPARK_OPERATORS` | Lightspark's on mainnet; required on regtest | `address\|64-hex identifier\|identity key`, separated by `;` |
| `SPARK_THRESHOLD` | derived from the operator count | Operators' signing threshold |
| `SPARK_SSP_URL` | Lightspark's on mainnet; required on regtest | The SSP's GraphQL endpoint |
| `SPARK_SSP_IDENTITY_PUBKEY` | Lightspark's on mainnet; fetched from the SSP on regtest | Required with a custom SSP on mainnet |
| `SPARK_MAX_EXIT_FEE_SATS` | `20000` | Highest SSP exit fee accepted per swap |
| `SPARK_MAX_BALANCE_SATS` | `10000000` | Most sats the transit wallet may hold, counting a new swap |
| `SPARK_OPERATOR_CERTS_DIR` | none; refused on mainnet | Self-signed operator certificates to pin (regtest) |
| `SPARK_SIGNER_RIE_URL` | none; refused on mainnet | Reach a locally run signer image through the Lambda emulator |

With the remote signer, `REMOTE_SIGNER_ENDPOINT` must be the Function URL with no path. The PSBT and Spark routes share it.

## Using it

- **Auto-liquidity.** Give the node a Spark weight on the Nodes page; the Loop, 40swap and Spark weights sum to 100. Spark is skipped while it is unavailable or another Spark swap is in flight. A Spark swap is sized down to the room left under `SPARK_MAX_BALANCE_SATS`, and Spark is skipped when that room is below the node's minimum swap. If no other weighted provider is left, the run is skipped without reserving an address.
- **New Swap dialog.** Spark is listed while the wallet is ready. There is no up-front quote: the SSP quotes the exit for the leaves the payment brings in. The confirmation shows the caps instead.
- **gRPC.**
  - `RequestSwapOut` (`provider: SWAP_PROVIDER_SPARK`) returns the destination address.
  - An optional `reference_id` makes the call repeatable: a known `reference_id` returns its swap instead of paying again.
  - `GetSwapOut` takes a swap id or a `reference_id`.

## Guardrails and alerts

The swap monitor checks the transit wallet after each pass. Each alert below is audited once and logged with structured fields:
- `SparkBalanceStuck`: sats have been stuck in the wallet for an hour with no Spark swap in flight.
- `SparkExitOverdue`: a swap was paid six hours ago and still has no confirmed payout.

The balance is shown on the Swaps page and exported as the `nodeguard.spark.balance` gauge (meter `NodeGuard.Spark`).

**Resolving by hand.**
- **Stuck sats** (frozen, unrenewed or unclaimed leaves) need a Spark wallet with the same seed and account to inspect them and withdraw them. Until they are gone they count against `SPARK_MAX_BALANCE_SATS`.
- **A swap paid into another Spark identity** (the seed or account changed while it was in flight) is refused by the monitor, which logs the error on every pass. Exit its sats from a Spark wallet with the old seed and account, then set the swap's status in the database.

## Rotating or retiring the Spark seed

Before rotating the internal wallet seed that holds the Spark keys, or marking it `Compromised` in the remote signer:
1. Wait until no Spark swap is in flight.
2. Wait until the Swaps page shows a zero Spark balance, withdrawing any stuck sats by hand.
3. Only then rotate, and set the new `SPARK_SEED_FINGERPRINT` and `SPARK_IDENTITY_PUBKEY`.

The remote signer refuses to sign Spark operations with a `Compromised` seed.

## Local development and E2E

`just spark-up` starts a regtest Spark network (three operators, the open-source open-ssp SSP and its Lightning node) on the Polar chain; see [docker/spark/README.md](../docker/spark/README.md).

`just test-e2e` and the CI `e2e-test` job run the whole E2E suite with the network up, including `SparkSwapOutE2ETests`, which follows a swap from alice to a confirmed payout.

## Before enabling it on mainnet

Run small canary swaps and confirm:
- Lightspark's SSP accepts exits to NodeGuard's (P2WSH multisig) addresses and serves receive-request lookups.
- The `SPARK_ACCOUNT` matches what the identity was derived with.
- The real fees and limits fit `SPARK_MAX_EXIT_FEE_SATS` and `SPARK_MAX_BALANCE_SATS`.
- Renewed leaves exit. A rejection of exits from leaves renewed down to a zero node timelock was seen on a local operator build; it would leave sats stuck and needs an NSpark fix upstream before mainnet.
