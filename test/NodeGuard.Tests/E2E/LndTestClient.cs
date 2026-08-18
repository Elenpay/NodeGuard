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

using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using Routerrpc;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Test-only driver for a single LND node over its gRPC API — the in-process replacement for the deleted
/// <c>generate-flow.sh</c> sidecar, so the fee-engine flow scenario can open channels and send force-routed
/// payments itself. Connects like NodeGuard's own LND clients (<c>https://{host}</c>, cert check off for
/// regtest self-signed certs, admin macaroon hex per call); host + macaroon come from <c>{NODE}_HOST</c> /
/// <c>{NODE}_MACAROON</c> — the process env, or the shared env file extract-env.sh writes (see FromEnv).
/// </summary>
internal sealed class LndTestClient
{
    public string Name { get; }
    public string PubKey { get; }
    public Lightning.LightningClient Lightning { get; }
    public Router.RouterClient RouterClient { get; }

    private readonly Metadata _auth;

    private LndTestClient(string name, string pubKey, GrpcChannel channel, string macaroonHex)
    {
        Name = name;
        PubKey = pubKey;
        Lightning = new Lightning.LightningClient(channel);
        RouterClient = new Router.RouterClient(channel);
        _auth = new Metadata { { "macaroon", macaroonHex } };
    }

    // extract-env.sh writes {NODE}_HOST/{NODE}_MACAROON to this file on the mounted e2e_env volume. Load it
    // into the environment once — like NodeGuard's Program.cs does — so FromEnv finds the creds without the
    // runner entrypoint exporting them. Override the path with LND_ENV_FILE; absent file → no-op.
    static LndTestClient()
    {
        var path = Environment.GetEnvironmentVariable("LND_ENV_FILE") ?? "/shared/nodeguard-macaroons.env";
        if (File.Exists(path)) DotNetEnv.Env.Load(path);
    }

    public static LndTestClient FromEnv(string name, string pubKey)
    {
        var key = name.ToUpperInvariant();
        var host = Environment.GetEnvironmentVariable($"{key}_HOST");
        var macaroon = Environment.GetEnvironmentVariable($"{key}_MACAROON");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(macaroon))
            throw new InvalidOperationException(
                $"Missing {key}_HOST/{key}_MACAROON — the fee-engine flow e2e drives LND directly and needs the " +
                "connection env from docker/e2e/extract-env.sh (process env, or LND_ENV_FILE / /shared/nodeguard-macaroons.env).");

        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var channel = GrpcChannel.ForAddress($"https://{host}", new GrpcChannelOptions { HttpHandler = httpHandler });
        return new LndTestClient(name, pubKey, channel, macaroon);
    }

    // One ListChannels round-trip; the scid/balance helpers below each build on it.
    private async Task<IReadOnlyList<Lnrpc.Channel>> ChannelsToAsync(string peerPubKey)
    {
        var resp = await Lightning.ListChannelsAsync(new ListChannelsRequest(), _auth);
        return resp.Channels.Where(c => c.RemotePubkey == peerPubKey).ToList();
    }

    // Largest-capacity CONFIRMED channel (ChanId != 0) toward the peer whose capacity is at least
    // minCapacitySats, or null if none qualifies.
    public async Task<ulong?> ScidToAsync(string peerPubKey, long minCapacitySats = 0)
        => (await ChannelsToAsync(peerPubKey))
            .Where(c => c.ChanId != 0 && c.Capacity >= minCapacitySats)
            .OrderByDescending(c => c.Capacity)
            .FirstOrDefault()?.ChanId;

    public async Task<long> LocalBalanceToAsync(string peerPubKey)
        => (await ChannelsToAsync(peerPubKey)).Select(c => c.LocalBalance).DefaultIfEmpty(0).Max();

    // Peer's local balance = their sending liquidity toward us.
    public async Task<long> RemoteBalanceToAsync(string peerPubKey)
        => (await ChannelsToAsync(peerPubKey)).Select(c => c.RemoteBalance).DefaultIfEmpty(0).Max();

    // Peer's local balance on a SPECIFIC channel (by scid).
    public async Task<long> RemoteBalanceOnScidAsync(string peerPubKey, ulong scid)
        => (await ChannelsToAsync(peerPubKey)).Where(c => c.ChanId == scid).Select(c => c.RemoteBalance).DefaultIfEmpty(0).Max();

    // Idempotent — an "already connected" RpcException is expected and swallowed.
    public async Task ConnectAsync(string peerPubKey, string hostPort)
    {
        try
        {
            await Lightning.ConnectPeerAsync(new ConnectPeerRequest
            {
                Addr = new LightningAddress { Pubkey = peerPubKey, Host = hostPort },
                Perm = false,
            }, _auth);
        }
        catch (RpcException ex) when (
             ex.StatusCode == StatusCode.AlreadyExists ||
             ex.StatusCode == StatusCode.FailedPrecondition ||
             ex.Status.Detail.Contains("already connected", StringComparison.OrdinalIgnoreCase))
        {
            // expected: peer already connected
        }
    }

    // Returns once the funding tx is BROADCAST (not yet confirmed) — the caller mines to confirm.
    public async Task<ChannelPoint> OpenChannelAsync(string peerPubKey, long localSats, long pushSats)
    {
        return await Lightning.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom(Convert.FromHexString(peerPubKey)),
            LocalFundingAmount = localSats,
            PushSat = pushSats,
            SatPerVbyte = 2, // regtest has no fee estimation to fall back on
        }, _auth);
    }

    public async Task<string> AddInvoiceAsync(long amtSats)
    {
        var resp = await Lightning.AddInvoiceAsync(new Invoice { Value = amtSats }, _auth);
        return resp.PaymentRequest;
    }

    // Pays a fresh invoice from the receiver, forcing the FIRST hop over firstHopScid (LND pathfinds the
    // rest). Returns true only on a settled payment.
    public async Task<bool> PayViaScidAsync(LndTestClient receiver, ulong firstHopScid, long amtSats, int timeoutSecs = 60)
    {
        string paymentRequest;
        try
        {
            paymentRequest = await receiver.AddInvoiceAsync(amtSats);
        }
        catch (RpcException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(paymentRequest)) return false;

        var request = new SendPaymentRequest
        {
            PaymentRequest = paymentRequest,
            OutgoingChanId = firstHopScid,
            TimeoutSeconds = timeoutSecs,
            FeeLimitSat = Math.Max(1_000, amtSats), // generous for regtest — never the reason a hop fails
            NoInflightUpdates = true,
        };

        try
        {
            using var call = RouterClient.SendPaymentV2(request, _auth);
            await foreach (var payment in call.ResponseStream.ReadAllAsync())
            {
                if (payment.Status == Payment.Types.PaymentStatus.Succeeded) return true;
                if (payment.Status == Payment.Types.PaymentStatus.Failed) return false;
            }
        }
        catch (RpcException)
        {
            return false;
        }
        return false;
    }
}
