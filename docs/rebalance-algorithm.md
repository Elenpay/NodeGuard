# Rebalance Algorithm

Guide to the routing engine's **automated rebalance** decision logic:
[`RebalanceInitiatorService`](../src/Services/RebalanceInitiatorService.cs), driven by
[`AutoRebalanceJob`](../src/Jobs/AutoRebalanceJob.cs) and executed by
[`RebalanceService`](../src/Services/RebalanceService.cs).

Read [routing-engine.md](routing-engine.md) first for the signal layer (`EmaLocalRatio`,
`TargetLocalRatio`) that this algorithm consumes. Companion:
[fee-engine-algorithm.md](fee-engine-algorithm.md).

All constants below are the current defaults from [`Constants.cs`](../src/Helpers/Constants.cs) and
are env-overridable (`ROUTING_ENGINE_REBALANCE_*`, `REBALANCE_*`).

---

## 1. What it does

A **circular rebalance** is a payment from the node to itself: out through one of our channels, back
in through another. It moves local balance from a channel that has too much to a peer that has too
little, and it costs routing fees.

Where the fee engine *prices* an imbalance and waits for the market to correct it, the rebalancer
*pays* to correct it directly. The two run on independent cadences and coordinate only through the
database: dispatching persists a `Rebalance` row as `Pending`, which is what makes the fee job leave
the drained channel alone.

`RebalanceInitiatorService` is **pure** — no I/O, no clock, no DB — so it is a static function
library rather than a DI-registered service (mirroring `FeeOptimizerService`). It has two entry
points: `Classify` sorts our channels into who can give and who can take; `BuildPlans` pairs them
up. The job around it handles snapshots, pricing, budget, caps, dry-run, and dispatch.

## 2. The asymmetry that shapes everything

LND's `SendPaymentV2` constrains the two ends of the circle differently:

| Field | Constrains | Precision |
|---|---|---|
| `outgoing_chan_id` | which channel the payment leaves by | **exact channel** |
| `last_hop_pubkey` | which peer the payment returns through | **peer only** — LND picks the channel |

So the algorithm models **sources per channel** and **destinations per peer**, aggregated across
every channel we hold with that peer. Everything downstream follows: a source is a `SourceChannel`,
a destination is a `DestinationPeer` with a `Members` list.

## 3. Inputs

Built at `AutoRebalanceJob.RebalanceNode` from one `ListChannels` per node plus the per-channel
routing state, via [`RoutingEngineSnapshotService`](../src/Services/RoutingEngineSnapshotService.cs).

| Input | Source | Meaning |
|---|---|---|
| `LocalSats` / `RemoteSats` | `ListChannels` | Live balances. Used for **sizing**. Note `base = local + remote`, not `Capacity` — this excludes the commit fee and reserve, so ratios are over the balance that can actually move. |
| `EmaLocalRatio` | `ChannelRoutingState` | EMA-smoothed `local / base`. Used for the **direction** decision. |
| `TargetLocalRatio` | `ChannelRoutingState` | Where we want that ratio. |
| `Active` | `ListChannels` | Inactive channels take no part, either side. |
| `SourceOptIn` | `Channel.IsAutoRebalanceEnabled` **and** not already being drained | Per-channel opt-in, ANDed with `GetPendingInFlightSourceChannelIds`. **Source side only** — see §8. |

The split matters: **direction is decided on the EMA** so a single forward can't trigger a payment,
while **sizing uses live balances** because those are the sats that actually move.

---

## 4. Stage 1 — `Classify`: four pools

Each channel/peer lands in one of four pools. `Sources` and `Destinations` are imbalances the engine
actually **detected**. The two fallback pools are everything else that is merely *able* to take
part, and exist so a detected imbalance is still acted on when nothing on the opposite side tripped.

Let `T` = `TargetLocalRatio`, `E` = `EmaLocalRatio`, `db` = `ROUTING_ENGINE_REBALANCE_DEADBAND`
(0.15), `min` = `REBALANCE_MIN_AMOUNT_SATS` (10 k).

### Source side — per channel

Gated on `Active && SourceOptIn && base > 0`.

| Pool | Condition | How much it may give |
|---|---|---|
| `Sources` | `E − T > db` **and** excess `> 0` | `local − round(T · base)` — down to its **own target** |
| `FallbackSources` | otherwise, if lendable `> min` | `local − round(max(0, T − db) · base)` — down to the **low edge of its deadband** |

### Destination side — per peer, balance-weighted

Channels grouped by `PeerPubKey`; `aggEma` and `aggTarget` are weighted by each channel's `base`.

| Pool | Condition | How much it may take |
|---|---|---|
| `Destinations` | `aggEma − aggTarget < −db` **and** deficit `> 0` | `round(Σ T·base) − peerLocal` — up to its **own target** |
| `FallbackDestinations` | otherwise, if absorbable `> min` | `round(min(1, aggTarget + db) · peerBase) − peerLocal` — up to the **high edge of its deadband** |

Note the destination loop groups on `Active` alone — it does **not** filter on `SourceOptIn`.
Opting a channel out stops it being drained, not from receiving (§8).

### Why the fallback pools stop at the deadband edge

This is the load-bearing invariant. A fallback source lends only down to `T − db` and a fallback
destination absorbs only up to `aggTarget + db`, so **taking part can never flip a channel to the
opposite role next cycle**. Without it the engine oscillates, paying fees in both directions.

```
                 lend down to            fill up to
                 T − db                  T + db
                     |                       |
   0 ─────────────[══│═══════ T ═══════│══]───────────── 1
                     └──── deadband ───┘
        source pool  ◄──                 ──►  destination pool
```

The clamp holds **per plan**, not per run — see the per-run guard in §5.

### The min-amount floor applies only to the fallback pools

`min` gates `FallbackSources` and `FallbackDestinations`, because below 10 k sats a hop isn't worth
its fee. It is **not** applied to `Sources` or `Destinations`, and nothing downstream re-checks it —
so a detected pair whose overlap is tiny will produce a sub-10 k plan. See §8.

---

## 5. Stage 2 — `BuildPlans`: pairing

All four pools are sorted **largest imbalance first** (`ExcessSats` desc for sources,
`DeficitSats` desc for destinations), then paired greedily. Bigger imbalances get first claim on the
available liquidity; no destination is preferred for earning more, no source for costing less. What
keeps a pairing from losing money is the profit gate, not the ranking.

`MaxInitiations` (5) is enforced **here** — `BuildPlans` returns as soon as it holds that many
plans, so the planner never builds more than the job can dispatch.

### Pass 1 — refill every detected destination

For each `Destinations` entry, largest deficit first:

1. Compute the peer's balance-weighted earn ppm. **No known rate ⇒ skip the peer** — without it
   there is no safe profit gate, and guessing is worse than waiting.
2. Take the largest free source from `Sources`; failing that, from `FallbackSources`
   (`IsFallbackPairing = true`). "Free" means not already used this run **and not on the
   destination's own peer** — draining a peer to refill the same peer is a no-op.
3. Size and gate it (§6). If it survives, mark the source used and emit the plan.

### Pass 2 — drain every detected source pass 1 left unused

For each unused `Sources` entry, walk `FallbackDestinations` (largest first) until one yields a
viable plan, then stop. This is what drains a too-local channel when nothing tripped the destination
trigger.

### Invariants

| Rule | Mechanism |
|---|---|
| A source is drained at most once per run | `usedSourceIds` — concurrent drains would race on the same balance |
| A fallback destination is refilled at most once per run | `refilledFallbackPeers` — its room is a **per-run allowance**; two sources each sized against the full room would overshoot the deadband ceiling and undo §4's invariant |
| Never pair a peer with itself | `s.PeerPubKey != dest.PeerPubKey` |
| A rejected pairing burns nothing | Both sets are written **after** `TryBuildPlan` returns non-null, so a plan killed by the profit gate leaves the source and peer available downstream |
| Pass 1 needs no destination guard | It visits each detected destination once, and `Classify` puts a peer in `Destinations` **or** `FallbackDestinations`, never both |

## 6. Stage 3 — `TryBuildPlan`: sizing and the profit gate

**Profit gate.** Cost is capped at a fraction of what the destination earns:

```
maxCostPpm = round(CostToEarnRatio · destEarnPpm)     # default ratio 0.5
if (maxCostPpm < 1) → no plan
maxFeePct  = maxCostPpm / 10_000                      # ppm → percent
```

A near-zero-earn destination yields a near-zero cap and is dropped: there is no margin to pay a
route with. The `maxCostPpm < 1` check is **load-bearing** —
[`RebalanceService.ResolveMaxFeePct`](../src/Services/RebalanceService.cs) treats any value `<= 0`
as "use the default", so emitting a `0` cap would silently become `REBALANCE_DEFAULT_MAX_FEE_PCT`
(0.05 %) and pay real fees on a pairing judged not worth paying for.

**Sizing.** What the source can give, bounded by what the destination can take, then by the ceiling:

```
amount = min( source.ExcessSats, dest.DeficitSats, MaxAmountSats )
```

Fee units are consistent end to end — `pct` where `0.125` means 0.125 %:
`maxCostPpm/10_000` → `WorstCaseFeeSats` (`sats × pct / 100`) → `ComputeFeeLimitMsat`
(`sats × pct × 10`).

## 7. Stage 4 — `AutoRebalanceJob`: dispatch

Per node with `AutoRebalanceEnabled`, gated by the global `ROUTING_ENGINE_ENABLED` kill switch:

1. **Budget configured?** `RebalanceBudgetSats > 0`, else skip — checked before any LND round-trip.
2. **Refresh the budget period** if `now − RebalanceBudgetStartDatetime >= RebalanceBudgetRefreshInterval`.
3. **Remaining budget** = budget − `GetPessimisticConsumedFeesSince(periodStart)`. That query counts
   `Succeeded` rows at their **actual** fee paid and `Pending`/`InFlight` rows at their
   **worst-case reservation**, so an in-flight rebalance is charged in full immediately and can only
   ever release budget back. Exhausted ⇒ skip the node.
4. **In-flight cap** — `GetInFlightByNode` counts `Pending` + `InFlight`. At the cap ⇒ skip the node.
5. **Snapshot → `Classify`.** If both detected pools are empty, stop here — before spending an LND
   round-trip on pricing.
6. **Price the node → `BuildPlans`.**
7. **Dispatch loop**, per plan in order:
   - `inFlight + initiations >= maxInFlight` ⇒ **break** (abandons every remaining plan)
   - `WorstCaseFeeSats(amount, maxFeePct) > remainingBudget` ⇒ **continue** (skips this plan only)
   - `RoutingEngineDryRun` ⇒ log, charge the budget, count the initiation, continue
   - else dispatch, audit, decrement the budget, count the initiation

Because the in-flight cap **breaks** the loop, every remaining plan is abandoned — so the job logs
the dropped count *and* each dropped plan's `Reason` individually, rather than leaving
"initiated 1 rebalance(s)" to read as "only one was worth doing".

Every live dispatch also writes an `AuditActionType.RebalanceInitiated` entry carrying the amount,
fee cap, reserved fee, remaining and total budget, in-flight counts, and the plan's reason.

### Pricing: one `FeeReport` for the whole node

`ILightningService.GetLocalOutboundFeeRatesPpmAsync` wraps LND's `FeeReport`, which returns the
node's own fee schedule for every channel in **one** round-trip. `ChannelFeeReport.fee_per_mil` is
the same ppm figure as `RoutingPolicy.fee_rate_milli_msat`, but reported for our own side — so
unlike `GetChanInfo` there is no `Node1Pub` / `Node2Pub` policy to disambiguate.

Because the whole node costs one call, there is nothing to gain from working out which channels the
planner will actually consult. Everything is priced, keyed by LND `chan_id` — which is what the
planner's own records carry, so nothing has to be re-keyed into NodeGuard's channel ids.

`null` from that call means `FeeReport` failed, and the job **skips the node**. It must not be
treated as an empty map: with no rates every destination is unpriceable, and the run would report
"no profitable rebalance plans" when the truth is that it could not see the fees at all.

### Execution

The job **awaits** `RebalanceService.RebalanceAsync`, which persists the `Rebalance` row and runs
the first attempt inline. So a cycle's plans execute **serially**, and a node's run lasts as long as
its dispatched payments take (each bounded by `DEFAULT_REBALANCE_TIMEOUT_SECONDS`, 180 s). With
`MaxInitiations` at 5 that is a worst case of ~15 minutes for a run — longer than the 10-minute
cadence, though `[DisallowConcurrentExecution]` means the next fire is skipped rather than overlapped.

`CancellationToken.None` is passed deliberately: the job's own token dies with the run and would
cancel an in-flight payment the moment the cycle ends.

Retries are `RebalanceService`'s business, not the job's. It logs and audits its own lifecycle
(attempt, terminal status, retry scheduling), and
[`MonitorRebalancesJob`](../src/Jobs/MonitorRebalancesJob.cs) reconciles against LND anything the
process abandons.

`RetryMaxFeePct` is set to the plan's own `MaxFeePct`, so retries escalate *to* the profitable
ceiling and no further; leaving it null would let them climb to the 0.05 % default.

---

## 8. Known limitations

A peer can be refilled and drained in the same run

The same-peer guard is **per plan**. Nothing checks the plan set as a whole, so a peer whose
aggregate trips the destination trigger while one of its channels trips the source trigger gets both
done at once: funded from another peer, and drained via its own too-local channel.

Because `last_hop_pubkey` pins only the peer, LND may land the refill on the very channel being
drained — reachable whenever that channel's remote balance covers the refill amount. Both legs pay
routing fees to accomplish nothing, and the drained channel can end up *more* too-local. Serial
dispatch makes the two legs unlikely to be in flight simultaneously, but does nothing to stop the
pair being planned.

**Fix:** track peers drained and peers refilled across both passes; refuse a pairing that would put
one peer in both sets.

### Opting a channel out doesn't stop it receiving

`IsAutoRebalanceEnabled` reaches `Classify` only through `SourceOptIn`, which gates draining. The
destination loop filters on `Active` alone, so an opted-out channel still receives rebalance
traffic. The same asymmetry applies to in-flight exclusion: nothing stops a second rebalance aiming
at a peer whose previous refill is still pending.

Arguably a policy question rather than a bug — "don't drain this" and "don't send traffic here" are
different intents — but the current toggle only implements the first.

### A slow run can starve the cadence

Because dispatch is awaited and serial (§7), a node whose payments time out can occupy the job for
longer than the 10-minute interval, and `[DisallowConcurrentExecution]` then skips fires. With
several nodes enabled, nodes later in the list are dispatched consistently later than nodes earlier
in it — the loop is sequential across nodes too.

### Pairing ties depend on LND's response order

Sorting by imbalance size makes pairing deterministic for a given snapshot, but ties are broken by
`ListChannels` order, which LND does not contract as stable. Among equally-imbalanced channels,
which one gets drained can differ between runs on an otherwise unchanged node.

---

## 9. Constant reference (current defaults)

**Planning**

| Constant | Default | Effect |
|---|---|---|
| `ROUTING_ENGINE_ENABLED` | `false` | Global kill switch for both actuators. |
| `ROUTING_ENGINE_REBALANCE_JOB_INTERVAL_MINUTES` | `10` | Job cadence (1 min in dev). |
| `ROUTING_ENGINE_REBALANCE_DEADBAND` | `0.15` | Trigger distance **and** the fallback pools' clamp. |
| `REBALANCE_MIN_AMOUNT_SATS` | `10_000` | Floor for the fallback pools only (§8). |
| `ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS` | `10_000_000` | Per-plan ceiling. |
| `ROUTING_ENGINE_REBALANCE_MAX_INITIATIONS_PER_RUN` | `5` | Plans built per node per run. |
| `ROUTING_ENGINE_REBALANCE_DEFAULT_COST_TO_EARN_RATIO` | `0.5` | Profit gate; per-node `MaxRebalanceCostToEarnRatio` overrides. |

**Budget and concurrency**

| Constant | Default | Effect |
|---|---|---|
| `ROUTING_ENGINE_REBALANCE_DEFAULT_MAX_IN_FLIGHT` | `5` | Concurrent `Pending`+`InFlight`; per-node `MaxRebalancesInFlight` overrides. |
| `ROUTING_ENGINE_REBALANCE_DEFAULT_BUDGET_REFRESH_HOURS` | `24` | Budget window; per-node `RebalanceBudgetRefreshInterval` overrides. |

**Execution (shared with manual and gRPC rebalances)**

| Constant | Default | Effect |
|---|---|---|
| `DEFAULT_REBALANCE_TIMEOUT_SECONDS` | `180` | Payment timeout when the caller supplies none. |
| `REBALANCE_MAX_ATTEMPTS` | `3` | Attempts per rebalance. |
| `REBALANCE_INITIAL_RETRY_DELAY_SECONDS` | `60` | First retry delay. |
| `REBALANCE_RETRY_BACKOFF_MULTIPLIER` | `2.0` | `delay = 60 · 2^(attempt−2)` ⇒ 60 s, 120 s. |
| `REBALANCE_AMOUNT_BACKOFF_RATIO` | `0.8` | Retries shrink the amount by this factor per attempt. |
| `REBALANCE_MAX_PARTS` | `32` | Max MPP shards. |
| `REBALANCE_DEFAULT_MAX_FEE_PCT` | `0.05` | Fallback cap when none is supplied (0.05 %). |
| `REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT` | `0.05` | Fallback retry cap. |
| `REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS` | `24` | How far back `MonitorRebalancesJob` reconciles. |

Per-node overrides live on [`Node`](../src/Data/Models/Node.cs): `AutoRebalanceEnabled`,
`RebalanceBudgetSats`, `RebalanceBudgetRefreshInterval`, `MaxRebalancesInFlight`,
`MaxRebalanceCostToEarnRatio`, `RoutingEngineDryRun`. Per-channel:
[`Channel.IsAutoRebalanceEnabled`](../src/Data/Models/Channel.cs).
