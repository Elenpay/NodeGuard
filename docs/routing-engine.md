# Routing Engine — Overview

The routing engine is NodeGuard's autonomous channel-management layer. It observes how each
channel behaves, then actuates two levers to steer it: **fees** and **circular rebalances**.

It is built as **one sensor and two independent actuators**:

| Job | Role | Cadence | Writes to LND? |
|---|---|---|---|
| [`TargetRatioReevaluationJob`](../src/Jobs/TargetRatioReevaluationJob.cs) | **Sensor** — derives the signal every decision reads | 30 min | No |
| [`ChannelFeeOptimizerJob`](../src/Jobs/ChannelFeeOptimizerJob.cs) | **Fee actuator** — sets outbound/inbound ppm | 30 min | Yes |
| [`AutoRebalanceJob`](../src/Jobs/AutoRebalanceJob.cs) | **Rebalance actuator** — dispatches circular payments | 10 min | Yes (payments) |

Detailed guides, one per algorithm:

- **[fee-engine-algorithm.md](fee-engine-algorithm.md)** — the integral fee control law.
- **[rebalance-algorithm.md](rebalance-algorithm.md)** — source/destination classification and pairing.

This document covers what they share: the signal, the enable flags, and the rollout path.

---

## 1. The core idea

Every channel has a **dynamic target local-balance ratio**. Everything else is machinery to
(a) measure where the channel actually sits, (b) steer it toward that target, and (c) refuse to
spend more steering it than the channel earns.

```
                    ┌──────────────────────────────┐
   LND ListChannels │  TargetRatioReevaluationJob  │  ForwardingHtlcEvent
   ────────────────►│         (the sensor)         │◄──────────────────────
                    └──────────────┬───────────────┘
                                   │ writes
                                   ▼
                        ┌──────────────────────┐
                        │ ChannelRoutingState  │   EmaLocalRatio
                        │  (per channel/node)  │   TargetLocalRatio
                        └──────┬────────┬──────┘   PeerFlowCategory
                               │        │
                 ┌─────────────┘        └─────────────┐
                 ▼                                    ▼
     ┌───────────────────────┐            ┌───────────────────────┐
     │ ChannelFeeOptimizerJob│            │   AutoRebalanceJob    │
     │  price the imbalance  │            │  move sats directly   │
     └───────────┬───────────┘            └───────────┬───────────┘
                 │ UpdateChannelPolicy                │ SendPaymentV2
                 ▼                                    ▼
                LND                                  LND
```

The two actuators never call each other. They coordinate only through the database: a dispatched
rebalance persists a `Rebalance` row, and the fee job excludes any channel that is currently a
rebalance source.

## 2. The signal — `ChannelRoutingState`

[`TargetRatioReevaluationJob`](../src/Jobs/TargetRatioReevaluationJob.cs) is the only writer of
[`ChannelRoutingState`](../src/Data/Models/ChannelRoutingState.cs). Actuators **read** it and must
not re-derive any of it.

State is keyed by **(channel, managed node)** — one row *per side*. A channel between two managed
nodes gets two rows, because local balance, forwarding history and fee policy are all per-node
views of the same channel. (Deduping to the channel's initiator left the other side blind to its
own depleted channels, so they could never become rebalance destinations.)

### Eligibility

If the routing engine flag is enabled, the job runs for every managed, non-disabled node — it is
**not** gated by the per-node fee or rebalance flags, so the signal keeps accumulating even for
nodes whose actuators are off. Per node it needs both `GetInfo` (block height) and `ListChannels`;
if either is unavailable the node is skipped for that cycle. Per channel it requires a confirmed
open `Channel` row matched by `chan_id`, and `Active == true`.

### a. `EmaLocalRatio` — where the channel actually sits

```
observed = local / max(1, local + remote)
EmaLocalRatio ← α·observed + (1−α)·EmaLocalRatio        α = ROUTING_ENGINE_FEE_EMA_ALPHA (0.08)
```

Note the base is `local + remote`, **not** `Capacity` — this excludes the commit fee and reserve,
so the ratio is taken over the balance that can actually move.

On insert the EMA is **seeded with the first observation**, not with `0.5`, so a new channel has no
cold-start bias pulling it toward the middle.

At α = 0.08 the EMA has a half-life of roughly 24 cycles (~12 hours at the 30-minute cadence). ⍺ = 2/(cycles+1). This
is what makes a single large forward unable to trigger an actuator. 

### b. `NetFlowRatio` — which way the channel drifts

Over a 21-day window (`ROUTING_ENGINE_FLOW_WINDOW_DAYS`) of **settled** forwards:

```
push = Σ msat forwarded OUT through this channel   (drains our local)
pull = Σ msat forwarded IN  through this channel   (fills our local)

NetFlowRatio = (push − pull) / (push + pull)       0 when there is no flow at all
```

`NetFlowRatio > 0` means the channel is being drained; `< 0` means it is being filled.

### c. `PeerFlowCategory` — the peer's character

Two gates run before a channel is categorized at all:

| Gate | Rule | If it fails |
|---|---|---|
| **Age** (job-side) | `AgeBlocks >= ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS` (3024 ≈ 21 days) | Category and target are left untouched — `Uncategorized` at target `0.5`. `AgeBlocks` is null for pending/alias/zero-conf channels, which also fails the gate. |
| **Volume** (in `ComputeCategory`) | `push + pull >= ROUTING_ENGINE_FLOW_MIN_MSAT` (10 M sats) | Tentative category is `Uncategorized`. |

Past both gates, with `θ = ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD` (0.25):

| Tentative category | Condition | Meaning |
|---|---|---|
| **Sink** | `NetFlowRatio >= +θ` | The peer *drains* us. Our outbound liquidity here is valuable. |
| **Source** | `NetFlowRatio <= −θ` | The peer *feeds* us. Cheap outbound keeps the flow coming. |
| **Bidirectional** | in between | Balanced flow. |

Because the engine reads millions of existing `ForwardingHtlcEvent` rows, established channels
categorize on the **first run** — there is no deploy-time wait. The age gate only holds back
genuinely young channels.

**Hysteresis.** A tentative category must hold for
`ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES` (3) consecutive cycles before it commits.
`PendingCategory` and `ConsecutiveCategoryCyclesInNewState` carry that streak across restarts;
observing the committed category again clears the streak. This is what stops a channel oscillating
Sink ↔ Source and dragging the fee baseline with it.

### d. `TargetLocalRatio` — where we want it to sit

```
goal   = Uncategorized ? 0.5
                       : clamp( 0.5 + clamp(k·NetFlowRatio, ±maxDrift), 0.10, 0.90 )
target ← αT·goal + (1−αT)·target
```

with `k = ROUTING_ENGINE_TARGET_K` (0.70), `maxDrift = ROUTING_ENGINE_TARGET_MAX_DRIFT` (0.35),
`αT = ROUTING_ENGINE_TARGET_ALPHA` (0.10).

A sink is drained, so it should **hold more** local (target above 0.5); a source is refilled by its
peer, so it can hold **less** (target below 0.5). With the current defaults the drift clamp is the
binding one — `NetFlowRatio` maxes out at ±1, so the goal never leaves `[0.15, 0.85]` and the outer
`[0.10, 0.90]` clamp is a guard that only matters if `maxDrift` is raised above `0.40`.

**What each constant does.** `k` is the *sensitivity* — how many points of target you get per unit
of flow asymmetry. `maxDrift` is the *saturation* — the hardest the setpoint can ever be pushed off
centre. `αT` is the *slew rate* — how fast the committed target walks toward that goal.

The first two interact: at `k = 0.70` the `±0.35` cap binds once `|NetFlowRatio| >= 0.5`, so the
mapping is linear only up to that point and flat beyond it. Since categorization already needs
`|NetFlowRatio| >= 0.25`, the whole usable range of `k` is the band `0.25 → 0.50`, mapping target
`0.675 → 0.85`. Any channel more lopsided than 3:1 push-to-pull gets the same target as one at 10:1.

Time-scale separation matters here — a setpoint that moves as fast as the measurement would never
let either control loop settle. Note that `αT = 0.10` is *not* what provides it: as a smoothing
constant it is marginally faster than the ratio EMA's `0.08`. The
separation comes from the **input**: the target is driven by a 21-day flow window that barely moves
between cycles, while the ratio EMA tracks live balance. In practice a target takes ~17 hours to
walk from `0.5` to `0.675` after a channel is first classified as a sink.

## 3. Enabling it — three layers of gate

Nothing actuates unless **all** the applicable gates are open. They nest:

| Layer | Flag | Scope |
|---|---|---|
| **Global kill switch** | `ROUTING_ENGINE_ENABLED` (env, default `false`) | Checked first in all three jobs, before any DB or LND work. Off ⇒ the engine does not exist. |
| **Per node** | `Node.DynamicFeeManagementEnabled` | Fee actuator for this node. |
| | `Node.AutoRebalanceEnabled` | Rebalance actuator for this node. |
| | `Node.RoutingEngineDryRun` | Decide and log, but do not write. |
| | `Node.AllowPositiveInboundFees` | Permit inbound *surcharges*; otherwise inbound is discount-only. |
| | `Node.RebalanceBudgetSats` | **Required** — a node with no budget never rebalances. |
| **Per channel** | `Channel.IsDynamicFeeEnabled` | Opt this channel into fee management. |
| | `Channel.IsAutoRebalanceEnabled` | Opt this channel in as a rebalance **source**. |

All three per-channel/per-node booleans default to `false`, so a fresh deployment actuates nothing
until an operator opts in explicitly. Both are managed from
[`RoutingManagement.razor`](../src/Pages/RoutingManagement.razor).

### Recommended rollout

1. **Signal only.** Set `ROUTING_ENGINE_ENABLED=true` with every per-node flag off. The sensor
   fills in `ChannelRoutingState` and writes nothing to LND. Let it run long enough for the EMA and
   category to settle (a day or more).
2. **Dry-run one node.** Enable `DynamicFeeManagementEnabled` / `AutoRebalanceEnabled` **plus**
   `RoutingEngineDryRun` on a single canary node, and read the logs — every decision is logged with
   its reason whether or not it is applied.
3. **Go live on the canary.** Clear `RoutingEngineDryRun`.
4. **Widen** node by node.

> **Dry-run is not side-effect-free.** The fee job persists `ChannelFeeState.LastApplied*` in
> dry-run exactly as it does on a live write, and the rebalance job decrements its in-memory
> budget. This is deliberate — it makes a dry run trace the same trajectory a live run would — but
> it means the fee control loop's integrator advances while in dry-run, so the first live cycle
> resumes from where the simulation left off, not from the channel's actual fee. See
> [fee-engine-algorithm.md](fee-engine-algorithm.md) §7.

### Scheduling

Both actuators start `ROUTING_ENGINE_ACTUATOR_OFFSET_MINUTES` (5) after boot, while the sensor uses
`StartNow()`, so the control laws always act on freshly-written routing state rather than on
whatever survived the last shutdown.

All three jobs are `[DisallowConcurrentExecution]`, and each swallows its own exceptions — per
channel, per node, and per run — so one bad channel cannot take out a node's cycle and one bad node
cannot take out the run. The next cycle is the retry.

In `IS_DEV_ENVIRONMENT` every routing job runs at **1-minute** intervals and starts immediately.
`ROUTING_ENGINE_JOB_INTERVAL_SECONDS` overrides the cadence of all three (useful for tests).

## 4. Signal-layer constant reference

Defaults from [`Constants.cs`](../src/Helpers/Constants.cs); every one is env-overridable at
process start.

| Constant | Default | Effect |
|---|---|---|
| `ROUTING_ENGINE_ENABLED` | `false` | Global kill switch for all three jobs. |
| `ROUTING_ENGINE_JOB_INTERVAL_MINUTES` | `30` | Sensor and fee-actuator cadence. |
| `ROUTING_ENGINE_REBALANCE_JOB_INTERVAL_MINUTES` | `10` | Rebalance-actuator cadence. |
| `ROUTING_ENGINE_ACTUATOR_OFFSET_MINUTES` | `5` | Actuator start delay behind the sensor. |
| `ROUTING_ENGINE_JOB_INTERVAL_SECONDS` | *(unset)* | Overrides all three cadences, in seconds. |
| `ROUTING_ENGINE_FEE_EMA_ALPHA` | `0.08` | `EmaLocalRatio` smoothing. |
| `ROUTING_ENGINE_FLOW_WINDOW_DAYS` | `21` | Forwarding-history window for net flow. |
| `ROUTING_ENGINE_FLOW_MIN_MSAT` | `10_000_000_000` | Volume gate (10 M sats) for categorization. |
| `ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS` | `3024` | Age gate (~21 days). |
| `ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD` | `0.25` | Sink/Source threshold on `NetFlowRatio`. |
| `ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES` | `3` | Cycles a flip must hold before committing. |
| `ROUTING_ENGINE_TARGET_K` | `0.70` | Net-flow → target-drift gain. |
| `ROUTING_ENGINE_TARGET_MAX_DRIFT` | `0.35` | Cap on that drift. |
| `ROUTING_ENGINE_TARGET_ALPHA` | `0.10` | Target smoothing (deliberately slow). |

## 5. Reading the engine's behavior

- **Logs** — every decision logs its `Reason`, including no-ops, skips, and dry-runs. This is the
  primary diagnostic surface for both actuators.
- **[`RoutingManagement.razor`](../src/Pages/RoutingManagement.razor)** — per-node and per-channel
  flags, and the current signal.
- **[`Rebalances.razor`](../src/Pages/Rebalances.razor)** — rebalance history, status, fees paid.
- **`AuditActionType.RebalanceInitiated`** — every automated rebalance dispatch is audited with its
  amount, fee cap, reserved fee, and remaining budget.
- **DB** — `ChannelRoutingState` (signal) and `ChannelFeeState` (last applied fees) are both
  readable directly and are the ground truth for what the loops think the state is.
