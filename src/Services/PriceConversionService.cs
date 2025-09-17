// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using System.Text.Json;
using NBitcoin;

namespace NodeGuard.Services;

public interface IPriceConversionService
{
    public Task<decimal> GetBtcToUsdPrice();
    public decimal SatToUsdConversion(decimal sats, decimal btcPrice);
    public decimal BtcToUsdConversion(decimal btc, decimal btcPrice);
}
public class PriceConversionService : IPriceConversionService
{

    private readonly HttpClient _httpClient;
    private readonly ILogger<PriceConversionService> _logger;

    public PriceConversionService(HttpClient httpClient, ILogger<PriceConversionService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }


    public async Task<decimal> GetBtcToUsdPrice()
    {
        decimal btcPrice;

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, Constants.COINGECKO_ENDPOINT);

            requestMessage.Headers.Add("x-cg-pro-api-key", Constants.COINGECKO_KEY);

            var response = await _httpClient.SendAsync(requestMessage);
            string json = await response.Content.ReadAsStringAsync();
            JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement[0].GetProperty("current_price").GetDecimal();
        }
        catch (Exception e)
        {
            _logger.LogError("Bitcoin price could not be retrieved {Message}", e.Message);
            btcPrice = 0;
        }

        return btcPrice;
    }

    public decimal SatToUsdConversion(decimal sats, decimal btcPrice)
    {
        return Money.Satoshis(sats).ToUnit(MoneyUnit.BTC) * btcPrice;
    }

    public decimal BtcToUsdConversion(decimal btc, decimal btcPrice)
    {
        return btc * btcPrice;
    }
}
