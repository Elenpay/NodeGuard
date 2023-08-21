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
using NBitcoin;
using RestSharp;
using Serilog;

namespace NodeGuard.Helpers;

public static class PriceConversionHelper
{
    public static decimal GetBtcToUsdPrice()
    {
        var client = new RestClient(Constants.COINGECKO_ENDPOINT);
        var request = new RestRequest
        {
            Method = Method.GET
        };
        request.AddHeader("x-cg-pro-api-key", Constants.COINGECKO_KEY);
        var response = client.Execute(request);

        decimal btcPrice;

        try
        {
            if (response.IsSuccessful)
            {
                JsonDocument document = JsonDocument.Parse(response.Content);
                btcPrice = document.RootElement[0].GetProperty("current_price").GetDecimal();
            }
            else
            {
                throw new Exception($"Bitcoin price could not be retrieved {response.ErrorMessage}");
            }
        }
        catch (Exception e)
        {
            Log.Logger.Error(e.Message);
            btcPrice = 0;
        }

        return btcPrice;
    }

    public static decimal SatToUsdConversion(decimal sats, decimal btcPrice)
    {
        return Money.Satoshis(sats).ToUnit(MoneyUnit.BTC) * btcPrice;
    }

    public static decimal BtcToUsdConversion(decimal btc, decimal btcPrice)
    {
        return btc * btcPrice;
    }
}