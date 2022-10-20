namespace FundsManager.Helpers
{
    public class GRPCLoggerFactoryHelper
    {
        public static ILoggerFactory LoggerFactory()
        {
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddFilter("Grpc", LogLevel.Warning);
            });
            return loggerFactory;
        }
    }
}