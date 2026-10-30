# Auto channel open

Auto channel open proposes a new channel to a peer when two things are true: the route to that peer has been earning us fees, and we have recently been turning payments to it away for lack of outbound liquidity. The channel always has the same size, set per node.

[`AutoChannelOpenJob`](../src/Jobs/AutoChannelOpenJob.cs) runs every 30 minutes (every minute in a dev environment) as part of the routing engine (see [routing-engine.md](routing-engine.md)). It writes recommendations, which an operator promotes from the Routing Management page. In **Auto** mode the job promotes them itself. Either way a promotion only creates a channel open request: funding still goes through the usual approval and signing flow.

## The rule

The job looks at every peer we have an open channel with that refused at least one forward in the shortage window, and recommends a channel when all of the following hold:

| # | Check | Default |
|---|---|---|
| 1 | **The route works.** Fees earned on settled forwards out to the peer over the last `AUTO_CHANNEL_OPEN_LOOKBACK_DAYS` are at least `AUTO_CHANNEL_OPEN_MIN_EARNED_FEE_SATS`. | 7 days, 50,000 sats |
| 2 | **We were short recently.** Forwards to the peer refused with `INSUFFICIENT_BALANCE` over the last `AUTO_CHANNEL_OPEN_SHORTAGE_HOURS` number at least `AUTO_CHANNEL_OPEN_MIN_REFUSALS`. | 6 h, 5 refusals |
| 3 | **We are still short now.** Usable outbound to the peer (local balance minus the channel reserve) is below `AUTO_CHANNEL_OPEN_LOW_OUTBOUND_PCT` of the capacity we have with it. | 20% |
| 4 | **Not on cooldown.** No recommendation to the peer was promoted or dismissed in the last `AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS`. | 12 h |
| 5 | **The channel has a price.** LND reports an outbound fee policy for at least one channel to the peer. | |
| 6 | **The node can pay for it.** The node's channel size fits both the funding wallet and the remaining budget. | |

The size is never adjusted: a peer whose channel doesn't fit the wallet or the budget is skipped, not given a smaller channel. Qualifying peers are taken highest earned fee first, at most `AUTO_CHANNEL_OPEN_MAX_PLANS_PER_RUN` (5) per node per run. Every evaluated peer that doesn't qualify is logged with the reason.

**Peers, not channels.** All the evidence is summed over our open channels to the peer. LND forwards non-strictly: it may send an HTLC over any of our channels to the next hop, so a peer's fees and refusals are spread over its channels and only the total means anything. Evidence recorded on a channel that has since closed is dropped, since nothing maps it back to the peer.

**Where the numbers come from.**
- Earned fees, and the amount routed shown next to them, come from the forwards that [`NodeHtlcSubscribeJob`](../src/Jobs/NodeHtlcSubscribeJob.cs) records in `ForwardingHtlcEvents`.
- Refusals are the forwards that failed at our own link (`LinkFailEvent`) with `INSUFFICIENT_BALANCE`. That is the only failure a new channel fixes: `HTLC_EXCEEDS_MAX` is our own ceiling, and a failure further down the route says nothing about our balance.
- Outbound and capacity come from LND's `ListChannels` at the time of the run. Check 3 matters because the refusals are history: a rebalance or a payment the other way may have refilled the peer since.
- What the wallet can fund is computed from the wallet's spendable coins (locked and frozen ones excluded) at mempool's hour fee rate, leaving room for the funding fee and a change output.

## Modes and promotion

Each node runs in one of two modes:
- **Recommendation**: the job only writes recommendations. They show on the Routing Management page, where an operator creates the request or dismisses the recommendation.
- **Auto**: the job also promotes each recommendation itself, at the node's channel size. With the node in dry run, recommendations are still written but never promoted.

Promoting creates a `ChannelOperationRequest` to open a public channel to the peer from the node's funding wallet, at mempool's economy fee rate. From the panel the operator can change the capacity first. The request's description summarises the evidence, so an approver sees why it exists. Then:
- With a **hot wallet** that needs no human signature, the template PSBT is built and `ChannelOpenJob` is scheduled straight away. If the template can't be built (no spendable coins), the request fails.
- Otherwise the request stays **Pending** for the wallet's signers, like any other channel open.

**The fee the channel opens at.** A new channel arrives with all its balance on our side, so at the node's default fee it would become the cheapest route to a peer we already know is in demand. It opens instead at the highest fee rate we currently charge on our channels to that peer, with the base fee of that same channel. The policy is captured with the evidence on every run, and the channel opens at the one captured last, without asking LND again at promotion.

## Lifecycle

A recommendation is one row per node and peer, recycled from one run to the next. Its status:

| Status | Meaning |
|---|---|
| **Open** | The peer qualifies on this run's evidence. |
| **Promoted** | A channel open request was created from it. |
| **Dismissed** | An operator dismissed it from the panel. |
| **Expired** | It was open and the peer no longer qualifies. |
| **Failed** | Its request ended Failed or Cancelled, so no channel was ever opened. |

Every run first marks all open rows expired, then reopens the ones whose peer still qualifies, with fresh evidence. The panel therefore always shows the latest run, and a recommendation disappears as soon as its peer stops qualifying, for example once the refusals are older than the shortage window.

**Cooldown.** Promoting or dismissing a recommendation suppresses its peer for `AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS`. A promotion whose request was rejected counts as a decision and keeps the cooldown. One whose request failed or was cancelled is marked Failed and releases the peer at once, since the peer never got its channel. A promoted row whose request hasn't finished yet is never reopened, whatever the evidence.

## Budget

Each node has a channel open budget: the most it may commit to new channels per refresh period. The period is set per node, by default `AUTO_CHANNEL_OPEN_DEFAULT_BUDGET_REFRESH_HOURS` (30 days), and starts again once it has elapsed.

What counts as committed is the capacity of every channel open request from the node created in the current period, except those cancelled, rejected or failed. That includes opens created by hand, not only the automatic ones. Within a run, every promotion is deducted before the next plan is checked.

## Settings

**Environment** ([`Constants.cs`](../src/Helpers/Constants.cs)):

| Variable | Default | Meaning |
|---|---|---|
| `AUTO_CHANNEL_OPEN_ENABLED` | `false` | Global switch. `ROUTING_ENGINE_ENABLED` must be on too. |
| `AUTO_CHANNEL_OPEN_LOOKBACK_DAYS` | 7 | Window for earned fees. |
| `AUTO_CHANNEL_OPEN_MIN_EARNED_FEE_SATS` | 50,000 | Minimum fees earned to the peer in that window. |
| `AUTO_CHANNEL_OPEN_SHORTAGE_HOURS` | 6 | Window for refused forwards. |
| `AUTO_CHANNEL_OPEN_MIN_REFUSALS` | 5 | Minimum refused forwards in that window. |
| `AUTO_CHANNEL_OPEN_LOW_OUTBOUND_PCT` | 20 | Usable outbound must be below this share of the capacity with the peer. |
| `AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS` | 12 | Cooldown after a promotion or dismissal. |
| `AUTO_CHANNEL_OPEN_MAX_PLANS_PER_RUN` | 5 | Most recommendations per node per run. |
| `AUTO_CHANNEL_OPEN_DEFAULT_BUDGET_REFRESH_HOURS` | 720 | Budget period when the node sets none. |
| `AUTO_CHANNEL_OPEN_JOB_INTERVAL_MINUTES` | 30 | Job interval; 1 in a dev environment. |

**Per node**, on the Nodes page (liquidity management ▸ Automatic Channel Opening):
- **Enable automatic channel opening** and the **mode**.
- **Funding wallet**: the wallet the channels are paid from. Required.
- **Channel size**: the capacity of every recommended channel. Required, at least `MINIMUM_CHANNEL_CAPACITY_SATS`.
- **Budget** and **budget refresh interval**. A budget is required.
- **Dry run**, shared with the fee engine and the rebalancer.

A node without a funding wallet, channel size or budget is skipped, and the job logs why. The Routing Management page shows the size, the budget and what the current period has committed next to the recommendations, and warns when the feature is globally off or the node has no funding wallet.

## Known limitations

- **Refusals are raw HTLCs.** A sender retrying the same payment counts once per attempt, so a single persistent sender can reach the refusal minimum alone.
- **Evidence is only recorded while NodeGuard is running.** LND's HTLC event stream has no replay: forwards and refusals that happen while NodeGuard is down or restarting are never seen.
- **Earned fees are gross.** The rule doesn't weigh them against the on-chain cost of opening and closing the channel, or the cost of the capital locked in it.
- **One size for every peer.** The channel doesn't grow with demand. A peer that needs more gets another recommendation once the cooldown has passed and it is short again.
- **The budget is checked per recommendation.** In Recommendation mode several open recommendations may together exceed the remaining budget, and promoting from the panel doesn't check the budget again. The next run sees what was committed.
