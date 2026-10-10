# Auto Channel Open

When a node keeps refusing forwards because it has no outbound left toward a peer, open another
channel to that peer.

| Component | Role |
|---|---|
| [`ChannelOpenInitiatorService`](../src/Services/ChannelOpenInitiatorService.cs) | Planner. Pure, no I/O: demand in, recommendations out. |
| [`AutoChannelOpenJob`](../src/Jobs/AutoChannelOpenJob.cs) | Gathers inputs, runs the planner, stores the results. |
| [`ChannelOpenRecommendation`](../src/Data/Models/ChannelOpenRecommendation.cs) | A proposed open plus the evidence behind it. |
| [`ChannelOpenPromotionService`](../src/Services/ChannelOpenPromotionService.cs) | Turns a recommendation into a `ChannelOperationRequest`. Used by Auto mode and by the panel. |
| [`RoutingManagement.razor`](../src/Pages/RoutingManagement.razor) | Panel to review, resize, promote or dismiss. |
| [`ChannelOpenJob`](../src/Jobs/ChannelOpenJob.cs) | The existing executor (PSBT, signing, retries). Unchanged. |

Same split as the rebalancer ([rebalance-algorithm.md](rebalance-algorithm.md)): decisions live in a
pure service, and the job only fetches inputs and acts on the output.

## Overview

Every 30 minutes, for each node with the feature on:

1. Load the last 24h of forwards we refused with `INSUFFICIENT_BALANCE`.
2. Group them by peer, then collapse them into bursts (roughly one burst per payment).
3. Keep peers with at least 10 bursts and at least 100k sats of missed fees that are still short on
   outbound right now.
4. Size a channel that covers the drain for 2 days, never smaller than 3× the largest refused
   payment, and clamp it to the node, wallet and budget limits.
5. Drop it if one full drain wouldn't pay back the on-chain cost twice.
6. Save it as a recommendation. In Auto mode, also turn it into a channel request.

Scope: this only grows capacity to peers we **already have channels with**. A refused forward always
names one of our own channels, so it can't point at a new peer. It also only fixes a lack of
**outbound**. A channel we fund starts with all its capacity on our side, so it adds outbound and
nothing else. Inbound shortages fail on the peer's node and never reach our HTLC stream. For those,
the fixes are the peer opening to us, a swap-in, or a rebalance.

This runs independently of the fee engine and the rebalancer. Those two are cheaper and should handle
most shortages. This covers the case they can't fix: steady one-way demand with nothing to rebalance
from.

---

## 1. The signal

Source: [`ForwardingHtlcEvent`](../src/Data/Models/ForwardingHtlcEvent.cs) rows written by
[`NodeHtlcSubscribeJob`](../src/Jobs/NodeHtlcSubscribeJob.cs), via
`ForwardingHtlcEventRepository.GetInsufficientBalanceFailures`.

| Field | Value |
|---|---|
| `ManagedNodePubKey` | the node being planned for |
| `EventType` | `Forward` |
| `EventCase` | `LinkFailEvent` |
| `FailureDetail` | `INSUFFICIENT_BALANCE` (6) |
| `EventTimestamp` | within the last `AUTO_CHANNEL_OPEN_WINDOW_HOURS` (24) |

Nothing else counts. `HTLC_EXCEEDS_MAX` (5) is our own max-HTLC limit, not a shortage, and counting it
would mean buying capacity to work around a limit we set ourselves. Peer offline (3) and forwards
disabled (9) aren't capacity problems either.

The query is served by the `(ManagedNodePubKey, EventTimestamp)` index.

### Group by peer, not by channel

LND forwards non-strictly. It retries an HTLC on every channel we have to the peer and reports
whichever one it tried last. Counting per channel would split one peer's demand across its channels,
so a peer we already have three channels with would look a third as busy.

Each row's `OutgoingChannelId` is mapped to a peer pubkey using `ListChannels`. Rows on channels that
are no longer open are dropped. Aliases are only used for display.

## 2. Bursts: from rows to payments

We want to react to many payments, not to one payment that kept retrying. The forward stream has no
payment hash, so rows can't be deduplicated exactly.

What one refused payment looks like from our side depends on the sender:

- **Retry with split shards (the common case).** A sender's first attempt through us fails. Its
  pathfinding (mission control, in LND's case) now avoids sending that amount or more through our
  hop, so it doesn't retry the same amount. When no route fits the whole amount, LND splits it in
  half and tries again. If the halves come back through us and fail too, one 1M payment leaves rows
  for 1M, 500k, 500k, and maybe 250k × 4.
- **Split before sending.** The sender splits the payment into shards up front. Each shard picks its
  own route, so only some of them may pass through us. The rows we get are a subset of the payment,
  often of equal size.
- **Identical retries.** The same amount through the same hop again. This is unlikely within a
  burst, because the sender has just learned that our hop can't carry that amount.

All of this happens within seconds. So for each peer, rows are sorted by time and a new burst
starts whenever the gap since the previous row is more than `AUTO_CHANNEL_OPEN_BURST_GAP_SECONDS`
(120s). A burst stands in for one payment.

For each burst:

- **Payment size** = sum of the *distinct* `OutgoingAmountMsat` values.
- **Missed fee** = for each distinct amount, the fee the sender offered on it (`FeeMsat`, incoming
  minus outgoing amount), summed. This includes base fee and inbound fees, which a rate alone misses.
  If no row with that amount has `FeeMsat`, it falls back to amount × ppm. The ppm is the highest
  on the burst's rows, and a row with no `RoutingFeePpm` counts as the peer's current highest rate.

No rule gets every shape right, and summing distinct amounts is wrong in both of the common ones:

| Burst through us | Real | Sum all | **Sum distinct** (used) | Largest |
|---|---|---|---|---|
| Halving retry: 1M, then 500k + 500k | 1M | 2M | 1.5M | 1M |
| Equal split: 500k + 500k | 1M | 1M | 500k | 500k |
| Two payments: 800k + 300k | 1.1M | 1.1M | 1.1M | 800k |

The errors run in both directions. Halving retries overcount (more levels make it worse), and equal
splits undercount. Taking the largest is exact for halving retries but merges two payments that land
in the same burst. Summing everything is right for split payments but doubles the volume with every
halving level.

A better key is available: `OutgoingTimelock` is stored on every row. Shards and quick retries of one
payment usually share it (same block, same path after us), while different payments usually don't
(different destination, route or final CLTV delta). Grouping by (burst, timelock) and taking the
largest amount per group would fix the halving and two-payment cases. It hasn't been implemented or
checked against real traffic yet (see [§12](#12-future-work)).

The gap setting matters more than it seems. Retries can only be collapsed if they land in the same
burst. If the gap is too small, they end up in separate bursts and get counted twice. If it's too
large, separate payments merge (see [§11](#11-known-limitations)).

Per peer, the window comes down to:

| Metric | Meaning |
|---|---|
| `Bursts` | how many payments we turned away |
| `MissedFeeMsat` | what they would have paid us |
| `MissedSats` | their total size |
| `MaxBurstPaymentSats` | the largest one |
| `FailedAttempts` | raw row count, shown only, never used in a decision |

## 3. The gate

A peer becomes a recommendation only if all of these pass, in this order:

1. **Not on cooldown.** No promotion or dismissal for this peer in the last
   `AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS` (24).
2. **Has a fee policy.** LND's `FeeReport` returns a policy for at least one of our channels to it.
   Without one, the new channel can't be priced, so there's no recommendation (see
   [§5](#the-fee-the-channel-opens-at)).
3. **Enough payments.** `Bursts >= AUTO_CHANNEL_OPEN_MIN_BURSTS` (10). A few big failed payments
   are a problem with those payments, not with the route.
4. **Worth enough.** `MissedFeeMsat >= AUTO_CHANNEL_OPEN_MIN_MISSED_FEE_MSAT` (100,000,000 msat =
   100k sats). Lots of tiny failures are noise.
5. **Still short right now.**

   ```
   usableLocal = Σ max(0, local_balance - local_chan_reserve)   over all channels to the peer
   reject if usableLocal >= MaxBurstPaymentSats × AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER (3.0)
   ```

   The events don't record our balance at the time, so a peer that was briefly empty and one that is
   empty all the time look the same in the data. Checking the live balance tells them apart. If we
   can already cover 3× the largest refused payment, the shortage has passed.

Why 10: in 24h of prod data (2026-10-05), 11 of the 14 peers that passed the fee gate had 78–249
bursts. The other three had 18, 5 and 3. The 3-burst one was 179k sats of fees from three payments of
roughly 30M sats each, all from one sender, which is bigger than any channel we'd open. A minimum of 10
drops those few-giant-payment cases and changes nothing for the busy peers. Don't read the count as
"10 different payments", though (see [§11](#11-known-limitations)).

The live check doesn't catch everything. A channel full of in-flight HTLCs has those funds taken out
of `local_balance`, so it looks empty and passes. Hitting the HTLC *count* limit (483 slots, small
payments) is caught, because small payments barely reduce the balance.

## 4. Sizing

The size should answer one question: how long does the channel last before it's empty again?

| Input | Source |
|---|---|
| `Out` | settled outbound to the peer in the window (`GetOutgoingAmountsMsatByChannel`) |
| `In` | settled inbound from the peer in the window (`GetIncomingAmountsMsatByChannel`) |
| `Missed` | `MissedSats` from §2 |

`Out + Missed` is the total demand, both served and refused. `In` is how fast the channel refills on
its own.

```
drainPerDay    = (Out + Missed) / windowDays
refillPerDay   = In / windowDays
netDrainPerDay = max(0, drainPerDay - refillPerDay)

runway = netDrainPerDay      × AUTO_CHANNEL_OPEN_TARGET_RUNWAY_DAYS   (2)
floor  = MaxBurstPaymentSats × AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER    (3.0)
target = max(runway, floor)
```

`Regime` records which one was larger:

- **Draining**: the peer drains faster than it refills, so we buy days of runway.
- **Buffer**: drain and refill roughly cancel out, and the channel only runs dry when a big payment
  arrives. Room for the largest refused payment, with headroom, is enough.

The floor applies in both cases, so `Regime` tells you which number won, not how the peer behaves.
The badge on the panel always matches the capacity shown next to it.

Existing capacity to the peer isn't an input. The drain rate is the same whatever capacity sits
behind it.

> **Tune `MIN_MISSED_FEE_MSAT` and `TARGET_RUNWAY_DAYS` together.** Together they set the implied
> channel size. At 900 ppm, the 100k sat gate only lets through peers refusing about 1.1 BTC a day,
> and 2 days of runway on that is far above any normal channel size. With the defaults, a clamp sets
> the final size of every plan, usually the node max. This is intended. To let the runway formula
> pick the size, lower the gate: 2k sats/day at 2 days comes to roughly 4.5M sat channels at 900 ppm.

### Clamps

Applied in this order. `BindingClamp` records the last one that reduced the size.

| Step | Rule |
|---|---|
| Reserve | `target / 0.99`. LND keeps ~1% as channel reserve, which can't be used for routing. Without this the usable outbound falls short of the target. Not reported as a clamp. |
| Wumbo ceiling | 16,777,215 sats. **Regtest only.** On other networks wumbo is assumed. |
| Node max | `AutoChannelOpenMaxSizeSats` |
| Wallet | confirmed balance of the funding wallet |
| Budget | remaining budget for the period (§7) |

Then two checks on the final size. Failing either one means no recommendation, and the reason is
logged:

- **Profitability.** One full drain has to pay back the on-chain cost with margin:

  ```
  chainCostSats = hourFeeSatPerVb × 310             (assumed size: ≈140 vB open + ≈170 vB close)
  earnedSats    = (target × 0.99) × InheritedFeeRatePpm / 1e6
  reject if chainCostSats / earnedSats > MaxChannelOpenCostToEarnRatio   (node value, default 0.5)
  ```

  `hourFeeSatPerVb` is the NBXplorer/mempool `HourFee` rate. The funding transaction itself is sent
  at `EconomyFee`, so the estimate is on the high side.

  The rejection message says whether a clamp caused it (for example, the wallet or budget cut the
  channel too small), so you know whether to fix the setting or wait for cheaper fees.
- **Node min.** `target < AutoChannelOpenMinSizeSats` is rejected, not rounded up.

## 5. Modes and promotion

`Node.AutoChannelOpenEnabled` turns the feature on. `Node.AutoChannelOpenMode` picks what happens
next:

| Mode | What happens |
|---|---|
| `Recommendation` (default) | Saves the recommendation and stops. A person promotes it from the panel. |
| `Auto` | Saves it and promotes it straight away at the suggested size. |

Both modes promote through the same `Promote` call. It creates a `Pending` `ChannelOperationRequest`
with the funding wallet `Node.AutoChannelOpenWalletId`, the managed node as source, the peer as
destination (created with `GetOrCreateByPubKey` if needed), and `EconomyFee` for the funding
transaction. What happens next depends on the wallet:

- **Hot wallet.** `Promote` builds the template PSBT and schedules `ChannelOpenJob` directly. With
  Auto on, this runs end to end with no one involved. If the template can't be built (for example,
  no UTXOs), the request is marked `Failed`.
- **Multisig.** The request stays `Pending` until the signers sign on
  [`ChannelRequests.razor`](../src/Pages/ChannelRequests.razor). Auto takes away the decision to
  open, not the signatures.

Under `RoutingEngineDryRun`, recommendations are still written, but Auto doesn't promote and the
panel's promote button is disabled.

The suggested size is only a starting point. You can change it on the panel before promoting, and,
for multisig, again on the `Pending` request. The evidence goes into the request's `Description`, so
whoever approves it can see why it exists.

### The fee the channel opens at

The new channel is a second channel to a peer we already charge, and it starts full on our side.
Opened at the global default (`DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM`, 1500), it would become the
cheapest way to reach a peer the fee engine has probably already priced at 2500–3000 ppm. We'd sell
the new capacity for about half of what the engine already found the peer is worth.

So the planner records the **highest-rate policy among our current channels to the peer**, taken
from LND's `FeeReport`, and stores it as `InheritedFeeRatePpm` / `InheritedBaseFeeMsat`. Base fee
and rate come from the same channel, never mixed. That one rate is used for:

- the profitability check above, so the check and the actual open always use the same price;
- the **Opens at** column on the panel;
- the request's `InitialChannelFeeRatePpm` / `InitialChannelBaseFeeMsat`, which can still be edited
  before signing.

It isn't looked up again at promotion. The gossip graph (`GetChanInfo`) lags behind our own policy
changes, and looking it up again could change the price after the operator approved it. The stored
value is refreshed every run, so it's at most one run old.

The fee engine leaves the new channel alone while it matters. Categorization waits
`ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS` (3024, ~3 weeks), and the optimizer skips
`Uncategorized` channels. So the channel keeps its opening price for the first three weeks, which is
when it's full and draining fastest.

## 6. Recommendation lifecycle

One row per (node, peer).

| Status | Meaning |
|---|---|
| `Open` | Waiting for a decision. Refreshed in place each run. |
| `Promoted` | Turned into a request (`ChannelOperationRequestId` set). |
| `Dismissed` | Rejected on the panel. |
| `Expired` | Not produced by the latest run. |
| `Failed` | Promoted, but the request ended `Failed` or `Cancelled`, so no channel was opened. |

Each run, before planning:

1. All `Open` rows for the node become `Expired`. The run then re-opens the ones it still produces,
   so the panel only ever shows the latest run.
2. `Promoted` rows whose request ended `Failed` or `Cancelled` become `Failed`.

Only `Promoted` and `Dismissed` start the cooldown. A `Failed` promotion releases the peer right
away: the channel never opened and the demand is still there. A request **rejected** by an operator
leaves the row `Promoted`, so it counts as a decision against the peer, just like a dismissal.

The upsert reuses existing rows. When a peer comes back after its cooldown, its row returns to `Open`
and `ChannelOperationRequestId` and `DismissReason` are cleared. The earlier decision is still on the
request and in the audit log. A row is skipped (left as is) only when it's `Promoted` and its request
hasn't finished yet. A second open would double up on one that's already underway.

## 7. Budget

Per node: `AutoChannelOpenBudgetSats`, `AutoChannelOpenBudgetRefreshInterval` (default
`AUTO_CHANNEL_OPEN_DEFAULT_BUDGET_REFRESH_HOURS`, 720h), `AutoChannelOpenBudgetStartDatetime`. Same
shape as the rebalance budget.

- **Spent** is the sum of `SatsAmount` on open requests from this node created since the period
  start, excluding `Cancelled`, `Rejected` and `Failed`. Opens made in the UI or through gRPC count
  too, not just the ones this job created.
- The job skips the node when nothing is left. Within a run, each promoted plan is subtracted, so 5
  plans can't each spend the whole budget.
- No budget configured means the node is skipped.

**The budget limits the job, not people.** Promoting from the panel doesn't check it. Every plan is
sized against the full remaining budget, so promoting several at once can overshoot. That spend
still counts, so the job stops proposing on its next run, and the panel shows what's been spent next
to the budget.

## 8. The panel

On [`RoutingManagement.razor`](../src/Pages/RoutingManagement.razor), below the node gates:

- **Header**: an on/off switch and the Recommendation / Auto selector. It has its own controls
  instead of going through `RoutingFlag`, because that enum also creates a per-channel toggle column,
  and channel opening has no per-channel setting. It shows warnings when the feature is off globally
  or the node has no funding wallet.
- **Budget**: the budget and the amount spent in the current period.
- **Table**: one row per `Open` recommendation, highest missed fees first. Columns: Peer, Payments,
  Missed fees, Drain / refill, Sizing (regime badge plus the binding clamp), Opens at, Capacity
  (editable, limited to the node min/max), and Create request / Dismiss actions.
- **KPI strip**: number of open recommendations.

Funding wallet, budget, min/max size and cost-to-earn ratio are set in the node's liquidity settings
on [`Nodes.razor`](../src/Pages/Nodes.razor).

## 9. Configuration

### Env ([`Constants.cs`](../src/Helpers/Constants.cs))

| Constant | Default | Meaning |
|---|---|---|
| `AUTO_CHANNEL_OPEN_ENABLED` | `false` | Global switch. `ROUTING_ENGINE_ENABLED` must also be on. |
| `AUTO_CHANNEL_OPEN_JOB_INTERVAL_MINUTES` | `30` | How often the job runs (1 in dev) |
| `AUTO_CHANNEL_OPEN_WINDOW_HOURS` | `24` | Demand window |
| `AUTO_CHANNEL_OPEN_BURST_GAP_SECONDS` | `120` | Gap that separates two payments |
| `AUTO_CHANNEL_OPEN_MIN_BURSTS` | `10` | Minimum bursts |
| `AUTO_CHANNEL_OPEN_MIN_MISSED_FEE_MSAT` | `100_000_000` | Minimum missed fees. Tune with runway days. |
| `AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER` | `3.0` | × largest refused payment, for the live check and the size floor |
| `AUTO_CHANNEL_OPEN_TARGET_RUNWAY_DAYS` | `2` | Days of runway to buy |
| `AUTO_CHANNEL_OPEN_DEFAULT_COST_TO_EARN_RATIO` | `0.5` | Used when the node doesn't set `MaxChannelOpenCostToEarnRatio` |
| `AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS` | `24` | Per-peer cooldown after a promotion or dismissal |
| `AUTO_CHANNEL_OPEN_MAX_PLANS_PER_RUN` | `5` | Plans per node per run. In Auto this is the only cap on opens in flight. |
| `AUTO_CHANNEL_OPEN_DEFAULT_BUDGET_REFRESH_HOURS` | `720` | Budget period when the node doesn't set one |

### Per node

`AutoChannelOpenEnabled`, `AutoChannelOpenMode`, `AutoChannelOpenWalletId`,
`AutoChannelOpenBudgetSats`, `AutoChannelOpenBudgetRefreshInterval`,
`AutoChannelOpenBudgetStartDatetime`, `AutoChannelOpenMinSizeSats`, `AutoChannelOpenMaxSizeSats`,
`MaxChannelOpenCostToEarnRatio`.

## 10. Worked example

Peer `03ab…`, our highest outbound rate to it is 900 ppm. 380 matching rows in 24h across our three
channels to it.

**Bursts.** The 380 rows collapse into 42 bursts. Several bursts are 12–15 rows within a few
seconds: one payment and the smaller split retries that followed it. Burst sizes add up to
120,000,000 sats, and the largest is 4,000,000. Every row was refused at 900 ppm, and the offered
fees on the distinct amounts add up to 108,000,000 msat (the rate part only; base fees are left out
to keep the numbers round).

**Gate.**

```
Bursts         42 >= 10                                         ✓
MissedFeeMsat  Σ offered FeeMsat per distinct amount = 108_000_000 msat
               (≈ 120_000_000 sats × 900 ppm)
               >= 100_000_000                                   ✓
Live check     usable local 2,000,000 < 4,000,000 × 3 = 12,000,000   ✓ still short
```

At 900 ppm, clearing the fee gate takes about 1.1 BTC of refused volume in a day. This is a very busy
peer.

**Sizing.** Settled `Out` = 40,000,000, settled `In` = 25,000,000.

```
drainPerDay    = 40_000_000 + 120_000_000 = 160_000_000
netDrainPerDay = 160_000_000 - 25_000_000 = 135_000_000
runway         = 135_000_000 × 2          = 270_000_000
floor          =   4_000_000 × 3          =  12_000_000
target         = 270_000_000              → Draining
```

| Step | Result |
|---|---|
| Reserve (/0.99) | 272,727,272 |
| Wumbo ceiling | skipped on mainnet (16,777,215 on regtest) |
| Node max 16M | **16,000,000**, binding |
| Wallet 50M, budget 20M | unchanged |
| Profitability | 15,840,000 × 900 ppm = 14,256 sats earned per drain vs 3,100 sats of chain cost (10 sat/vB × 310 vB assumed size) → 0.22 ≤ 0.5 ✓ |

Result: a 16M sat recommendation, Draining, bound by `NodeMax`, opening at 900 ppm.

If `In` had been 200,000,000 (refilling faster than it drains), the net drain would be 0, the floor
would win, and the plan would be 12,000,000 / 0.99 = 12,121,212 sats in the Buffer regime.

## 11. Known limitations

**A burst is only an approximation of a payment.** A sender retrying more than 120s apart creates
extra bursts and overcounts. Two payments less than 120s apart merge into one and undercount. An MPP payment split across multiple parts may also be miscounted as one single payment.

**Bursts mostly measure how often one sender retries.** In the prod data above, a single incoming
peer brought 46–100% of each peer's missed fee. One sender (speed3) retried the same ~5M sat payment
83 times over 16 hours, every 8–30 minutes, with the amount slowly drifting down. That counted as 83
bursts and about 85% of that peer's 1.1M sats of "missed fees", for what was really one payment worth
about 10k sats. Every retry had a new `OutgoingTimelock` because the block height had moved on, so
grouping by timelock wouldn't catch it either. `MIN_BURSTS` can't filter this. Counting distinct
senders can (see [§12](#12-future-work)).

**Dense refusals merge into one burst.** The gap is measured from the previous row, so rows chain
together. A channel that's empty refuses everything until it refills. If refusals stay under 120s
apart, they become one burst lasting hours. That burst fails `MIN_BURSTS` on its own, and equal
amounts across the whole span collapse into one. At 380 rows/day the average spacing is ~227s, so it
doesn't happen. Above ~720 rows/day it starts to, and it does in prod: one peer with 11k refused rows in
a day collapsed into 85 bursts, some an hour long, where a 30s gap would have given 853. A wider gap
makes this worse, and a narrower one brings back the retry problem above.

**Payment size is wrong in both directions.** Summing distinct amounts overcounts halving retries
(1M + 500k for a 1M payment) and undercounts equal shards (500k for 1M). `MissedSats`,
`MissedFeeMsat` and `MaxBurstPaymentSats` inherit both errors. Overcounting makes the fee gate easier
to pass and the runway bigger. Undercounting makes the live check more likely to call the peer
covered and shrinks the floor. With the default settings the final size is usually set by a clamp,
so the error matters most at the fee gate.

**Missed fees are an upper bound.** They assume every refused payment would have settled through us.
Some went through another route or were abandoned.

**In-flight saturation looks like demand.** A channel kept empty by our own in-flight cap passes the
live check. The real fix is to store a balance snapshot on the event.

**The duplicate-open guard only sees our own opens.** A promoted recommendation whose request isn't
finished yet blocks a second one. A pending open to the same peer created by hand on
`ChannelRequests.razor` is not detected.

**Peer reachability isn't checked.** If the peer is offline, `ChannelOpenJob` fails, the
recommendation becomes `Failed`, and the next run can propose it again.

**The wallet clamp leaves no room for fees.** A plan can be sized to the wallet's full confirmed
balance, which doesn't leave enough to pay for the funding transaction.

**No coordination with the other tools.** The rebalancer may be refilling the same peer while this
proposes buying capacity for it. The only thing shared is the price: the new channel opens at what
we already charge that peer.

**Sizing trusts one day.** Both regimes stretch one 24h window into several days. A quiet or unusual
day sizes wrong in either direction.

**No real economic model.** The profitability check only asks whether one drain covers the chain
cost. Cost of capital and how long the channel stays open aren't considered.

## 12. Future work

1. **New-peer discovery.** `unknown_next_peer` failures name pubkeys we have no channel with, which
   is the signal for adding new peers rather than growing existing ones. First check that those
   events survive the `EventType == Forward` filter, and filter out stale graph entries and probes.
2. **Payback model.** Replace the flat fee threshold with amortized cost (open + close + cost of
   capital over the expected lifetime), and use the same model for sizing.
3. **Escalation ladder.** Only propose an open after rebalancing has clearly failed.
4. **Fee headroom in the wallet clamp**, and a reachability check (`ListPeers` / `ConnectPeer`)
   before promoting.
5. **Group bursts by timelock.** Treat (burst, `OutgoingTimelock`) as one payment and take its
   largest amount. First check prod data: count distinct timelocks per burst, and whether amounts
   under one timelock look like halving chains or unrelated payments.
6. **Smoothed demand** across several windows instead of a single 24h sample.
7. **Shared budget-period helper.** The rollover logic is now written three times (here,
    `AutoRebalanceJob`, `AutoLiquidityManagementJob`). Keep the fields separate and share the code.
8. **Removing bursts from consideration.** Rethink the value of bursts and consider removing the bursts concept entirely from the analysis.