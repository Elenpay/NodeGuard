using NBitcoin;

namespace FundsManager.Helpers
{
    public static class CurrentNetworkHelper
    {
        /// <summary>
        /// Gets the current settings for the system network
        /// </summary>
        /// <returns></returns>
        public static Network GetCurrentNetwork()
        {
            var network = Environment.GetEnvironmentVariable("BITCOIN_NETWORK")?.ToUpper();

            var result = network switch
            {
                "REGTEST" => Network.RegTest,
                "MAINNET" => Network.Main,
                "TESTNET" => Network.TestNet,
                _ => Network.RegTest
            };
            return result;
        }
    }
}