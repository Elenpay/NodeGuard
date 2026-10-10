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

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using NSpark.RemoteSigner;

namespace NodeGuard.Services.Spark;

/// <summary>
/// The remote signer's IAM-authenticated Function URL, SigV4-signed with the same AWS credentials
/// as PSBT signing. Requests go to /spark/{op} on the Function URL; anything else is the PSBT
/// signer.
/// </summary>
public sealed class FunctionUrlSparkSignerTransport : ISparkSignerTransport
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _region;
    private readonly Func<ImmutableCredentials> _credentials;

    /// <param name="http">HTTP client.</param>
    /// <param name="endpoint">The Function URL with no path (checked by <see cref="SparkSettings"/>).</param>
    /// <param name="region">AWS region of the function.</param>
    /// <param name="credentials">AWS credentials of the caller.</param>
    public FunctionUrlSparkSignerTransport(HttpClient http, Uri endpoint, string region, Func<ImmutableCredentials> credentials)
    {
        _http = http;
        _endpoint = endpoint;
        _region = region;
        _credentials = credentials;
    }

    public async Task<(int Status, string Body)> InvokeAsync(string path, string jsonBody, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            new Uri(_endpoint, path),
            new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            regionName: _region,
            serviceName: "lambda",
            credentials: _credentials(),
            cancellationToken: ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// A signer image running locally under the Lambda runtime interface emulator (dev only): the
/// emulator takes the raw invocation event, so the Function URL event (payload format 2.0) is built
/// here and the function's response unwrapped.
/// </summary>
public sealed class RieSparkSignerTransport : ISparkSignerTransport
{
    private readonly HttpClient _http;
    private readonly Uri _invokeUrl;

    /// <param name="http">HTTP client.</param>
    /// <param name="invokeUrl">The emulator's invocation URL (…/2015-03-31/functions/function/invocations).</param>
    public RieSparkSignerTransport(HttpClient http, Uri invokeUrl)
    {
        _http = http;
        _invokeUrl = invokeUrl;
    }

    public async Task<(int Status, string Body)> InvokeAsync(string path, string jsonBody, CancellationToken ct)
    {
        var invocation = new
        {
            version = "2.0",
            rawPath = path,
            body = jsonBody,
            isBase64Encoded = false,
            requestContext = new { http = new { method = "POST", path } }
        };

        using var response = await _http.PostAsJsonAsync(_invokeUrl, invocation, ct);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (result.RootElement.GetProperty("statusCode").GetInt32(),
            result.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty);
    }
}
