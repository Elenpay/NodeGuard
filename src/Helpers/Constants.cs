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
using System.Globalization;
using System.Reflection;
using NBitcoin;
using NodeGuard.Helpers;

public class Constants
{
    public static readonly bool IS_DEV_ENVIRONMENT;
    // Features
    public static readonly bool ENABLE_REMOTE_SIGNER;
    public static readonly bool PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED;
    public static readonly bool ENABLE_HW_SUPPORT;
    public static readonly bool NBXPLORER_ENABLE_CUSTOM_BACKEND = false;
    /// <summary>
    /// Allow simultaneous channel opening operations using the same source and destination nodes
    /// </summary>
    public static bool ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS; // Not readonly so we can change it in tests

    // Connections
    public static readonly string POSTGRES_CONNECTIONSTRING = "Host=localhost;Port=25432;Database=nodeguard;Username=rw_dev;Password=rw_dev";
    public static readonly string NBXPLORER_URI;
    public static readonly string? NBXPLORER_BTCRPCURL;
    public static readonly string? FUNDSMANAGER_ENDPOINT;
    public static readonly string? COINGECKO_ENDPOINT;
    public static readonly string? MEMPOOL_ENDPOINT;
    public static readonly string? AMBOSS_ENDPOINT;
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
    public static readonly string API_TOKEN_SALT;

    // Crons & Jobs
    public static readonly string MONITOR_WITHDRAWALS_CRON = "10 0/5 * * * ?";
    public static readonly string MONITOR_CHANNELS_CRON = "0 0 */1 * * ?";
    public static readonly string JOB_RETRY_INTERVAL_LIST_IN_MINUTES = "1,2,5,10,20";
    /// <summary>
    /// The interval in minutes for the SweepAllNodesWalletsJob to run. 
    /// This job sweeps funds from all lightning node wallets. Default is 15 minutes.
    /// Can be configured via SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES environment variable.
    /// </summary>
    public static readonly int SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES = 15;

    // Observability
    public static readonly string? OTEL_EXPORTER_ENDPOINT;

    // Usage
    public static readonly string BITCOIN_NETWORK;
    public static readonly long MINIMUM_CHANNEL_CAPACITY_SATS = 20_000;
    public static readonly long MAXIMUM_CHANNEL_CAPACITY_SATS_REGTEST = 16_777_215;
    public static readonly decimal MINIMUM_WITHDRAWAL_BTC_AMOUNT = 0.0m;
    public static readonly decimal MAXIMUM_WITHDRAWAL_BTC_AMOUNT = 21_000_000;
    public static readonly int TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS;
    public static readonly long ANCHOR_CLOSINGS_MINIMUM_SATS;
    public static readonly long MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS = 25_000_000; //25M sats
    public static readonly string DEFAULT_DERIVATION_PATH = "48'/1'";
    public static readonly int SESSION_TIMEOUT_MILLISECONDS = 3_600_000;
    public static readonly Money BITCOIN_DUST = new Money(0.00000546m, MoneyUnit.BTC); // 546 satoshi in BTC

    //Sat/vb ratio
    public static decimal MIN_SAT_PER_VB_RATIO = 0.9m;
    public static decimal MAX_SAT_PER_VB_RATIO = 2.0m;
    /// <summary>
    /// Max ratio of the tx total input sum that could be used as fee
    /// </summary>
    public static decimal MAX_TX_FEE_RATIO = 0.5m;

    /// <summary>
    /// The target number of confirmations blocks (fee rate) for the sweep transaction
    /// </summary>
    public static int SWEEP_CONF_TARGET = 6;

    public const string IsFrozenTag = "frozen";
    public const string IsManuallyFrozenTag = "manually_frozen";

    //  Constants for the NBXplorer API
    public static int SCAN_GAP_LIMIT = 1000;
    public static int SCAN_BATCH_SIZE = 1000;

    // DB Migration
    public static readonly string ALICE_PUBKEY = "02dc2ae598a02fc1e9709a23b68cd51d7fa14b1132295a4d75aa4f5acd23ee9527";
    public static readonly string ALICE_HOST = "host.docker.internal:10001";
    public static readonly string ALICE_MACAROON = "0201036c6e6402f801030a108cdfeb2614b8335c11aebb358f888d6d1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620c999e1a30842cbae3f79bd633b19d5ec0d2b6ebdc4880f6f5d5c230ce38f26ab";
    public static readonly string BOB_PUBKEY = "038644c6b13cdfc59bc97c2cc2b1418ced78f6d01da94f3bfd5fdf8b197335ea84";
    public static readonly string BOB_HOST = "host.docker.internal:10002";
    public static readonly string BOB_MACAROON = "0201036c6e6402f801030a10e0e89a68f9e2398228a995890637d2531201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620b85ae6b693338987cd65eda60a24573e962301b2a91d8f7c5625650d6368751f";
    public static readonly string CAROL_PUBKEY = "03650f49929d84d9a6d9b5a66235c603a1a0597dd609f7cd3b15052382cf9bb1b4";
    public static readonly string CAROL_HOST = "host.docker.internal:10003";
    public static readonly string CAROL_MACAROON = "0201036c6e6402f801030a101ec5b6370c166f6c8e2853164109145a1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e6572617465120472656164000006208e957e78ec39e7810fad25cfc43850b8e9e7c079843b8ec7bb5522bba12230d6";


    private static string? GetEnvironmentalVariableOrThrowIfNotTesting(string envVariableName, string? errorMessage = null)
    {
        // If it is a command from ef or a test, ignore the empty env variables
        var command = Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant();
        var ignoreMissingVar = command == "ef" || (command != null && command.Contains("test"));

        var envVariable = Environment.GetEnvironmentVariable(envVariableName);
        if (!ignoreMissingVar && envVariable == null)
        {
            throw new EnvironmentalVariableMissingException(errorMessage ?? envVariableName);
        }
        return envVariable;
    }

    static Constants()
    {
        IS_DEV_ENVIRONMENT = StringHelper.IsTrue(Environment.GetEnvironmentVariable("IS_DEV_ENVIRONMENT"));
        // Features
        ENABLE_REMOTE_SIGNER = StringHelper.IsTrue(Environment.GetEnvironmentVariable("ENABLE_REMOTE_SIGNER"));

        PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED = StringHelper.IsTrue(Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED"));

        ENABLE_HW_SUPPORT = Environment.GetEnvironmentVariable("ENABLE_HW_SUPPORT") != "false"; // We default to true

        NBXPLORER_ENABLE_CUSTOM_BACKEND = Environment.GetEnvironmentVariable("NBXPLORER_ENABLE_CUSTOM_BACKEND") == "true";

        ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS = Environment.GetEnvironmentVariable("ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS") == "true";

        // Connections
        POSTGRES_CONNECTIONSTRING = Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") ?? POSTGRES_CONNECTIONSTRING;

        NBXPLORER_URI = GetEnvironmentalVariableOrThrowIfNotTesting("NBXPLORER_URI");

        NBXPLORER_BTCRPCURL = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCURL");

        FUNDSMANAGER_ENDPOINT = Environment.GetEnvironmentVariable("FUNDSMANAGER_ENDPOINT");

        COINGECKO_ENDPOINT = Environment.GetEnvironmentVariable("COINGECKO_ENDPOINT");

        MEMPOOL_ENDPOINT = Environment.GetEnvironmentVariable("MEMPOOL_ENDPOINT");

        AMBOSS_ENDPOINT = Environment.GetEnvironmentVariable("AMBOSS_ENDPOINT");

        // Credentials
        NBXPLORER_BTCRPCUSER = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCUSER");

        NBXPLORER_BTCRPCPASSWORD = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCPASSWORD");

        COINGECKO_KEY = Environment.GetEnvironmentVariable("COINGECKO_KEY");

        if (PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED)
        {
            PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID = GetEnvironmentalVariableOrThrowIfNotTesting("PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID", "if PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED is set, PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID");

            PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH = GetEnvironmentalVariableOrThrowIfNotTesting("PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH", "if PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED is set,PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH");

            PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN = GetEnvironmentalVariableOrThrowIfNotTesting("PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN", "if PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED is set, PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN");

            var _check = GetEnvironmentalVariableOrThrowIfNotTesting("FUNDSMANAGER_ENDPOINT", "if PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED is set, FUNDSMANAGER_ENDPOINT");
        }

        if (ENABLE_REMOTE_SIGNER)
        {
            AWS_REGION = GetEnvironmentalVariableOrThrowIfNotTesting("AWS_REGION", "if ENABLE_REMOTE_SIGNER is set, AWS_REGION");

            AWS_ACCESS_KEY_ID = GetEnvironmentalVariableOrThrowIfNotTesting("AWS_ACCESS_KEY_ID", "if ENABLE_REMOTE_SIGNER is set, AWS_ACCESS_KEY_ID");

            AWS_SECRET_ACCESS_KEY = GetEnvironmentalVariableOrThrowIfNotTesting("AWS_SECRET_ACCESS_KEY", "if ENABLE_REMOTE_SIGNER is set, AWS_SECRET_ACCESS_KEY");

            REMOTE_SIGNER_ENDPOINT = GetEnvironmentalVariableOrThrowIfNotTesting("REMOTE_SIGNER_ENDPOINT", "if ENABLE_REMOTE_SIGNER is set, REMOTE_SIGNER_ENDPOINT");
        }

        API_TOKEN_SALT = Environment.GetEnvironmentVariable("API_TOKEN_SALT") ?? "H/fCx1+maAFMcdi6idIYEg==";

        // Crons & Jobs
        MONITOR_WITHDRAWALS_CRON = Environment.GetEnvironmentVariable("MONITOR_WITHDRAWALS_CRON") ?? MONITOR_WITHDRAWALS_CRON;

        MONITOR_CHANNELS_CRON = Environment.GetEnvironmentVariable("MONITOR_CHANNELS_CRON") ?? MONITOR_CHANNELS_CRON;

        JOB_RETRY_INTERVAL_LIST_IN_MINUTES = Environment.GetEnvironmentVariable("JOB_RETRY_INTERVAL_LIST_IN_MINUTES") ?? JOB_RETRY_INTERVAL_LIST_IN_MINUTES;

        var sweepIntervalMinutes = Environment.GetEnvironmentVariable("SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES");
        if (sweepIntervalMinutes != null) SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES = int.Parse(sweepIntervalMinutes);


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
                    GetEnvironmentalVariableOrThrowIfNotTesting(otelResourceAttributes, "both OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_RESOURCE_ATTRIBUTES")!
                );
                Environment.SetEnvironmentVariable(otelResourceAttributes, expandedResourceAttributes);
                OTEL_EXPORTER_ENDPOINT = otelCollectorEndpoint;
            }
        }

        // Usage
        BITCOIN_NETWORK = Environment.GetEnvironmentVariable("BITCOIN_NETWORK");

        var minChannelCapacity = GetEnvironmentalVariableOrThrowIfNotTesting("MINIMUM_CHANNEL_CAPACITY_SATS");
        if (minChannelCapacity != null) MINIMUM_CHANNEL_CAPACITY_SATS = long.Parse(minChannelCapacity, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var environmentVariableMin = Environment.GetEnvironmentVariable("MINIMUM_WITHDRAWAL_BTC_AMOUNT");
        if (environmentVariableMin != null) MINIMUM_WITHDRAWAL_BTC_AMOUNT = decimal.Parse(environmentVariableMin, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var environmentVariableMax = Environment.GetEnvironmentVariable("MAXIMUM_WITHDRAWAL_BTC_AMOUNT");
        if (environmentVariableMax != null) MAXIMUM_WITHDRAWAL_BTC_AMOUNT = decimal.Parse(environmentVariableMax, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var transactionConfBlocks = GetEnvironmentalVariableOrThrowIfNotTesting("TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS");
        if (transactionConfBlocks != null) TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS = int.Parse(transactionConfBlocks);

        var anchorClosingMinSats = GetEnvironmentalVariableOrThrowIfNotTesting("ANCHOR_CLOSINGS_MINIMUM_SATS");
        if (anchorClosingMinSats != null) ANCHOR_CLOSINGS_MINIMUM_SATS = long.Parse(anchorClosingMinSats); // Check https://github.com/lightningnetwork/lnd/issues/6505#issuecomment-1120364460 to understand, we need 100K+ to support anchor channel closings

        var sweepConfTarget = Environment.GetEnvironmentVariable("SWEEP_CONF_TARGET");
        if (sweepConfTarget != null) SWEEP_CONF_TARGET = int.Parse(sweepConfTarget);

        var minSweepTransactionAmount = Environment.GetEnvironmentVariable("MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS");
        if (minSweepTransactionAmount != null) MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS = long.Parse(minSweepTransactionAmount);


        DEFAULT_DERIVATION_PATH = GetEnvironmentalVariableOrThrowIfNotTesting("DEFAULT_DERIVATION_PATH") ?? DEFAULT_DERIVATION_PATH;

        var timeout = Environment.GetEnvironmentVariable("SESSION_TIMEOUT_MILLISECONDS");
        if (timeout != null) SESSION_TIMEOUT_MILLISECONDS = int.Parse(timeout);

        //Sat/vb ratio
        var minSatPerVbRatioEnv = Environment.GetEnvironmentVariable("MIN_SAT_PER_VB_RATIO");
        MIN_SAT_PER_VB_RATIO = minSatPerVbRatioEnv != null ? decimal.Parse(minSatPerVbRatioEnv) : MIN_SAT_PER_VB_RATIO;

        var maxSatPerVbRatioEnv = Environment.GetEnvironmentVariable("MAX_SAT_PER_VB_RATIO");
        MAX_SAT_PER_VB_RATIO = maxSatPerVbRatioEnv != null ? decimal.Parse(maxSatPerVbRatioEnv) : MAX_SAT_PER_VB_RATIO;

        //NBXplorer scan
        var scanGapLimit = Environment.GetEnvironmentVariable("SCAN_GAP_LIMIT");
        SCAN_GAP_LIMIT = scanGapLimit != null ? int.Parse(scanGapLimit) : SCAN_GAP_LIMIT;

        var scanBatchSize = Environment.GetEnvironmentVariable("SCAN_BATCH_SIZE");
        SCAN_BATCH_SIZE = scanBatchSize != null ? int.Parse(scanBatchSize) : SCAN_BATCH_SIZE;

        // DB Initialization
        ALICE_PUBKEY = Environment.GetEnvironmentVariable("ALICE_PUBKEY") ?? ALICE_PUBKEY;
        ALICE_HOST = Environment.GetEnvironmentVariable("ALICE_HOST") ?? ALICE_HOST;
        ALICE_MACAROON = Environment.GetEnvironmentVariable("ALICE_MACAROON") ?? ALICE_MACAROON;

        BOB_PUBKEY = Environment.GetEnvironmentVariable("BOB_PUBKEY") ?? BOB_PUBKEY;
        BOB_HOST = Environment.GetEnvironmentVariable("BOB_HOST") ?? BOB_HOST;
        BOB_MACAROON = Environment.GetEnvironmentVariable("BOB_MACAROON") ?? BOB_MACAROON;

        CAROL_PUBKEY = Environment.GetEnvironmentVariable("CAROL_PUBKEY") ?? CAROL_PUBKEY;
        CAROL_HOST = Environment.GetEnvironmentVariable("CAROL_HOST") ?? CAROL_HOST;
        CAROL_MACAROON = Environment.GetEnvironmentVariable("CAROL_MACAROON") ?? CAROL_MACAROON;
    }

}

public class EnvironmentalVariableMissingException : ArgumentNullException
{
    public EnvironmentalVariableMissingException(string message) : base(message + " must be set")
    {
    }
}
