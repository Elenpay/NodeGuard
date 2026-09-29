# Spark Swap Provider

## Context

NodeGuard uses swap-outs to move funds from Lightning channels back to the chain. This gives a node
inbound liquidity without closing channels. Closing a channel is slow and expensive, because it
needs an on-chain transaction confirmed in the Bitcoin chain, and reopening one later costs the same
again. Two providers exist today: Loop and 40swap.

Loop is the only provider working right now. It has had downtimes in the past, and when it is down
NodeGuard cannot swap out at all.

40swap is not running at the moment. We found some vulnerabilities in it that are still pending
fixes, and getting it back takes real work. Even in its usual setup, the operations team has to run
some steps of the swaps manually.

We looked at other swap-out providers:

- Boltz was a good option, but it suspended its swap service indefinitely in August 2026 after
  repeated attacks on its infrastructure, and there is no date for it to come back.

- PeerSwap has some challenges for us: the project is in beta and recommends using it only with
  small balances and swaps only happen between direct channel peers both running PeerSwap.

Spark is the suggested option since it does not have these challenges. Using Spark is also a
strategic move. We have other developments and integrations planned with them, so it makes sense to
start learning how to work with their stack now. Also they have a good liquidity and transactions
volume.

### How Loop and 40swap work in NodeGuard today

Both providers follow the same pattern. Next to each LND node runs a small helper program: loopd
for Loop and 40swapd for 40swap. The helper knows how to talk to that node's LND and to the
provider's server on the internet. NodeGuard never does the swap itself. It asks the helper for one
and then checks on it.

A swap-out works like this. The node pays sats over Lightning to the provider, and the provider
sends the same amount, minus fees, to a Bitcoin address we give it. The two payments are tied
together with an HTLC (a hash-locked contract), so the provider cannot keep the Lightning payment
without releasing the on-chain coins, and we cannot take the coins without paying. Nobody holds our
funds in the middle.

## Integration Challenges

The section above ends with the three things a provider must give NodeGuard: a helper to call, a
swap id, and a status check. Spark gives none of them. Spark is not a swap service but a wallet. To
move sats from a channel to the chain through it, NodeGuard needs its own Spark wallet and two
moves: pay a Lightning invoice into that wallet, then withdraw from that wallet to an on-chain
address. The Lightning side of Spark is handled by a Spark Service Provider (SSP), today Lightspark.
All the challenges below come from NodeGuard becoming a wallet.

- Keys. A wallet signs with private keys, and Spark needs them inside the process every time funds
  move, because each move is co-signed with Spark's operators, the companies that run the Spark
  network's servers (Lightspark, Flashnet and Breez). Today NodeGuard never signs by itself: it
  sends PSBTs to the remote signer, and funds move with FinanceManager approval. Spark allows a
  custom signer, but it would have to speak Spark's signing protocol, which our remote signer does
  not.

- Custody. With Loop and 40swap, nobody holds our funds in the middle: the sats go from our channel
  to our on-chain address in one swap. With Spark, the sats stop in the Spark wallet between the two
  moves. While they are there, they are locked with two keys, ours and the operators', so we cannot
  move them unless the operators sign too. If the operators go offline or refuse, the only way out
  is an exit without their help, which is slow and expensive: in one documented mainnet case it took
  about 10 days, and about 10% of a 100k sat balance went to fees.

- No ready-made helper. Loop and 40swap come with a helper program that NodeGuard calls. Spark does
  not. Spark gives code libraries instead, and only for JavaScript and Rust, not for C#, which is
  what NodeGuard is written in. There are two ways around this. One is a C# library made by Breez, a
  third party. It works, but it needs an account with Breez, it runs inside the NodeGuard process,
  and it stores the wallet in its own database. The other is to build our own helper program in
  JavaScript or Rust and run it next to each node, the same way loopd runs today. Either way it is a
  new piece to build and maintain.

- More steps to follow. Today a swap is simple for NodeGuard: it asks the helper once, gets a swap
  id back, and later keeps asking "is it done?" until the answer is done or failed. With Spark,
  NodeGuard has to do the work itself, in order: create an invoice in the Spark wallet, pay that
  invoice from the node, wait until the Spark wallet has received the sats, ask Spark what the
  withdrawal will cost (the price is only valid for a short time), send the withdrawal, and wait for
  it to confirm on-chain. Any of these can fail on its own, so NodeGuard has to remember at which
  point each swap is. Spark also does not answer "is it done?" questions. It tells the wallet when
  something happens, so the wallet has to stay connected all the time. Both the swap record in the
  database and the job that watches swaps would need to handle these extra steps.

- Local testing. Today we can test a whole swap on a laptop. Our dev setup runs a private Bitcoin
  test network (regtest) with a few Lightning nodes, and next to them a copy of the Loop server and
  of the 40swap server, so the nodes can pay them and get coins back. Spark offers a test network,
  but it runs on their servers, on a test network of their own, so our local nodes cannot pay into
  it. We could run Spark's operator servers on our laptop, but that is not enough: the Lightning
  payment into Spark goes through a Spark Service Provider, a separate server with its own Lightning
  node, and Lightspark does not publish theirs for others to run.

- Limits. Automatic swaps are sized from the node's balance and its limits, and they can be large.
  Neither Spark nor Lightspark publish a maximum per Lightning receive or per withdrawal, so we have
  to confirm it with them.

### Decisions to take

Each challenge above needs a decision before we start building. The list follows the same order.

1. Where the Spark keys live. Inside NodeGuard as a hot wallet, in a separate helper program, or in
   the remote signer after teaching it Spark's signing protocol.

2. How much we accept to leave with Spark. A rule for how long funds may stay in the Spark wallet
   (for example, withdraw right after each payment arrives), a cap on the amount in flight, and who
   approves it.

3. Which helper to use. The C# library from Breez inside NodeGuard, or our own helper program in
   JavaScript or Rust next to each node. Also whether all nodes share one Spark wallet or each node
   gets its own.

4. How to track a swap. The new states a swap goes through, whether the job that watches swaps keeps
   asking or listens to Spark's events, and what to do when a swap stops halfway.

5. How to test it. Run a Spark Service Provider ourselves for local tests, connect one of our test
   nodes to Spark's test network, or test only on mainnet with small amounts.

6. What limits to set. Ask Lightspark for the maximum per payment and per withdrawal and for the
   exact fees, then set the node's swap limits from that.

## How the integration of a PoC could look

The scope is a proof of concept: one manual swap-out through Spark, from the Swaps page, on one
node, with a small amount, completed end to end. Everything that is not needed for that is left out.
The idea is still to make Spark look like Loop and 40swap from NodeGuard's point of view, with a
helper program next to the node, so that the Spark keys and wallet stay out of the NodeGuard process
and the later full integration can build on the same pieces.

### The Spark helper

A small program, built with Spark's JavaScript library, that holds one Spark wallet and has access
to one node's LND. It exposes two calls over gRPC, the same two NodeGuard already uses with the
other helpers: start a swap-out and get the status of a swap. It keeps the state of each swap in
memory. When NodeGuard asks for a swap-out with an amount and an address, the helper:

1. Creates a Lightning invoice for the amount in its Spark wallet.

2. Pays that invoice from the node's LND.

3. Waits until the Spark wallet has received the sats.

4. Asks Spark for a withdrawal price and sends the withdrawal to the address NodeGuard gave.

5. Watches until the withdrawal is confirmed, then marks the swap as completed.

The status call returns pending, completed or failed, plus the current step and any error. If
something fails after the invoice is paid, the sats stay in the Spark wallet and we finish the
withdrawal by hand. That is acceptable for a proof of concept with small amounts.

### Changes in NodeGuard

- A new value, Spark, in the list of swap providers.

- A new field on the node with the address of its Spark helper.

- A new service that talks to the helper, added behind SwapsService for start and status.

- The Swaps page offers Spark as a provider for manual swaps.

- The monitor job includes nodes that have a Spark helper, so it picks up the final status.

### Keeping funds safe

Manual swaps only, on one node, with a small amount each time and one swap at a time. The wallet
seed is given to the helper at start from a secret store, never written in a config file. The helper
withdraws as soon as the sats arrive and does not keep a balance on purpose.

### Testing

The helper is first tried on its own against Spark's test network to check the wallet side: receive,
withdraw and status. Then the full flow runs on mainnet, by hand, from the Swaps page, with a small
amount. The proof of concept is done when one swap-out started from NodeGuard ends with the coins
confirmed in the node's destination wallet.
