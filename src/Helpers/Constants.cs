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
using FundsManager.Helpers;

public class Constants
{
    public static readonly bool IS_DEV_ENVIRONMENT;
    // Features
    public static readonly bool ENABLE_REMOTE_SIGNER;
    public static readonly bool PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED;
    public static readonly bool ENABLE_HW_SUPPORT;
    public static readonly bool NBXPLORER_ENABLE_CUSTOM_BACKEND = false;

    // Connections
    public static readonly string POSTGRES_CONNECTIONSTRING = "Host=localhost;Port=5432;Database=fundsmanager;Username=rw_dev;Password=rw_dev";
    public static readonly string NBXPLORER_URI;
    public static readonly string? NBXPLORER_BTCRPCURL;
    public static readonly string ALICE_HOST;
    public static readonly string CAROL_HOST;
    public static readonly string BOB_HOST;
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

    // Crons & Jobs
    public static readonly string MONITOR_WITHDRAWALS_CRON = "10 0/5 * * * ?";
    public static readonly string MONITOR_CHANNELS_CRON = "0 0 0 * * ?";
    public static readonly string JOB_RETRY_INTERVAL_LIST_IN_MINUTES = "1,2,5,10,20";


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
    public static readonly string DEFAULT_DERIVATION_PATH = "48'/1'";
    public static readonly int SESSION_TIMEOUT_MILLISECONDS = 3_600_000;

    //Sat/vb ratio
    public static decimal MIN_SAT_PER_VB_RATIO = 0.9m;
    public static decimal MAX_SAT_PER_VB_RATIO = 2.0m;
    /// <summary>
    /// Max ratio of the tx total input sum that could be used as fee
    /// </summary>
    public static decimal MAX_TX_FEE_RATIO =0.5m;

    private static string? GetEnvironmentalVariableOrThrowIfNotTesting(string envVariableName, string? errorMessage = null)
    {
        // If it is a command from ef or a test, ignore the empty env variables
        var command = Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant();
        var ignoreMissingVar = command == "ef" || (command != null && command.Contains("test"));

        var envVariable = Environment.GetEnvironmentVariable(envVariableName);
        if (!ignoreMissingVar && envVariable == null) {
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

        // Connections
        POSTGRES_CONNECTIONSTRING =  Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") ?? POSTGRES_CONNECTIONSTRING;

        NBXPLORER_URI = GetEnvironmentalVariableOrThrowIfNotTesting("NBXPLORER_URI");

        NBXPLORER_BTCRPCURL = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCURL");

        ALICE_HOST = Environment.GetEnvironmentVariable("ALICE_HOST") ?? "host.docker.internal:10001";

        CAROL_HOST = Environment.GetEnvironmentVariable("CAROL_HOST") ?? "host.docker.internal:10003";

        BOB_HOST = Environment.GetEnvironmentVariable("BOB_HOST") ?? "host.docker.internal:10002";

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



        // Crons & Jobs
        MONITOR_WITHDRAWALS_CRON = Environment.GetEnvironmentVariable("MONITOR_WITHDRAWALS_CRON") ?? MONITOR_WITHDRAWALS_CRON;

        MONITOR_CHANNELS_CRON = Environment.GetEnvironmentVariable("MONITOR_CHANNELS_CRON") ?? MONITOR_CHANNELS_CRON;

        JOB_RETRY_INTERVAL_LIST_IN_MINUTES = Environment.GetEnvironmentVariable("JOB_RETRY_INTERVAL_LIST_IN_MINUTES") ?? JOB_RETRY_INTERVAL_LIST_IN_MINUTES;


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

        DEFAULT_DERIVATION_PATH = GetEnvironmentalVariableOrThrowIfNotTesting("DEFAULT_DERIVATION_PATH") ?? DEFAULT_DERIVATION_PATH;

        var timeout = Environment.GetEnvironmentVariable("SESSION_TIMEOUT_MILLISECONDS");
        if (timeout != null) SESSION_TIMEOUT_MILLISECONDS = int.Parse(timeout);

        //Sat/vb ratio
        var minSatPerVbRatioEnv = Environment.GetEnvironmentVariable("MIN_SAT_PER_VB_RATIO");
        MIN_SAT_PER_VB_RATIO = minSatPerVbRatioEnv!= null ? decimal.Parse(minSatPerVbRatioEnv) : MIN_SAT_PER_VB_RATIO;

        var maxSatPerVbRatioEnv = Environment.GetEnvironmentVariable("MAX_SAT_PER_VB_RATIO");
        MAX_SAT_PER_VB_RATIO = maxSatPerVbRatioEnv!= null ? decimal.Parse(maxSatPerVbRatioEnv) : MAX_SAT_PER_VB_RATIO;

    }
}

public class EnvironmentalVariableMissingException: ArgumentNullException
{
    public EnvironmentalVariableMissingException(string message): base(message + " must be set")
    {
    }
}
