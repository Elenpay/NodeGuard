/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Grpc.Core;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end regression for coin selection when the list of outpoints to exclude is longer than an
/// HTTP request line: GetAvailableUtxos must keep returning the wallet's real UTXOs, and must keep
/// hiding every excluded one, no matter how many exclusions there are.
///
/// What this guards against. NodeGuard asks NBXplorer's custom <c>selectutxos</c> endpoint to skip
/// the locked, frozen and dust outpoints of the wallet. Those used to travel as one repeated
/// <c>&amp;ignoreOutpoint=&lt;txid&gt;-&lt;n&gt;</c> query parameter each — about 82 bytes apiece — on the
/// request line, and NBXplorer leaves Kestrel's MaxRequestLineSize at its 8KB default. Past roughly
/// 98 exclusions Kestrel rejected the request before it ever reached MVC routing and answered
/// <b>HTTP 414</b> with an empty body. GetAvailableUtxos catches every backend failure and degrades
/// to <c>new UTXOChanges()</c> (src/Rpc/NodeGuardService.cs), so the RPC still returned <c>OK</c> —
/// with an <b>empty</b> confirmed list for a wallet holding 20 BTC. Nothing threw, nothing logged an
/// error to the caller, and unlike the Blazor path there is no fallback to the plain UTXO listing.
/// Silent, total loss of coin selection for any wallet with enough excluded outpoints.
///
/// The fix moves the list off the request line for good: NBXplorerService POSTs the exclusions as a
/// JSON body on every call, whatever their number, and the NBXplorer controller accepts them there
/// as well as on the query string. So this test needs an NBXplorer build that serves the POST
/// <c>selectutxos</c> route — and so does every other caller, which is why a GET-only backend is a
/// deployment problem rather than a quirk of this test. It is a precondition, not an assertion about
/// NodeGuard: against an older image the POST is refused, the failure is swallowed the same way, and
/// every assertion below fails identically whether or not NodeGuard is behaving — which is why the
/// route is probed first and reported by name.
///
/// Why FROZEN outpoints rather than dust drive the list. Dust is now excluded by value
/// (<c>minimumValue</c>), so a dust-driven test would stay green even if the exclusion list were
/// silently dropped on the floor — the backend would hide the dust anyway. Frozen UTXOs worth more
/// than the dust floor have no other reason to be missing: their absence from the response is only
/// explicable if the list actually reached the server. That also keeps the test honest if the
/// gRPC path ever stops enumerating dust outpoints individually, the way the Blazor path already did.
///
/// Exercised against a LIVE NodeGuard instance + bitcoind; shared plumbing in <see cref="E2ETestBase"/>.
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   NBXPLORER_URI            default http://localhost:32838 — probed for the POST selectutxos route
///   E2E_HOT_WALLET_ID        the hot wallet the other e2e classes share (default 3) — avoided here
///   E2E_LARGE_IGNORE_LIST_WALLET_ID  the hot wallet this test pollutes (default 4)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class LargeIgnoreListCoinSelectionE2ETests : E2ETestBase
{
    /// <summary>
    /// How many outpoints to push into the wallet's exclusion list. The 414 threshold is ~98
    /// (see <see cref="KestrelMaxRequestLineSize"/>); 130 clears it by a third, which absorbs
    /// multi-digit output indices and any outpoint the fixture fails to place. The funding
    /// transaction is ~4.2 kvB, far below the 100 kvB standardness cap.
    /// </summary>
    private const int FrozenUtxoCount = 130;

    /// <summary>
    /// Value of each frozen output. It must sit ABOVE Constants.MINIMUM_UTXO_VALUE_SATS (546), so
    /// that the backend's minimumValue filter is not what hides these UTXOs — only the exclusion
    /// list can be, which is the whole point of the assertions below. It must also clear bitcoind's
    /// P2WPKH dust relay threshold (294 sats at the default minrelaytxfee) or the funding
    /// transaction would be rejected as non-standard.
    /// </summary>
    private const long FrozenUtxoValueSats = 1_000;

    /// <summary>
    /// Values of the outputs the same funding transaction leaves UNFROZEN. DbInitializer funds this
    /// wallet exactly once, with a single 20 BTC UTXO, so without these the post-freeze selection is
    /// one element long and everything said about it holds trivially: a one-element list is both
    /// ascending and descending, and limit=1 cannot be told apart from no limit at all. Three values
    /// distinct from each other, from <see cref="FrozenUtxoValueSats"/> and from the seeded 20 BTC
    /// make the ordering observable and give the limit something to discard.
    /// </summary>
    private static readonly long[] UnfrozenUtxoValuesSats = [250_000, 500_000, 750_000];

    // The custom NBXplorer selectutxos backend behind GetAvailableUtxos picks UTXOs toward a target
    // amount (amount=0 always yields an empty selection), so every call must request one.
    private const long ProbeAmountSats = 2_000_000;

    /// <summary>Kestrel's default request line budget, which NBXplorer does not raise.</summary>
    private const int KestrelMaxRequestLineSize = 8192;

    // Fee for the funding transaction, as a flat rate over its virtual size plus an allowance for the
    // signature the unsigned skeleton does not carry yet. Both are deliberately generous: the
    // transaction is ~4.2 kvB, only bitcoind's own change pays for it, and the alternative to
    // overpaying on regtest is a batch that never relays.
    private const long FundingFeeRateSatsPerVByte = 5;
    private const int SignedInputVSizeAllowance = 150;

    private const string IgnoreOutpointParam = "&ignoreOutpoint=";

    // Constants.IsManuallyFrozenTag / the value CoinSelectionService.GetFrozenUTXOs looks for.
    private const string ManuallyFrozenTagKey = "manually_frozen";

    public LargeIgnoreListCoinSelectionE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task GetAvailableUtxos_WithAnIgnoreListTooLongForTheRequestLine_StillSelects()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        // 0. Harness precondition, established before anything expensive runs. GetAvailableUtxos
        //    swallows every backend failure into an empty UTXOChanges, so against an NBXplorer that
        //    predates the POST route this whole test degrades to "the selection is empty" — the same
        //    red a genuine regression shows, with none of the diagnosis.
        await AssertSelectUtxosAcceptsPostAsync();

        var walletId = int.Parse(Env("E2E_LARGE_IGNORE_LIST_WALLET_ID", "4"));
        walletId.Should().NotBe(int.Parse(Env("E2E_HOT_WALLET_ID", "3")),
            "this test permanently freezes ~130 UTXOs into the wallet it targets, so it must not run against " +
            "the hot wallet the rest of the e2e suite withdraws from and opens channels with; DbInitializer " +
            "seeds a fourth wallet (\"Test BIP39 Singlesig wallet\", 20 BTC) that no other e2e class references");

        // 1. Wait for NodeGuard to be up AND for DbInitializer to have seeded and funded the wallets:
        //    the host answers gRPC before the seeding hosted service has finished seeding.
        var wallet = await RetryAsync(async () =>
        {
            var wallets = await client.GetAvailableWalletsAsync(
                new GetAvailableWalletsRequest { WalletType = WALLET_TYPE.Hot }, headers);
            return wallets.Wallets.SingleOrDefault(w => w.Id == walletId)
                   ?? throw new InvalidOperationException(
                       $"wallet {walletId} is not an available hot wallet; seen: " +
                       $"[{string.Join(", ", wallets.Wallets.Select(w => $"{w.Id}:{w.Name}"))}]");
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetAvailableWallets (NodeGuard readiness)");
        _output.WriteLine($"target wallet {wallet.Id} \"{wallet.Name}\" (hot={wallet.IsHotWallet})");

        // 2. Baseline, taken BEFORE anything is added to the exclusion list: with a short list the
        //    selection already works for this wallet. Every later assertion is a regression against
        //    this snapshot, which is what makes a later failure attributable to the list's LENGTH
        //    rather than to the wallet being broken or empty. It is a subset, not the whole answer:
        //    step 3 deliberately adds spendable UTXOs on top of it.
        var baselineOutpoints = await RetryAsync(async () =>
        {
            var selection = await SelectAsync(client, headers, walletId, COIN_SELECTION_STRATEGY.BiggestFirst);
            if (selection.Confirmed.Sum(u => u.Amount) < ProbeAmountSats)
                throw new InvalidOperationException($"wallet {walletId} has no spendable UTXO yet");
            return selection.Confirmed.Select(u => u.Outpoint).ToList();
        }, attempts: 60, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (target wallet funded)");
        _output.WriteLine($"baseline selection: {baselineOutpoints.Count} UTXO(s)");

        // 3. Give the wallet enough outputs to overflow the request line — plus a few larger ones that
        //    stay spendable, so the selection this test inspects has more than one element in it — in
        //    ONE transaction on ONE reserved address. One transaction because a loop of sendtoaddress
        //    would chain each send onto the previous one's unconfirmed change and hit bitcoind's
        //    25-ancestor mempool limit; one address because NBXplorer keys its UTXO set by outpoint,
        //    not by script, so repeated payments to the same address are still independent UTXOs — and
        //    reusing an address keeps the wallet's address index (shared state) where it was.
        var addressResponse = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = true }, headers);
        var fundingAddress = BitcoinAddress.Create(addressResponse.Address, Network.RegTest);
        var fundingValues = Enumerable.Repeat(FrozenUtxoValueSats, FrozenUtxoCount)
            .Concat(UnfrozenUtxoValuesSats)
            .ToList();
        var fundingTxId = await BroadcastManyOutputsAsync(rpc, fundingAddress, fundingValues);
        await MineAsync(rpc, 6);

        // Only the outputs paying our address are ours: bitcoind's change output pays a script of its
        // own. Value tells the two batches apart — no unfrozen value coincides with the frozen one.
        var fundingTx = await rpc.GetRawTransactionAsync(fundingTxId);
        var ours = fundingTx.Outputs.AsIndexedOutputs()
            .Where(o => o.TxOut.ScriptPubKey == fundingAddress.ScriptPubKey)
            .ToList();
        var frozenOutpoints = ours.Where(o => o.TxOut.Value == Money.Satoshis(FrozenUtxoValueSats))
            .Select(o => new OutPoint(fundingTxId, o.N).ToString()).ToList();
        var spendableOutpoints = ours.Where(o => o.TxOut.Value != Money.Satoshis(FrozenUtxoValueSats))
            .Select(o => new OutPoint(fundingTxId, o.N).ToString()).ToList();
        frozenOutpoints.Should().HaveCount(FrozenUtxoCount,
            "the funding transaction must carry one output per intended exclusion");
        spendableOutpoints.Should().HaveCount(UnfrozenUtxoValuesSats.Length,
            "the same transaction must carry the larger outputs that keep the selection multi-element");
        _output.WriteLine($"funded {FrozenUtxoCount} x {FrozenUtxoValueSats} sats to freeze and " +
                          $"[{string.Join(", ", UnfrozenUtxoValuesSats)}] sats to leave spendable, in {fundingTxId}");

        // NBXplorer indexes on block-connect through a notification pipeline that can lag a second or
        // two, and an outpoint it has not indexed yet is not in the wallet's UTXO set — so NodeGuard
        // would filter it straight back out of the frozen list instead of putting it on the wire.
        var fundedOutpoints = frozenOutpoints.Concat(spendableOutpoints).ToList();
        await RetryAsync(async () =>
        {
            var all = await client.GetUtxosAsync(new GetUtxosRequest(), headers);
            var indexed = all.Confirmed.Select(u => u.Outpoint).ToHashSet();
            var missing = fundedOutpoints.Count(outpoint => !indexed.Contains(outpoint));
            if (missing > 0)
                throw new InvalidOperationException($"{missing}/{fundedOutpoints.Count} funded UTXOs not indexed yet");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetUtxos (funded UTXOs indexed)");

        // 4. Non-vacuity guard. The transport no longer varies with the size of the list, so what
        //    makes this a regression test rather than a plain smoke test is that the list is big
        //    enough that the OLD query-string form could not have carried it: a shorter one would
        //    have been served fine before the fix and would prove nothing. So assert on the byte
        //    count that decided it: the exclusion parameters ALONE must not fit on a request line.
        //    This is
        //    a strict lower bound (it charges nothing for the ~110-char derivation scheme in the path
        //    or for the strategy/limit/amount/minimumValue parameters), so clearing it is conclusive.
        var ignoreListBytes = frozenOutpoints.Sum(outpoint => IgnoreOutpointParam.Length + outpoint.Length);
        var requestLineLowerBound = "GET ".Length + "/v1/cryptos/btc/derivations/".Length
                                                  + "/selectutxos".Length + " HTTP/1.1".Length + ignoreListBytes;
        _output.WriteLine($"exclusion list: {frozenOutpoints.Count} outpoints / {ignoreListBytes} bytes; " +
                          $"request line >= {requestLineLowerBound} bytes vs Kestrel's {KestrelMaxRequestLineSize}");
        requestLineLowerBound.Should().BeGreaterThan(KestrelMaxRequestLineSize,
            $"{FrozenUtxoCount} exclusions must overflow Kestrel's request line, otherwise this test would " +
            "also have passed against the pre-fix query-string form and would be guarding nothing");

        // 5. Freeze them all in one call. Frozen ∩ wallet is the part of the exclusion list that both
        //    code paths deliberately keep sending, so it is the durable way to make the list long.
        await SetManuallyFrozenAsync(client, headers, frozenOutpoints, frozen: true);

        // 6. The regression itself. No retry loop: once the UTXOs are indexed the answer is
        //    deterministic, so retrying would only turn an instant failure into a slow one — and it
        //    would mask an intermittent regression rather than report it.
        var expectedSelectable = baselineOutpoints.Count + spendableOutpoints.Count;
        var selection = await SelectAsync(client, headers, walletId, COIN_SELECTION_STRATEGY.BiggestFirst);
        selection.Confirmed.Should().NotBeEmpty(
            $"the {frozenOutpoints.Count}-outpoint exclusion list travels in a POST body; on the query string it " +
            "would overflow the request line, NBXplorer would answer 414, GetAvailableUtxos would swallow the " +
            "failure into new UTXOChanges() and report a successful RPC with nothing selectable for a 20 BTC wallet");
        selection.Confirmed.Select(u => u.Outpoint).Should().Contain(baselineOutpoints,
            "a long exclusion list must not cost the wallet any of the UTXOs it could spend before");
        selection.Confirmed.Select(u => u.Outpoint).Should().Contain(spendableOutpoints,
            "the larger outputs of the funding transaction were never frozen, so a correct backend returns them " +
            "alongside the baseline — and they are what keeps the ordering and limit assertions below honest");
        selection.Confirmed.Select(u => u.Outpoint).Should().NotIntersectWith(frozenOutpoints,
            $"every frozen outpoint is worth {FrozenUtxoValueSats} sats, comfortably above the backend's " +
            "minimumValue floor, and the gRPC path applies no local filter afterwards — so the only thing that " +
            "can keep them out of the response is the exclusion list arriving and being honoured server-side");
        selection.Confirmed.Should().HaveCountGreaterThanOrEqualTo(expectedSelectable,
            "the ordering assertion that follows is only worth making over several UTXOs of different values: " +
            "any one-element list is both ascending and descending");
        selection.Confirmed.Select(u => u.Amount).Should().BeInDescendingOrder(
            "BiggestFirst orders the selection server-side (value DESC), so a correctly ordered response is " +
            "proof the answer came from the custom backend rather than from some degraded fallback");

        // SmallestFirst is the adversarial ordering: the frozen UTXOs are by far the smallest, so any
        // leak surfaces at the very front of this response instead of being buried.
        var smallestFirst = await SelectAsync(client, headers, walletId, COIN_SELECTION_STRATEGY.SmallestFirst);
        smallestFirst.Confirmed.Should().NotBeEmpty(
            "an empty answer here is the swallowed-failure mode this whole test exists to catch, not a pass: if " +
            "NBXplorer 5xx'd or restarted between the two calls, GetAvailableUtxos would degrade to " +
            "new UTXOChanges() and both assertions below would hold over nothing at all");
        smallestFirst.Confirmed.Should().HaveCountGreaterThanOrEqualTo(expectedSelectable,
            "SmallestFirst sees the same unfrozen UTXOs as BiggestFirst, so a shorter answer means something " +
            "was dropped — and would leave the ordering assertion below with too little to order");
        smallestFirst.Confirmed.Select(u => u.Outpoint).Should().NotIntersectWith(frozenOutpoints,
            "SmallestFirst would rank the frozen UTXOs ahead of everything else if they leaked through");
        smallestFirst.Confirmed.Select(u => u.Amount).Should().BeInAscendingOrder(
            "SmallestFirst orders value ASC server-side, the exact reverse of the response above, over the same set");

        // The body carries only the exclusions; strategy/limit/amount/minimumValue stay on the query
        // string. Asking for a single UTXO checks that the split did not lose them: with several
        // candidates available, a limit that never reached the backend comes back with all of them,
        // and a limit applied to the wrong ordering comes back with the wrong one.
        var limitedToOne = await client.GetAvailableUtxosAsync(new GetAvailableUtxosRequest
        {
            WalletId = walletId,
            Strategy = COIN_SELECTION_STRATEGY.BiggestFirst,
            Amount = 1,
            Limit = 1,
        }, headers);
        limitedToOne.Confirmed.Should().ContainSingle(
            $"limit=1 travels as a query parameter alongside the body, so with {selection.Confirmed.Count} " +
            "UTXOs available it must still trim the answer to exactly one")
            .Which.Amount.Should().Be(selection.Confirmed.Max(u => u.Amount),
                "BiggestFirst with limit=1 must pick the wallet's largest available UTXO, not just any one of them");

        // 7. Fixture proof: unfreeze exactly one of them and it comes back. Without this, the
        //    NotIntersectWith assertions above would also hold if the UTXOs were missing for some
        //    unrelated reason (never indexed, wrong wallet, filtered on value) — this pins the
        //    exclusion on the tag we set, and the list is still 129 long while it runs.
        var probeOutpoint = frozenOutpoints[0];
        try
        {
            await SetManuallyFrozenAsync(client, headers, [probeOutpoint], frozen: false);
            var withProbeThawed = await SelectAsync(client, headers, walletId, COIN_SELECTION_STRATEGY.SmallestFirst);
            withProbeThawed.Confirmed.Select(u => u.Outpoint).Should().Contain(probeOutpoint,
                "an unfrozen UTXO above the dust floor must be selectable again, which proves the other " +
                "outpoints are absent because they are frozen and not for some incidental reason");
        }
        finally
        {
            // Re-freeze even if the assertion failed: these UTXOs stay frozen for the rest of the run
            // on purpose. Nothing else uses this wallet, and frozen UTXOs are inert for every selection
            // path, whereas leaving 130 spendable 1000-sat inputs behind would reshape any later
            // selection on it. The few larger outputs stay spendable by design — they are ordinary
            // wallet coins and each further run simply adds its own. Nothing is cleaned up: `just
            // test-e2e` tears the volumes down, and a long-lived dev stack keeps them.
            await SetManuallyFrozenAsync(client, headers, [probeOutpoint], frozen: true);
        }
    }

    private static Task<GetUtxosResponse> SelectAsync(NodeGuardService.NodeGuardServiceClient client,
        Metadata headers, int walletId, COIN_SELECTION_STRATEGY strategy)
        => client.GetAvailableUtxosAsync(new GetAvailableUtxosRequest
        {
            WalletId = walletId,
            Strategy = strategy,
            Amount = ProbeAmountSats,
            // 0 means "do not trim the result", so the response is the full set of UTXOs the backend
            // considers available — which is what the "none of these appear" assertions need.
            Limit = 0,
        }, headers).ResponseAsync;

    /// <summary>
    /// Fails fast unless NBXplorer routes POST on selectutxos — the transport every selectutxos call uses.
    /// </summary>
    /// <remarks>
    /// MVC answers a method mismatch from the endpoint's method matcher, before model binding and
    /// before the controller's <c>[Authorize]</c>, so a POST carrying a deliberately unparseable
    /// derivation scheme is a read-only capability probe: it touches no wallet and needs no
    /// credentials. A pre-fix image answers <b>405</b> with <c>Allow: GET</c>; a patched one answers
    /// 400, because the junk scheme fails to bind. The readiness call comes first so "this backend
    /// predates the fix" is never confused with "this backend is not up yet" — and so an unreachable
    /// NBXplorer fails loudly instead of quietly excusing the test.
    /// </remarks>
    private async Task AssertSelectUtxosAcceptsPostAsync()
    {
        var baseUri = Env("NBXPLORER_URI", "http://localhost:32838").TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        await RetryAsync(async () =>
        {
            var status = await http.GetAsync($"{baseUri}/v1/cryptos/btc/status");
            if (!status.IsSuccessStatusCode)
                throw new InvalidOperationException($"status endpoint answered {(int)status.StatusCode}");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(2), what: $"NBXplorer readiness ({baseUri})");

        var probe = await http.PostAsync($"{baseUri}/v1/cryptos/btc/derivations/PROBE/selectutxos?amount=0",
            JsonContent.Create(new { ignoreOutpoints = Array.Empty<string>() }));
        var allow = probe.Content.Headers.Allow.Count > 0
            ? string.Join(", ", probe.Content.Headers.Allow)
            : probe.Headers.TryGetValues("Allow", out var values) ? string.Join(", ", values) : "unset";
        _output.WriteLine($"NBXplorer {baseUri}: POST selectutxos -> {(int)probe.StatusCode} (Allow: {allow})");

        probe.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
            $"the NBXplorer at {baseUri} serves selectutxos on GET only (Allow: {allow}), so it predates the POST " +
            "route this test exercises. Nothing below would be measuring NodeGuard: the POST 405s, " +
            "GetAvailableUtxos degrades to an empty UTXOChanges, and every assertion fails identically whether " +
            "or not NodeGuard is behaving. Point the stack at an NBXplorer carrying the POST route: " +
            "the image is pinned in docker/docker-compose.dev.yml");
    }

    /// <summary>
    /// Freezes (or unfreezes) outpoints the same way the Wallets UI does, in a single AddTags call.
    /// </summary>
    private static async Task SetManuallyFrozenAsync(NodeGuardService.NodeGuardServiceClient client,
        Metadata headers, IEnumerable<string> outpoints, bool frozen)
    {
        var request = new AddTagsRequest();
        foreach (var outpoint in outpoints)
        {
            request.Tags.Add(new Tag
            {
                Key = ManuallyFrozenTagKey,
                Value = frozen ? "true" : "false",
                UtxoOutpoint = outpoint,
            });
        }

        await client.AddTagsAsync(request, headers);
    }

    /// <summary>
    /// Broadcasts one transaction paying every value in <paramref name="outputValuesSats"/> to the
    /// same address, as that many separate outputs, and returns its txid.
    /// </summary>
    /// <remarks>
    /// Assembled with NBitcoin and handed to bitcoind only to sign and broadcast, because none of
    /// bitcoind's transaction-building RPCs will produce this shape. sendmany takes its outputs as a
    /// map keyed by address, so repeated payments to one address collapse into a single summed
    /// output. createrawtransaction's array form rejects the same address twice outright — "Invalid
    /// parameter, duplicated address" (RPC error -8), and its help says so: "no address may be
    /// duplicated". That restriction belongs to the RPC alone. A transaction is a plain list of
    /// outputs, so one carrying 130 identical scriptPubKeys is valid, standard and relayable, and
    /// bitcoind signs and accepts it without complaint. NBitcoin serialises it happily too: its only
    /// refusal is an INPUT-LESS transaction, which is why the input is picked here from listunspent
    /// rather than left to fundrawtransaction.
    /// </remarks>
    private async Task<uint256> BroadcastManyOutputsAsync(RPCClient rpc, BitcoinAddress address,
        IReadOnlyCollection<long> outputValuesSats)
    {
        var paid = Money.Satoshis(outputValuesSats.Sum());

        // One confirmed coin of bitcoind's own pays for the whole batch: a single input keeps the
        // transaction small beside its many outputs, and keeps it off any unconfirmed ancestor chain.
        var coin = (await rpc.ListUnspentAsync(1, int.MaxValue))
            .Where(c => c.IsSpendable)
            .OrderByDescending(c => c.Amount)
            .FirstOrDefault();
        if (coin is null)
            throw new InvalidOperationException("bitcoind's wallet holds no confirmed spendable coin to fund from");

        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(coin.OutPoint);
        foreach (var value in outputValuesSats)
        {
            tx.Outputs.Add(Money.Satoshis(value), address);
        }

        var change = tx.Outputs.Add(Money.Zero, await rpc.GetRawChangeAddressAsync());
        var fee = Money.Satoshis(FundingFeeRateSatsPerVByte * (tx.GetVirtualSize() + SignedInputVSizeAllowance));
        change.Value = coin.Amount - paid - fee;
        change.Value.Satoshi.Should().BeGreaterThan(FrozenUtxoValueSats,
            $"the largest confirmed coin bitcoind holds ({coin.Amount}) has to cover {paid} of outputs and a {fee} " +
            "fee and still leave a change output well clear of the dust relay threshold");

        var signed = await rpc.SignRawTransactionWithWalletAsync(new SignRawTransactionRequest { Transaction = tx });
        signed.Complete.Should().BeTrue(
            $"bitcoind must be able to sign the coin it listed as spendable ({coin.OutPoint}); errors: " +
            (signed.Errors?.Length > 0 ? string.Join(", ", signed.Errors) : "none"));

        var txId = await rpc.SendRawTransactionAsync(signed.SignedTransaction);
        _output.WriteLine($"funding tx {txId}: {outputValuesSats.Count} outputs totalling {paid}, " +
                          $"{tx.GetVirtualSize()} vB, {fee} fee, funded by {coin.OutPoint}");
        return txId;
    }
}
