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
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using FluentAssertions;

namespace NodeGuard.Services.Spark;

public class SparkSignerTransportTests
{
    [Fact]
    public async Task TheFunctionUrlTransport_PostsSigV4SignedJsonToTheSparkPath()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":"refused"}""")
        });
        var transport = new FunctionUrlSparkSignerTransport(new HttpClient(handler),
            new Uri("https://abc.lambda-url.eu-central-1.on.aws/"), "eu-central-1",
            () => new ImmutableCredentials("AKIDEXAMPLE", "not-a-real-secret", null));

        var (status, body) = await transport.InvokeAsync("/spark/info", """{"wallet":"ed0210c8"}""", CancellationToken.None);

        status.Should().Be(403);
        body.Should().Be("""{"error":"refused"}""");
        handler.Request!.RequestUri.Should().Be(new Uri("https://abc.lambda-url.eu-central-1.on.aws/spark/info"));
        handler.Request.Method.Should().Be(HttpMethod.Post);
        handler.Request.Headers.GetValues("Authorization").Single().Should()
            .StartWith("AWS4-HMAC-SHA256").And.Contain("/eu-central-1/lambda/aws4_request");
        handler.Body.Should().Be("""{"wallet":"ed0210c8"}""");
    }

    [Fact]
    public async Task TheEmulatorTransport_SendsAFunctionUrlEventAndUnwrapsTheResponse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"statusCode":200,"headers":{"Content-Type":"application/json"},"body":"{\"result\":\"AA==\"}"}""")
        });
        var transport = new RieSparkSignerTransport(new HttpClient(handler),
            new Uri("http://spark-signer:8080/2015-03-31/functions/function/invocations"));

        var (status, body) = await transport.InvokeAsync("/spark/identity-public-key", """{"wallet":"ed0210c8"}""", CancellationToken.None);

        status.Should().Be(200);
        body.Should().Be("""{"result":"AA=="}""");
        using var invocation = JsonDocument.Parse(handler.Body!);
        invocation.RootElement.GetProperty("rawPath").GetString().Should().Be("/spark/identity-public-key");
        invocation.RootElement.GetProperty("body").GetString().Should().Be("""{"wallet":"ed0210c8"}""");
        invocation.RootElement.GetProperty("version").GetString().Should().Be("2.0");
    }

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return respond(request);
        }
    }
}
