using System.Text.Json;
using NBitcoin;
using NuGet.Protocol;

namespace NodeGuard.Services;

public interface IPriceConversionService
{
    public Task<decimal> GetBtcToUsdPrice();
    public decimal SatToUsdConversion(decimal sats, decimal btcPrice);
    public decimal BtcToUsdConversion(decimal btc, decimal btcPrice);
}
public class PriceConversionService: IPriceConversionService
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