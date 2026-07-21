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

using System.Text.Json;
using FluentAssertions;

namespace NodeGuard.Services;

/// <summary>
/// Contract test guarding the Payments Watcher frontend seam. Blazor's IJSRuntime
/// serializes interop arguments with <see cref="JsonSerializerDefaults.Web"/> (camelCase),
/// so passing the <see cref="PaymentGraph"/> record straight to
/// <c>InvokeVoidAsync("paymentsWatcher.render", ...)</c> must yield the exact camelCase keys
/// that <c>wwwroot/js/payments-watcher-graph.js</c> reads. If someone hand-serializes with
/// default (PascalCase) options, the graph renders blank — these tests fail first.
/// </summary>
public class PaymentGraphSerializationTests
{
    // The exact options Blazor's IJSRuntime uses for interop argument serialization.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static PaymentGraph SampleGraph() => new(
        Nodes: new List<PaymentGraphNode>
        {
            new(Id: "03origin", IsOrigin: true,
                Payments: new List<PaymentGraphNodePayment> { new("hash1", "success") },
                Alias: "origin-node"),
            new(Id: "02hop", IsOrigin: false,
                Payments: new List<PaymentGraphNodePayment> { new("hash1", "failed") })
        },
        Channels: new List<PaymentGraphChannel>
        {
            new(
                Id: "18446744073709551615", // uint64 max — must survive as a JSON string
                From: "03origin",
                To: "02hop",
                PaymentId: "hash1",
                PaymentStatus: "failed",
                HopStatus: "failed_here",
                FailureCode: "TEMPORARY_CHANNEL_FAILURE",
                AttemptIndex: 2,
                HopSequence: 1)
        });

    [Fact]
    public void PaymentGraph_SerializedWithWebDefaults_UsesCamelCaseKeysTheRendererReads()
    {
        var json = JsonSerializer.Serialize(SampleGraph(), WebOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Container keys.
        root.TryGetProperty("nodes", out _).Should().BeTrue("the renderer reads graph.nodes");
        root.TryGetProperty("channels", out _).Should().BeTrue("the renderer reads graph.channels");

        // Node keys.
        var node = root.GetProperty("nodes")[0];
        node.TryGetProperty("id", out _).Should().BeTrue();
        node.TryGetProperty("isOrigin", out var isOrigin).Should().BeTrue("node.isOrigin drives origin styling/layout");
        isOrigin.GetBoolean().Should().BeTrue();
        node.TryGetProperty("payments", out _).Should().BeTrue();
        node.TryGetProperty("alias", out _).Should().BeTrue();

        // Node payment keys.
        var nodePayment = node.GetProperty("payments")[0];
        nodePayment.TryGetProperty("id", out _).Should().BeTrue();
        nodePayment.TryGetProperty("status", out var payStatus).Should().BeTrue("p.status drives node success/fail counts");
        payStatus.GetString().Should().Be("success");

        // Channel keys the renderer reads.
        var channel = root.GetProperty("channels")[0];
        channel.TryGetProperty("id", out _).Should().BeTrue();
        channel.TryGetProperty("from", out _).Should().BeTrue();
        channel.TryGetProperty("to", out _).Should().BeTrue();
        channel.TryGetProperty("paymentId", out _).Should().BeTrue();
        channel.TryGetProperty("paymentStatus", out var chStatus).Should().BeTrue("ch.paymentStatus drives edge visibility/colour");
        chStatus.GetString().Should().Be("failed");
        channel.TryGetProperty("hopStatus", out _).Should().BeTrue("hopStatus drives per-hop trace tone");
        channel.TryGetProperty("failureCode", out _).Should().BeTrue("failureCode is shown next to a failed hop");
        channel.TryGetProperty("attemptIndex", out _).Should().BeTrue();
        channel.TryGetProperty("hopSequence", out _).Should().BeTrue();
    }

    [Fact]
    public void PaymentGraph_ChannelId_IsSerializedAsString_NotNumber()
    {
        // Channel ids are uint64 > 2^53; they must ride the wire as strings or JS loses precision.
        var json = JsonSerializer.Serialize(SampleGraph(), WebOptions);

        using var doc = JsonDocument.Parse(json);
        var idElement = doc.RootElement.GetProperty("channels")[0].GetProperty("id");

        idElement.ValueKind.Should().Be(JsonValueKind.String);
        idElement.GetString().Should().Be("18446744073709551615");
    }

    [Fact]
    public void PaymentGraph_SerializedWithWebDefaults_DoesNotEmitPascalCaseKeys()
    {
        // Regression guard: the "renders blank" bug is PascalCase output. Prove Web defaults
        // do not leak PascalCase variants of the keys the renderer relies on.
        var json = JsonSerializer.Serialize(SampleGraph(), WebOptions);

        json.Should().NotContain("\"IsOrigin\"");
        json.Should().NotContain("\"PaymentStatus\"");
        json.Should().NotContain("\"HopStatus\"");
        json.Should().NotContain("\"AttemptIndex\"");
        json.Should().NotContain("\"HopSequence\"");
    }
}
