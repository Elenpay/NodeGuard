

using System.Text.Json;
using NBitcoin;
using RestSharp;
using Serilog;

namespace FundsManager.Helpers;

public static class PriceConversionHelper
{
    private static string? COINGECKO_KEY = Environment.GetEnvironmentVariable("COINGECKO_KEY"); // ToDo To be filled in production
    private static string? COINGECKO_ENDPOINT = Environment.GetEnvironmentVariable("COINGECKO_ENDPOINT");

    public static decimal GetBtcToUsdPrice()
    {
        var client = new RestClient(COINGECKO_ENDPOINT);
        var request = new RestRequest
        {
            Method = Method.GET
        };
        request.AddHeader("x-cg-pro-api-key", COINGECKO_KEY);
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
                throw new Exception("Bitcoin price could not be retrieved");
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