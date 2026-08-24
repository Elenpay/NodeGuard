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
using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NodeGuard.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq.Protected;
using NBitcoin;

namespace NodeGuard.Services;

public class NBXplorerServiceTests
{
    private readonly ILogger<NBXplorerService> _logger = new Mock<ILogger<NBXplorerService>>().Object;
    private readonly InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

    /// <summary>
    /// Captures what the service put on the wire. NBXPLORER_URI is unset under test, which leaves
    /// the request URI relative, so the client needs a base address to resolve it against.
    /// </summary>
    private sealed class RequestRecorder
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        public HttpClient CreateClient()
        {
            var handler = new Mock<HttpMessageHandler>();
            handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                {
                    Request = request;
                    // Read it here: HttpClient disposes the content once the send completes
                    Body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                })
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });

            return new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:32838") };
        }
    }

    private static List<string> CreateOutpoints(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new OutPoint(new uint256((uint)i), 0).ToString())
            .ToList();
    }

    private static int CountIgnoreOutpointParams(Uri uri)
    {
        return uri.Query.Split('&').Count(part => part.TrimStart('?').StartsWith("ignoreOutpoint="));
    }

    // Both a handful of outpoints and a list far past the old ~90-outpoint request-line ceiling:
    // the transport does not vary with size, so neither can 414.
    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    public async Task GetUTXOsByLimitAsync_SendsPostWithTheOutpointsInTheBody(int outpointCount)
    {
        // Arrange
        var recorder = new RequestRecorder();
        var service = new NBXplorerService(recorder.CreateClient(), _logger);
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
        var ignoreOutpoints = CreateOutpoints(outpointCount);

        // Act
        await service.GetUTXOsByLimitAsync(derivationStrategy, CoinSelectionStrategy.SmallestFirst, 50, 40_000, 0,
            ignoreOutpoints);

        // Assert: nothing about the exclusion list touches the request line, so Kestrel's 8KB cap
        // is out of the picture regardless of how many outpoints there are
        recorder.Request.Should().NotBeNull();
        recorder.Request!.Method.Should().Be(HttpMethod.Post);
        recorder.Request.RequestUri!.Query.Should().NotContain("ignoreOutpoint");
        CountIgnoreOutpointParams(recorder.Request.RequestUri!).Should().Be(0);

        // Pin the wire format: NBXplorer binds this to SelectUTXOsRequest.IgnoreOutpoints, and the
        // fork's CoinSelectionControllerTests posts exactly this shape
        recorder.Body.Should().NotBeNull();
        recorder.Body.Should().StartWith("{\"ignoreOutpoints\":[");
        foreach (var outpoint in ignoreOutpoints)
        {
            recorder.Body.Should().Contain($"\"{outpoint}\"");
        }
    }

    [Fact]
    public async Task GetUTXOsByLimitAsync_AlwaysSendsMinimumValueSoDustNeedsNoOutpoints()
    {
        // Arrange
        var recorder = new RequestRecorder();
        var service = new NBXplorerService(recorder.CreateClient(), _logger);
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();

        // Act
        await service.GetUTXOsByLimitAsync(derivationStrategy, CoinSelectionStrategy.SmallestFirst, 50, 40_000, 0,
            new List<string>());

        // Assert: one scalar parameter replaces what used to be one parameter per dust UTXO
        recorder.Request!.RequestUri!.Query.Should()
            .Contain($"minimumValue={Constants.MINIMUM_UTXO_VALUE_SATS}");
    }

    [Fact]
    public async Task GetUTXOsByLimitAsync_WhenBackendFails_ThrowsWithTheStatusCode()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.RequestUriTooLong)
            {
                Content = new StringContent(string.Empty)
            });
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:32838") };
        var service = new NBXplorerService(httpClient, _logger);
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();

        // Act
        var act = async () => await service.GetUTXOsByLimitAsync(derivationStrategy,
            CoinSelectionStrategy.SmallestFirst, 50, 40_000, 0, new List<string>());

        // Assert
        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*414*");
    }
}
