# Spark swap-outs

Spark is a swap-out provider next to Loop and 40swap. A node moves Lightning liquidity to a NodeGuard on-chain wallet through a [Spark](https://www.spark.money/) wallet of NodeGuard's:
1. The node pays an invoice of the Spark wallet.
2. What that payment brought in exits on-chain to an address of the destination wallet, reserved when the swap exits, in a cooperative exit brokered by the Spark Service Provider (SSP).

NodeGuard is not a Spark wallet product. A Spark wallet is transit only: each swap's sats leave in that swap's own exit, and the guardrails below alert when sats stay behind.

The Spark client is the [NSpark](https://github.com/orklabs/nspark) .NET SDK, from the [Elenpay fork](https://github.com/Elenpay/nspark) in `vendor/nspark` (a git submodule). By default, on mainnet, the operators are Lightspark, Breez and Flashnet (2-of-3) and the SSP is Lightspark's.

## How a swap runs

**Creation.** Swaps are created by auto-liquidity, the New Swap dialog, or the `RequestSwapOut` RPC. A swap goes through the node's Spark wallet (see [Signing](#signing)).
1. A node may have up to its **Max swaps in flight** Spark swaps open at once (the node setting on the Nodes page; 0 counts as 1). An in-process lock serializes the check and the record, so concurrent requests can't go over it.
2. A swap that would bring the wallet over its max balance, counting what its swaps not paid yet will still bring in, is refused.
3. The Spark wallet issues an invoice through the SSP. An invoice without a receive request is refused before anything is paid: the swap's funds are found only through the transfer the SSP reports for it.
4. The swap is recorded before anything is paid: its payment hash, destination wallet and the Spark identity that will receive it. No address is reserved yet: the swap needs one only when it exits.
5. The node pays the invoice. The payment is not cancellable. A definitive failure fails the swap. An unknown outcome, such as a broken payment stream, is left to the monitor.

**Payment.** `MonitorSwapsJob` runs every 10 minutes, or every minute in a dev environment. It looks the payment up on the node by its hash. The swap fails if the payment failed, or if the node never saw it and the invoice has expired. Once the payment has settled, the swap is `SparkSwapExitJob`'s.

**Exit.** `SparkSwapExitJob` runs every `SPARK_EXIT_INTERVAL_MINUTES` (10 by default, 1 in a dev environment). Each paid swap exits on its own, from the Spark wallet it was paid into (found by its identity, whichever wallet the node uses now), to its own destination address, in saved steps: paid, then leaves attributed, then exit sent, then payout confirmed.
1. **Leaves attributed.** A Spark balance is made of leaves of fixed sizes, and the payment arrives as one inbound transfer whose leaves add up to what it brought in. The job claims it and records on the swap the transfer (the one the SSP reports for the swap's receive request), its leaves and their total. A transfer is given to one swap only, and a swap only ever takes its own: while the SSP reports none, the swap waits, and nothing is matched by amount. Only a claimed transfer is attributed: one whose claim failed is claimed again on the next run. A transfer that can never be claimed leaves the swap waiting until the overdue alert.
2. **Exit sent.** The job reserves the swap's address in the destination wallet and saves it on the swap, then exactly those leaves exit to it. No leaf is split or swapped with the SSP, so the remote signer can sign the exit too. The exit fee is capped by `SPARK_MAX_EXIT_FEE_SATS`, and the exit's txid is saved right away.
   - Leaves near their timelock floor are renewed first. A renewal keeps a leaf's id (the operators renew it in place), so the swap still exits its own leaves. If they can't move yet (a renewal failed, or they are frozen), the swap waits and tries again on the next run, and the overdue alert below flags it. No other leaves are ever taken in their place.
   - If the leaves already exited (a crash before the txid was saved), the job waits for the payout instead of exiting again.
3. **Payout confirmed.** The swap completes when the destination wallet sees a confirmed payout to the reserved address. Matching on the address, rather than the exit's txid, survives the SSP fee-bumping the exit. The payout is checked before exiting, so an exit whose txid was not saved never runs twice.

All attributions run before any exit, and exits go strictly one at a time, oldest swap first. This is separate from Max swaps in flight: that limit decides how many swaps a node may have open; the job only orders their exits. With Max swaps in flight at 3, three swaps are paid in parallel, and the next run exits the first, then the second, then the third.

**Fees and accounting.** What the swap delivered is what landed on-chain: its payout, shown as "Received on-chain" on the Swaps page. The fees add up to the amount minus the payout, plus the routing fee:
- **Routing fee:** what the node paid on Lightning. It includes Spark's fixed 15 bps (0.15%), which Spark charges to the sender on the Lightning route through the invoice's route hints ([Spark fees](https://docs.spark.money/wallets/estimate-fees)); receiving into Spark is free.
- **Service fee:** shown as Spark's 15 bps "in routing fee", not added to the total. Only if the payment brought in less than the amount is that shortfall recorded as a service fee.
- **On-chain fee:** the move from Spark to L1: the SSP's exit fee and the L1 broadcast, what the swap's leaves held minus the payout.

Each fee shows its BTC amount and its percentage of the swap, with ppm and USD in the tooltip.

## Trust model

Spark swaps are trust-minimized, not trustless the way Loop's are. A Loop swap is an on-chain HTLC: either the node's Lightning payment fails or the destination is paid on-chain, and only the Loop server's liveness is trusted. A Spark swap goes through three steps:

1. **Lightning into Spark is atomic.** The operators hold shares of the invoice's preimage and release it to the SSP only when the SSP transfers the Spark leaves to the Spark wallet's identity. A threshold of operators (two of the three on mainnet: Lightspark, Breez and Flashnet) could release it without that transfer.
2. **While the sats are on Spark**, from the payment until the exit confirms (minutes), they rely on the same operator threshold, which co-signs every leaf transfer. If the operators disappear, a unilateral exit with the leaves' pre-signed refund transactions still recovers the sats, but it is slow (timelocks) and costs on-chain fees. NSpark can snapshot what that exit needs (`RecoveryService.GetRecoverySnapshotAsync`); NodeGuard doesn't automate it.
3. **The exit to on-chain is atomic.** Before signing anything, NSpark checks that the SSP's exit transaction pays the reserved address at least the amount minus the fee cap, and that the connector transaction spends it. The operators release the leaves to the SSP only once that exit transaction confirms.

So the exposure is the sats in transit, while they are on Spark. That is why a Spark wallet is transit only: its max balance caps it, and the guardrails alert when sats get stuck or an exit is overdue.

## Signing

`SPARK_SIGNER` sets where the Spark keys live:

- **`wallet`** (the default; temporary). Spark wallets are created on the Wallets page (or with `CreateSparkWallet`), and each node is given the Spark wallet its swaps pass through. Their keys are hot: see [Spark wallets](#spark-wallets-temporary).
- **`remote`**. One Spark wallet, whose keys stay in the remote signer Lambda under its `/spark/` routes, serves every node; see the [remote signer README](https://github.com/Elenpay/Nodeguard-Remote-Signer#spark-signing). Its keys come from the seed with master fingerprint `SPARK_SEED_FINGERPRINT`, at Spark account `SPARK_ACCOUNT` (`m/8797555'/account'/…`). NodeGuard calls `/spark/info` at startup. It uses the signer only if the signer reports the same contract version, the pinned identity (`SPARK_IDENTITY_PUBKEY`), and a policy for exactly NodeGuard's operators, threshold and SSP.

If a Spark wallet's signer is refused or unreachable, or its seed no longer decrypts, that wallet is unavailable and the reason is logged. Its nodes then don't get Spark in the New Swap dialog, auto-liquidity skips Spark for them and the RPC refuses their Spark swaps. NodeGuard retries at most once a minute per wallet.

## Spark wallets (temporary)

Spark wallets let NodeGuard use Spark without the remote signer, for instance for a short mainnet test. They will be removed once the remote signer is the only Spark signer.

- **Creating one.** Wallets page, New ▸ New Spark wallet (finance managers). Give it a name, a Spark account (0 by default) and optionally a max balance. NodeGuard generates a 24-word mnemonic and shows it **once**: write it down. A wallet created over gRPC never shows it.
- **Storage.** The mnemonic is encrypted with ASP.NET Data Protection (purpose `NodeGuard.SparkWallet.Mnemonic.v1`). The key ring is NodeGuard's own, in Postgres (`DataProtectionKeys`), so it is shared by every replica and survives restarts and redeploys. This protects against a dump of the `Wallets` table alone, not of the whole database: the key ring itself is stored unencrypted. If the key ring is lost or NodeGuard's application name changes, the mnemonics can't be decrypted, and the words shown at creation are the only way back. A wallet whose seed doesn't decrypt is reported unavailable.
- **Using one.** Nodes page ▸ liquidity settings ▸ Spark wallet, next to the Spark weight. A Spark weight above 0 needs a Spark wallet. Several nodes may share one. A node's Spark wallet can't change while the node has a Spark swap in flight. Until a node has one, the New Swap dialog lists no node for Spark and says so.
- **Max balance.** Each Spark wallet has its own max balance (Edit max balance), the most it may hold counting a new swap and its swaps not paid yet. Empty means `SPARK_MAX_BALANCE_SATS`. For a mainnet test, keep it small (1-2 M sats).
- **Withdraw all** exits everything the wallet holds to a new address of an on-chain wallet, the fee capped by `SPARK_MAX_EXIT_FEE_SATS`. It is refused while the wallet has a swap in flight, so a swap's sats never leave with the rest.
- **Archive** is allowed only for an empty wallet that no node uses.
- On mainnet, NodeGuard logs a warning at startup while Spark wallets exist.

Spark wallets are rows of the Wallets grid (marked ⚡) with no on-chain keys. They never appear where an on-chain wallet is chosen, and the gRPC methods that take an on-chain wallet refuse them with `FAILED_PRECONDITION`. Every action is audited.

## Configuration

While Spark is enabled, invalid settings stop NodeGuard at startup.

| Variable | Default | Meaning |
|---|---|---|
| `SPARK_ENABLED` | `true` on mainnet, `false` elsewhere | Enables the Spark provider |
| `SPARK_SIGNER` | `wallet` | `wallet` (Spark wallets in NodeGuard) or `remote` (the remote signer) |
| `SPARK_SEED_FINGERPRINT` | required with the remote signer | Master fingerprint of the seed holding the remote signer's Spark keys |
| `SPARK_ACCOUNT` | `0` | The remote signer's Spark account index; the same as the signer's |
| `SPARK_IDENTITY_PUBKEY` | required with the remote signer | The Spark identity the signer must serve (seed ceremony: `verify --spark-account`) |
| `SPARK_OPERATORS` | Lightspark's on mainnet; required on regtest | `address\|64-hex identifier\|identity key`, separated by `;` |
| `SPARK_THRESHOLD` | derived from the operator count | Operators' signing threshold |
| `SPARK_SSP_URL` | Lightspark's on mainnet; required on regtest | The SSP's GraphQL endpoint |
| `SPARK_SSP_IDENTITY_PUBKEY` | Lightspark's on mainnet; fetched from the SSP on regtest | Required with a custom SSP on mainnet |
| `SPARK_MAX_EXIT_FEE_SATS` | `20000` | Highest SSP exit fee accepted per swap |
| `SPARK_MAX_BALANCE_SATS` | `10000000` | Most sats a Spark wallet may hold, counting a new swap and its swaps not paid yet, unless it has its own max balance |
| `SPARK_EXIT_INTERVAL_MINUTES` | `10`, `1` in a dev environment | How often paid swaps exit on-chain |
| `SPARK_OPERATOR_CERTS_DIR` | none; refused on mainnet | Self-signed operator certificates to pin (regtest) |
| `SPARK_SIGNER_RIE_URL` | none; refused on mainnet | Reach a locally run signer image through the Lambda emulator |

On mainnet with the defaults, Spark needs no setting at all: Spark wallets, Lightspark's operators and SSP. A node without a Spark wallet simply doesn't use Spark, and NodeGuard doesn't contact the operators until a Spark wallet exists.

With the remote signer, `REMOTE_SIGNER_ENDPOINT` must be the Function URL with no path. The PSBT and Spark routes share it.

## Using it

- **Auto-liquidity.** Give the node a Spark weight and a Spark wallet on the Nodes page; the Loop, 40swap and Spark weights sum to 100. The node's Max swaps in flight limits Spark swaps like any other, and Spark is skipped while the node's Spark wallet is unavailable. A Spark swap is sized down to the room left under the wallet's max balance (counting its swaps not paid yet), and Spark is skipped when that room is below the node's minimum swap. If no other weighted provider is left, the run is skipped without reserving an address.
- **New Swap dialog.** Spark lists the nodes whose Spark wallet is ready. There is no up-front quote: the SSP quotes the exit for the leaves the payment brings in. The confirmation shows the caps instead, with their percentage of the swap.
- **gRPC.**
  - `RequestSwapOut` (`provider: SWAP_PROVIDER_SPARK`) returns no destination address: the swap reserves it when it exits, and `GetSwapOut` reports it from then on.
  - An optional `reference_id` makes the call repeatable: a known `reference_id` returns its swap instead of paying again.
  - `GetSwapOut` takes a swap id or a `reference_id`. Once a Spark swap completes, its `payout_sats` is what landed on-chain.
  - `CreateSparkWallet`, `GetSparkWallets`, `GetSparkWalletBalance`, `WithdrawAllSparkWallet` and `ArchiveSparkWallet` manage Spark wallets. A node's Spark wallet and a wallet's max balance are set on the web pages only.

## Guardrails and alerts

The swap monitor checks each Spark wallet after each pass. Each alert below is audited once and logged with structured fields:
- `SparkBalanceStuck`: for an hour, a Spark wallet has held more than its swaps in flight account for. Until a swap exits, what it brought in (or its amount, until that is known) is its own and never counts as stuck.
- `SparkExitOverdue`: a swap was paid six hours ago and still has no confirmed payout.

Each wallet's balance is shown in its details on the Wallets page and exported as the `nodeguard.spark.balance` gauge (meter `NodeGuard.Spark`), tagged `spark.wallet` with the wallet's id (`remote-signer` for the remote signer's).

**Resolving by hand.**
- **Stuck sats** (frozen, unrenewed or unclaimed leaves, or leaves no swap was given) count against the wallet's max balance until they are gone. With Spark wallets, Withdraw all exits what can be exited once the wallet has no swap in flight; otherwise inspect and withdraw them with a Spark wallet of the same seed and account.
- **A swap paid into a Spark identity no Spark wallet has any more** is refused by the exit job, which logs the error on every pass. Exit its sats from a Spark wallet with that seed and account, then set the swap's status in the database.

## Rotating or retiring the Spark seed

Before rotating the seed that holds the remote signer's Spark keys, or marking it `Compromised` in the remote signer:
1. Wait until no Spark swap is in flight.
2. Wait until the Spark balance (the `nodeguard.spark.balance` gauge) is zero, withdrawing any stuck sats by hand.
3. Only then rotate, and set the new `SPARK_SEED_FINGERPRINT` and `SPARK_IDENTITY_PUBKEY`.

The remote signer refuses to sign Spark operations with a `Compromised` seed. A Spark wallet is retired by withdrawing everything and archiving it.

## Local development and E2E

`just spark-up` starts a regtest Spark network (three operators, the open-source open-ssp SSP and its Lightning node) on the Polar chain; see [docker/spark/README.md](../docker/spark/README.md).

`just test-e2e` and the CI `e2e-test` job run the whole E2E suite with the network up, including `SparkSwapOutE2ETests`. It creates a Spark wallet over gRPC, gives it to alice in the database with Max swaps in flight 2, restarts NodeGuard (the seed must decrypt after a restart), and starts two swaps at once. Each must exit its own leaves to its own address and record its payout, with its on-chain and service fees adding up to the amount minus the payout, and the Spark wallet must end empty. A second test holds a swap's exit (NBXplorer stopped, so no exit address can be reserved), ages its leaves to their timelock floor with the local operators' test RPC (`mock.MockService/modify_node_timelock`), then lets the exit run: it must renew the leaves (one through a node renewal, the rest through refund renewals) and move exactly them, under their own ids, with the amounts adding up.

## Before enabling it on mainnet

Run small canary swaps and confirm:
- Lightspark's SSP accepts exits to NodeGuard's (P2WSH multisig) addresses and serves receive-request lookups, reporting the transfer that paid each one (open-ssp does).
- With the remote signer, the `SPARK_ACCOUNT` matches what the identity was derived with.
- The real fees and limits fit `SPARK_MAX_EXIT_FEE_SATS` and the wallets' max balances.
- Renewed leaves exit. A rejection of exits from leaves renewed down to a zero node timelock was seen on a local operator build; it would leave sats stuck and needs an NSpark fix upstream before mainnet.
