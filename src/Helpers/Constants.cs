using System.Globalization;
using System.Reflection;

public class Constants
{
    public static readonly bool IS_DEV_ENVIRONMENT;
    // Features
    public static readonly bool ENABLE_REMOTE_SIGNER;
    public static readonly bool PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED;
    public static readonly bool ENABLE_HW_SUPPORT;

    // Connections
    public static readonly string POSTGRES_CONNECTIONSTRING = "Host=localhost;Port=5432;Database=fundsmanager;Username=rw_dev;Password=rw_dev";
    public static readonly string NBXPLORER_URI;
    public static readonly string? NBXPLORER_BTCRPCURL;
    public static readonly string ALICE_HOST;
    public static readonly string CAROL_HOST;
    public static readonly string? FUNDSMANAGER_ENDPOINT;
    public static readonly string? COINGECKO_ENDPOINT;
    public static readonly string? MEMPOOL_ENDPOINT;
    public static readonly string? REMOTE_SIGNER_ENDPOINT;

    // Credentials
    public static readonly string? NBXPLORER_BTCRPCUSER;
    public static readonly string? NBXPLORER_BTCRPCPASSWORD;
    public static readonly string? COINGECKO_KEY;
    public static readonly string PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID;
    public static readonly string? PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH;
    public static readonly string? PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN;
    public static readonly string? AWS_REGION;
    public static readonly string? AWS_ACCESS_KEY_ID;
    public static readonly string? AWS_SECRET_ACCESS_KEY;
    public static readonly string? AWS_KMS_KEY_ID;

    // Crons & Jobs
    public static readonly string MONITOR_WITHDRAWALS_CRON = "10 0/5 * * * ?";
    public static readonly string? JOB_RETRY_INTERVAL_LIST_IN_MINUTES;


    // Observability
    public static readonly string? OTEL_EXPORTER_ENDPOINT;
    
    // Usage
    public static readonly string? BITCOIN_NETWORK; 
    public static readonly long MINIMUM_CHANNEL_CAPACITY_SATS = 20_000;
    public static readonly decimal MINIMUM_WITHDRAWAL_BTC_AMOUNT = 0.0m;
    public static readonly decimal MAXIMUM_WITHDRAWAL_BTC_AMOUNT = 21_000_000;
    public static readonly int TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS;
    public static readonly long ANCHOR_CLOSINGS_MINIMUM_SATS;
    public static readonly string DEFAULT_DERIVATION_PATH;
    public static readonly int SESSION_TIMEOUT_MILLISECONDS = 3_600_000;


    static Constants()
    {
        // If it is a command from ef, ignore the empty env variables
        if (Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() == "ef") return;

        IS_DEV_ENVIRONMENT = Environment.GetEnvironmentVariable("IS_DEV_ENVIRONMENT") != null;
        // Features
        ENABLE_REMOTE_SIGNER = Environment.GetEnvironmentVariable("ENABLE_REMOTE_SIGNER") != null;

        PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED") != null;

        ENABLE_HW_SUPPORT = Environment.GetEnvironmentVariable("ENABLE_HW_SUPPORT") != "false"; // We default to true

        // Connections
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING");
        if (connectionString != null) POSTGRES_CONNECTIONSTRING = connectionString;

        NBXPLORER_URI = Environment.GetEnvironmentVariable("NBXPLORER_URI") ?? throw new ArgumentNullException("NBXPLORER_URI env variable must be set");

        NBXPLORER_BTCRPCURL = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCURL");

        ALICE_HOST = Environment.GetEnvironmentVariable("ALICE_HOST") ?? "host.docker.internal:10001";

        CAROL_HOST = Environment.GetEnvironmentVariable("CAROL_HOST") ?? "host.docker.internal:10003";

        FUNDSMANAGER_ENDPOINT = Environment.GetEnvironmentVariable("FUNDSMANAGER_ENDPOINT");

        COINGECKO_ENDPOINT = Environment.GetEnvironmentVariable("COINGECKO_ENDPOINT");
        
        MEMPOOL_ENDPOINT = Environment.GetEnvironmentVariable("MEMPOOL_ENDPOINT");

        // Credentials
        NBXPLORER_BTCRPCUSER = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCUSER");

        NBXPLORER_BTCRPCPASSWORD = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCPASSWORD");

        COINGECKO_KEY = Environment.GetEnvironmentVariable("COINGECKO_KEY");

        if (PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED)
        {
            PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID") 
                ?? throw new ArgumentNullException("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED set but PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID not set");

            PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH")
                ?? throw new ArgumentNullException("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED set but PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH not set");

            PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN")
                ?? throw new ArgumentNullException("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED set but PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN not set");

            var _check = Constants.FUNDSMANAGER_ENDPOINT ?? throw new ArgumentNullException("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED set but FUNDSMANAGER_ENDPOINT not set");
        }

        if (ENABLE_REMOTE_SIGNER)
        {
            AWS_REGION = Environment.GetEnvironmentVariable("AWS_REGION") ?? throw new ArgumentNullException("ENABLE_REMOTE_SIGNER set but AWS_REGION is not set");

            AWS_ACCESS_KEY_ID = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? throw new ArgumentNullException("ENABLE_REMOTE_SIGNER set but AWS_ACCESS_KEY_ID is not set");

            AWS_SECRET_ACCESS_KEY = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? throw new ArgumentNullException("ENABLE_REMOTE_SIGNER set but AWS_SECRET_ACCESS_KEY is not set");

            AWS_KMS_KEY_ID = Environment.GetEnvironmentVariable("AWS_KMS_KEY_ID") ?? throw new ArgumentNullException("ENABLE_REMOTE_SIGNER set but AWS_KMS_KEY_ID is not set");

            AWS_KMS_KEY_ID = Environment.GetEnvironmentVariable("REMOTE_SIGNER_ENDPOINT") ?? throw new ArgumentNullException("ENABLE_REMOTE_SIGNER set but REMOTE_SIGNER_ENDPOINT is not set");
        }

        

        // Crons & Jobs
        var cronExpression = Environment.GetEnvironmentVariable("MONITOR_WITHDRAWALS_CRON");
        if (cronExpression != null) MONITOR_WITHDRAWALS_CRON = cronExpression;

        JOB_RETRY_INTERVAL_LIST_IN_MINUTES = Environment.GetEnvironmentVariable("JOB_RETRY_INTERVAL_LIST_IN_MINUTES");

        // Observability
        //We need to expand the env-var with %ENV_VAR% for K8S
        var otelCollectorEndpointToBeExpanded = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (otelCollectorEndpointToBeExpanded != null)
        {
            var otelCollectorEndpoint = Environment.ExpandEnvironmentVariables(otelCollectorEndpointToBeExpanded);

            if (!string.IsNullOrEmpty(otelCollectorEndpoint))
            {
                const string otelResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";
                var expandedResourceAttributes = Environment.ExpandEnvironmentVariables(
                    Environment.GetEnvironmentVariable(otelResourceAttributes)
                    ?? throw new ArgumentNullException("both OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_RESOURCE_ATTRIBUTES must be set"));
                Environment.SetEnvironmentVariable(otelResourceAttributes, expandedResourceAttributes);
                OTEL_EXPORTER_ENDPOINT = otelCollectorEndpoint;
            }
        }

        // Usage
        BITCOIN_NETWORK = Environment.GetEnvironmentVariable("BITCOIN_NETWORK");

        var maxChannelCapacity = Environment.GetEnvironmentVariable("MINIMUM_CHANNEL_CAPACITY_SATS") ?? throw new ArgumentNullException("MINIMUM_CHANNEL_CAPACITY_SATS must be set");
        MINIMUM_CHANNEL_CAPACITY_SATS = long.Parse(maxChannelCapacity, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var environmentVariableMin = Environment.GetEnvironmentVariable("MINIMUM_WITHDRAWAL_BTC_AMOUNT");
        if (environmentVariableMin != null) {
            MINIMUM_WITHDRAWAL_BTC_AMOUNT = decimal.Parse(environmentVariableMin, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        var environmentVariableMax = Environment.GetEnvironmentVariable("MAXIMUM_WITHDRAWAL_BTC_AMOUNT");
        if (environmentVariableMax != null) {
            MAXIMUM_WITHDRAWAL_BTC_AMOUNT = decimal.Parse(environmentVariableMax, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS =
                        int.Parse(Environment.GetEnvironmentVariable("TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS") ??
                                  throw new ArgumentNullException("TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS must be set"));

        ANCHOR_CLOSINGS_MINIMUM_SATS = long.Parse(Environment.GetEnvironmentVariable("ANCHOR_CLOSINGS_MINIMUM_SATS") ??
                       throw new ArgumentNullException("ANCHOR_CLOSINGS_MINIMUM_SATS must be set")); // Check https://github.com/lightningnetwork/lnd/issues/6505#issuecomment-1120364460 to understand, we need 100K+ to support anchor channel closings

        DEFAULT_DERIVATION_PATH = Environment.GetEnvironmentVariable("DEFAULT_DERIVATION_PATH") ?? throw new ArgumentNullException("DEFAULT_DERIVATION_PATH must be set");

        var timeout = Environment.GetEnvironmentVariable("SESSION_TIMEOUT_MILLISECONDS");
        if (timeout != null) SESSION_TIMEOUT_MILLISECONDS = int.Parse(timeout);
    }
}