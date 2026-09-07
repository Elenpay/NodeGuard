# Fee Engine Algorithm

Guide to the routing engine's **dynamic fee** decision logic:
[`FeeOptimizerService.ComputeNextPolicy`](../src/Services/FeeOptimizerService.cs), driven by
[`ChannelFeeOptimizerJob`](../src/Jobs/ChannelFeeOptimizerJob.cs).

Read [routing-engine.md](routing-engine.md) first for the signal layer (`EmaLocalRatio`,
`TargetLocalRatio`, `PeerFlowCategory`) that this algorithm consumes. Companion:
[rebalance-algorithm.md](rebalance-algorithm.md).

All constants below are the current defaults from [`Constants.cs`](../src/Helpers/Constants.cs) and
are env-overridable (`ROUTING_ENGINE_FEE_*`).

---

## 1. What it does

For each managed channel, the engine steers the channel's **local-balance ratio** toward its
per-channel **target** by adjusting two fees:

- **Outbound ppm** — what we charge others to route *out through* this channel.
- **Inbound ppm** — what we charge for HTLCs *arriving on* this channel. LND allows this to be
  **negative** (a discount), which *attracts* inbound traffic.

It is a closed feedback loop: measure how far the channel is from where we want it, nudge both fees
to correct it, repeat every ~30 minutes.

`ComputeNextPolicy` is **pure** — no I/O, no clock, no DB, no injected state — so it is a static
function library rather than a DI-registered service. The job around it handles eligibility,
dry-run, persistence, and safety (§7).

## 2. Inputs

| Input | Source | Meaning |
|---|---|---|
| `emaLocalRatio` | `ChannelRoutingState` | EMA-smoothed `local / (local + remote)`. Already smoothed by the sensor (α = 0.08), so the optimizer reads a stable signal and does **not** smooth again. |
| `targetLocalRatio` | `ChannelRoutingState` | Where we want that ratio. Drifts with observed peer flow; clamped `[0.10, 0.90]`. |
| `category` | `ChannelRoutingState` | `Sink` / `Source` / `Bidirectional` / `Uncategorized` — selects the baseline `p₀`. |
| `lastOutboundPpm` | `ChannelFeeState` | Last outbound ppm **we** applied; `null` on first evaluation. |
| `lastInboundPpm` | `ChannelFeeState` | Last inbound ppm **we** applied; `null` on first evaluation. |
| `allowPositiveInboundFees` | `Node` flag | When false, inbound is collapsed to `≤ 0` (discount-only). |
| `tunables` | `Constants.cs` | The control-law constants (§8). |

The loop's memory is `ChannelFeeState`, not LND. The engine steers off *its own* last-applied
value, so an operator's manual fee change between cycles is not treated as the loop's own output.

## 3. The control variable: deviation `d`

```
d = emaLocalRatio − targetLocalRatio
```

| `d` | Meaning | Corrective intent |
|---|---|---|
| `d > 0` | **too local** — we hold more than target | **drain** it: cheaper outbound (invite outflow) + positive inbound (repel inflow) |
| `d < 0` | **too remote** — we hold less than target | **fill** it: dearer outbound (protect scarce local) + negative inbound (invite inflow) |
| `d ≈ 0` | on target | do nothing |

`|d|` is a fraction of the channel, so `0.10` = "10 percentage points off target".

## 4. The operating point: category baseline `p₀`

`p₀` is the channel's "natural" outbound fee for its peer type. It does two things: it **scales the
whole response** (every step is proportional to `p₀`) and it **seeds the fee** on the very first
evaluation.

| Category | `p₀` (ppm) | Why |
|---|---|---|
| **Source** | `50` | Peer feeds *us* liquidity — keep cheap so the flow keeps coming. |
| **Bidirectional** | `1500` | Balanced peer — mid fee. |
| **Sink** | `2500` | Peer *drains* us — outbound liquidity here is scarce and valuable, charge a lot. |
| **Uncategorized** | `1500` | No signal yet — same as bidirectional. |

Because steps scale with `p₀`, a Source channel moves in ~1 ppm increments while a Sink moves in
tens. **Get the three baselines right before touching the gains** — they set the fee tier, the
gains only set how fast it converges.

## 5. Decision flow

```
p₀    = baseline(category)
pLast = lastOutboundPpm ?? p₀       # first eval seeds from the category baseline
iLast = lastInboundPpm  ?? 0
d     = emaLocalRatio − targetLocalRatio

┌─ |d| ≤ FeeDeadband (0.03) ────────────────► NoOp
└─ otherwise ───────────────────────────────► compute outbound + inbound (§6)
                                                └─ neither moved ≥ MinDeltaPpm ─► NoOp
                                                └─ otherwise ──────────────────► Update
```

Inside the deadband the channel is close enough — reacting would churn fees on noise. Outside it the
engine always acts, however far off-target the channel is; the per-cycle step clamps bound how fast
it can move.

## 6. The control law

Both fees use **integral** control: each cycle the applied value is nudged off its *previous* value
by `gain · d · p₀`. While an error persists the fee keeps stepping in the correcting direction until
the error closes or it hits a rail. This drives steady-state error toward zero instead of leaving a
fixed offset — and it means there is **no fixed fee that corresponds to "on target"**; the resting
fee is path-dependent.

### Outbound

```
pNew     = clamp( pLast − OutboundIntegralGain·d·p₀,  MinOutboundPpm, MaxOutboundPpm )   # 0 … 3000
Δp       = clamp( pNew − pLast,  −MaxStepPpm, +MaxStepPpm )                              # ±50 / cycle
outbound = |Δp| < MinDeltaPpm ? pLast : pLast + Δp                                       # skip <5 ppm moves
```

- `d < 0` (too remote): `−gain·d·p₀ > 0` → outbound **rises**, protecting scarce local balance.
- `d > 0` (too local): `−gain·d·p₀ < 0` → outbound **falls**, inviting outflow.

### Inbound

Same integral form, sign flipped — inbound moves opposite to outbound for a given deviation:

```
iRaw     = iLast + InboundIntegralGain·d·p₀
if !allowPositiveInboundFees:  iRaw = min(iRaw, 0)                                  # discount-only nodes
iNew     = clamp( iRaw,  MinInboundPpm, MaxInboundPpm )                             # −2000 … +1000
Δi       = clamp( iNew − iLast,  −MaxInboundStepPpm, +MaxInboundStepPpm )            # ±50 / cycle
inbound  = |Δi| < MinDeltaPpm ? iLast : iLast + Δi
```

- `d < 0` (too remote): nudge `< 0` → inbound **deepens negative** (discount, to attract inflow).
- `d > 0` (too local): nudge `> 0` → inbound **rises toward positive** (surcharge, to repel entry).

The `allowPositiveInboundFees` collapse is applied to the raw value **before** the rail clamp, so a
discount-only node can only ever occupy the negative half of the lever.

All rounding is `MidpointRounding.AwayFromZero`.

### The rails are the anti-windup

`[MinOutboundPpm, MaxOutboundPpm]` and `[MinInboundPpm, MaxInboundPpm]` clamp the *state*, not just
the output, so the integrator can never wind up beyond a rail and then take many cycles to unwind.

**A channel held off-target long enough will reach its rail and sit there.** Nothing in the law
notices this. With the current defaults outbound has only **500 ppm of headroom above the Sink
baseline** (2500 → 3000), i.e. ten cycles at the step clamp, so a persistently-drained sink pins at
`MaxOutboundPpm` within about five hours. Inbound has much more room (0 → −2000, forty cycles). This
is the limitation §9 addresses.

### Final gate

If neither fee actually changed — both computed moves fell under `MinDeltaPpm` — the result is
`NoOp`. Otherwise it is an `Update` carrying the new `(outbound, inbound)` pair. A `NoOp` still
records the observed ratio and target, but costs **no LND round-trip**.

## 7. Where it sits in the system — the job

`ComputeNextPolicy` is only the math.
[`ChannelFeeOptimizerJob`](../src/Jobs/ChannelFeeOptimizerJob.cs) wraps it each cycle:

**Gates.** `ROUTING_ENGINE_ENABLED` is checked before any work. Nodes must be managed, not
disabled, and have `DynamicFeeManagementEnabled`.

**Channel eligibility** — one `GetOpenChannels` query per run, filtered globally then snapshotted
per node:

| Requirement | Why |
|---|---|
| Channel is open and known to NodeGuard | Nothing to actuate otherwise |
| `Channel.IsDynamicFeeEnabled` | Per-channel opt-in |
| `SatsAmount >= ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS` (10 M) | Fee moves on tiny channels aren't worth the write |
| **Not** a source of a `Pending`/`InFlight` rebalance | Authority split — see below |
| Has a `ChannelRoutingState` row for this node | No signal ⇒ no decision |

**Authority split with the rebalancer.** A channel currently being drained by a rebalance is
excluded outright (`GetPendingInFlightSourceChannelIds`). Its balance is mid-flight, so the EMA is
measuring a transient the fee loop would integrate against.

**Order of writes.** On `Update` the job re-reads the live policy (`GetChannelFeePolicy`) purely to
carry forward the values the engine does **not** own — base fee, timelock, and inbound base msat.
The engine only ever modulates the two ppm rates. If that read fails the channel is skipped for the
cycle.

**Dry-run.** With `Node.RoutingEngineDryRun` the job logs the decision and **still persists
`LastApplied*` and `LastFeeUpdateAt`**. This is deliberate — the simulated run follows the same
trajectory a live one would — but it means:

> The integrator advances during dry-run. When you clear the flag, the loop resumes from the
> simulated operating point, not from the channel's actual on-chain fee. The first live write can
> therefore be a large jump relative to what LND currently has. Expect it.

**Failure handling.** A failed LND write is logged and the channel is skipped — and critically, the
fee state is **not** persisted, so the integrator does not advance on a write that didn't land. The
next cycle retries from the same operating point.

**Enable/disable lifecycle.** [`FeeEngineStateService`](../src/Services/FeeEngineStateService.cs)
**purges** `ChannelFeeState` when a channel opts out, or when its node is disabled or has fee
management switched off. A later re-enable therefore cold-starts from the category baseline rather
than resuming from a stale operating point.

> **There is no restore-on-disable.** Purging drops the engine's *memory*, but the last fees the
> engine wrote stay live on LND. Turning the engine off leaves the channel priced wherever the loop
> last left it; an operator who wants the original policy back must set it manually.

**No prioritization, cap, or throttle.** Every eligible channel is evaluated every cycle, in
`ListChannels` order, and every `Update` is written immediately. There is no per-run update limit
and no minimum interval between writes to the same channel. Churn is bounded by the deadband, the
step clamps, and the min-delta dead-zone rather than by rate limiting — so a node with many
off-target channels issues many `UpdateChannelPolicy` calls per cycle.

## 8. Constant reference (current defaults)

**Gains**

| Constant | Default | Role |
|---|---|---|
| `ROUTING_ENGINE_FEE_OUTBOUND_INTEGRAL_GAIN` | `0.8` | Outbound per-cycle integral gain. |
| `ROUTING_ENGINE_FEE_INBOUND_INTEGRAL_GAIN` | `0.5` | Inbound per-cycle integral gain. |

**Band — when the engine acts**

| Constant | Default | Role |
|---|---|---|
| `ROUTING_ENGINE_FEE_DEADBAND` | `0.03` | `abs(d)` at or below this → no-op. |

**Rate / churn limiters**

| Constant | Default | Role |
|---|---|---|
| `ROUTING_ENGINE_FEE_MAX_STEP_PPM` | `50` | Max outbound change per cycle. |
| `ROUTING_ENGINE_FEE_MAX_INBOUND_STEP_PPM` | `50` | Max inbound change per cycle. |
| `ROUTING_ENGINE_FEE_MIN_DELTA_PPM` | `5` | Moves smaller than this are skipped entirely. |

**Hard limits (also the anti-windup rails)**

| Constant | Default | Role |
|---|---|---|
| `ROUTING_ENGINE_FEE_MIN_OUTBOUND_PPM` | `0` | Outbound floor (LND won't take negative). |
| `ROUTING_ENGINE_FEE_MAX_OUTBOUND_PPM` | `3000` | Outbound ceiling. |
| `ROUTING_ENGINE_FEE_MIN_INBOUND_PPM` | `−2000` | Deepest inbound discount. |
| `ROUTING_ENGINE_FEE_MAX_INBOUND_PPM` | `1000` | Highest inbound surcharge. |

**Category baselines (`p₀`)**

| Constant | Default |
|---|---|
| `ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE` | `50` |
| `ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL` | `1500` |
| `ROUTING_ENGINE_FEE_BASELINE_PPM_SINK` | `2500` |
| `ROUTING_ENGINE_FEE_BASELINE_PPM_UNCATEGORIZED` | `1500` |

**Job-level (not used by the pure law)**

| Constant | Default | Role |
|---|---|---|
| `ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS` | `10_000_000` | Skip channels below this capacity. |
| `ROUTING_ENGINE_JOB_INTERVAL_MINUTES` | `30` | Cycle cadence (1 min in dev). |

Per-node flags on [`Node`](../src/Data/Models/Node.cs): `DynamicFeeManagementEnabled`,
`AllowPositiveInboundFees`, `RoutingEngineDryRun`. Per-channel:
[`Channel.IsDynamicFeeEnabled`](../src/Data/Models/Channel.cs).

## 9. Worked examples

All use the current defaults.

### A. Sink run dry — too remote

Sink (`p₀ = 2500`), `ema = 0.40`, `target = 0.50` → `d = −0.10`. First evaluation, so
`pLast = 2500` and `iLast = 0`. Positive inbound allowed.

| Leg | Computation | Result |
|---|---|---|
| Outbound | `2500 − 0.8·(−0.10)·2500 = 2700`; `Δp = clamp(+200, ±50) = +50` | **2550 ppm** — raise, protect scarce local |
| Inbound | `0 + 0.5·(−0.10)·2500 = −125`; `Δi = clamp(−125, ±50) = −50` | **−50 ppm** — discount, pull inflow |

While `d` stays negative both keep stepping every cycle (+50 outbound, −50 inbound). Outbound pins
at 3000 after ten cycles; inbound continues deepening toward −2000.

### B. Sink overfull — too local

Same sink, `ema = 0.60`, `target = 0.50` → `d = +0.10`.

| Leg | Computation | Result |
|---|---|---|
| Outbound | `2500 − 0.8·(0.10)·2500 = 2300`; `Δp = clamp(−200, ±50) = −50` | **2450 ppm** — cheaper, invite outflow |
| Inbound | `0 + 0.5·(0.10)·2500 = +125`; `Δi = +50` | **+50 ppm** — surcharge, repel inflow |

### C. Positive inbound disallowed

Example B with `allowPositiveInboundFees = false`: `iRaw = min(+125, 0) = 0`, so `Δi = 0` and
inbound stays **0**. Outbound still drops to **2450**, so the decision is still an `Update`.

### D. Below min-delta — no-op

Source (`p₀ = 50`), `ema = 0.54`, `target = 0.50` → `d = +0.04`, just outside the deadband.

| Leg | Computation | Result |
|---|---|---|
| Outbound | `50 − 0.8·0.04·50 = 48.4 → 48`; `Δp = −2`, and `abs(−2) < 5` | keep **50** |
| Inbound | `0 + 0.5·0.04·50 = 1`; `Δi = +1`, and `abs(1) < 5` | keep **0** |

Nothing moved → **NoOp**. Small channels barely move fees; the dead-zone avoids pointless writes.

## 10. Tuning

- **Baselines first, gains second.** `p₀` sets the fee tier and scales every step. Gains only set
  the ramp speed toward a resting level that the law itself does not define.
- **More aggressive:** raise the integral gains (faster ramp, more overshoot risk) or the step
  clamps (bigger jumps per cycle).
- **Calmer:** widen `FeeDeadband` (ignore more), lower the gains, or raise `MinDeltaPpm` (fewer,
  larger writes).
- **Give outbound room.** `MaxOutboundPpm = 3000` against a Sink baseline of 2500 leaves very
  little headroom. If sinks are pinning at the ceiling, raise the ceiling before raising the gain.
- **Watch for rails.** A channel sitting at `MaxOutboundPpm` or `MinInboundPpm` is telling you the
  loop has run out of authority — the fee lever alone cannot fix that channel.