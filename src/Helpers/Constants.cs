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
    public static bool NBXPLORER_ENABLE_CUSTOM_BACKEND = false; // Not readonly so we can change it in tests
    /// <summary>
    /// Allow simultaneous channel opening operations using the same source and destination nodes
    /// </summary>
    public static bool ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS; // Not readonly so we can change it in tests

    // Connections
    public static readonly string POSTGRES_CONNECTIONSTRING = "Host=localhost;Port=25432;Database=nodeguard;User ID=postgres;";
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
    public static string MONITOR_REBALANCES_CRON = "30 0/5 * * * ?";
    public static readonly string JOB_RETRY_INTERVAL_LIST_IN_MINUTES = "1,2,5,10,20";
    /// <summary>
    /// The interval in minutes for the SweepAllNodesWalletsJob to run. 
    /// This job sweeps funds from all lightning node wallets. Default is 15 minutes.
    /// Can be configured via SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES environment variable.
    /// </summary>
    public static readonly int SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES = 15;

    /// <summary>
    /// The interval in minutes for the AutoLiquidityManagementJob to run.
    /// This job monitors node balances and manages liquidity automatically (swap outs, swap ins, channels). Default is 10 minutes.
    /// Can be configured via AUTO_LIQUIDITY_MANAGEMENT_INTERVAL_MINUTES environment variable.
    /// </summary>
    public static readonly int AUTO_LIQUIDITY_MANAGEMENT_INTERVAL_MINUTES = 10;

    /// <summary>
    /// The number of days to retain audit log entries before automatic cleanup.
    /// Default is 180 days. Can be configured via AUDIT_LOG_RETENTION_DAYS environment variable.
    /// </summary>
    public static readonly int AUDIT_LOG_RETENTION_DAYS = 180;

    /// <summary>
    /// Cron expression for the audit log cleanup job. Default runs daily at 3:00 AM.
    /// Can be configured via AUDIT_LOG_CLEANUP_CRON environment variable.
    /// </summary>
    public static readonly string AUDIT_LOG_CLEANUP_CRON = "0 0 3 * * ?";

    // Observability
    public static readonly string? OTEL_EXPORTER_ENDPOINT;

    // Usage
    public static readonly string BITCOIN_NETWORK;
    public static readonly long MINIMUM_CHANNEL_CAPACITY_SATS = 20_000;
    public static readonly long MAXIMUM_CHANNEL_CAPACITY_SATS_REGTEST = 16_777_215;
    public static readonly decimal MINIMUM_WITHDRAWAL_BTC_AMOUNT = 0.0m;
    public static readonly decimal MAXIMUM_WITHDRAWAL_BTC_AMOUNT = 21_000_000;
    public static readonly int TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS;
    public static int DEFAULT_CHANNEL_FEE_POLICY_TIMELOCK_DELTA_BLOCKS = 40;
    public static long DEFAULT_CHANNEL_FEE_POLICY_BASE_FEE_MSAT = 0; 
    public static long DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM = 1500;
    public static readonly long ANCHOR_CLOSINGS_MINIMUM_SATS;
    public static readonly long MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS = 25_000_000; //25M sats
    public static readonly string DEFAULT_DERIVATION_PATH = "48'/1'";
    public static readonly int SESSION_TIMEOUT_MILLISECONDS = 3_600_000;
    public static readonly Money BITCOIN_DUST = new Money(0.00000546m, MoneyUnit.BTC); // 546 satoshi in BTC

    /// <summary>
    /// UTXOs with value less than or equal to this are excluded from coin selection (dust-attack protection).
    /// </summary>
    public static readonly long MINIMUM_UTXO_VALUE_SATS = 546;

    /// <summary>
    /// Minimum swap out size in BTC for automatic liquidity management (Swap Out).
    /// Can be configured via MINIMUM_SWAP_OUT_SIZE_BTC environment variable.
    /// </summary>
    public static decimal MINIMUM_SWAP_OUT_SIZE_BTC = 0.01m;

    /// <summary>
    /// Maximum swap out size in BTC for automatic liquidity management (Swap Out).
    /// Can be configured via MAXIMUM_SWAP_OUT_SIZE_BTC environment variable.
    /// </summary>
    public static decimal MAXIMUM_SWAP_OUT_SIZE_BTC = 0.5m;

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

    /// <summary>
    /// Maximum miner fees in satoshis for automatic swap operations
    /// </summary>
    public static long SWAP_MAX_MINER_FEES_SATS = 15000;

    /// <summary>
    /// Maximum service fees as a percentage for automatic swap operations
    /// </summary>
    public static decimal SWAP_MAX_SERVICE_FEES_PERCENT = 0.1m;

    /// <summary>
    /// Prepay amount in satoshis for automatic swap operations
    /// </summary>
    public static long SWAP_PREPAY_AMOUNT_SATS = 1337;

    // Rebalance configuration
    /// <summary>
    /// Default max fee cap for an initial rebalance attempt, expressed as a percentage of
    /// the rebalanced amount. ppm = pct × 10,000. 0.05 = 0.05% = 500 ppm.
    /// </summary>
    public static decimal REBALANCE_DEFAULT_MAX_FEE_PCT = 0.05m;

    /// <summary>
    /// Default max fee cap for retry attempts, expressed as a percentage of the rebalanced
    /// amount. ppm = pct × 10,000. 0.075 = 0.075% = 750 ppm. More aggressive than the
    /// initial cap to improve the chance of finding a route on retry.
    /// </summary>
    public static decimal REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT = 0.05m;

    /// <summary>
    /// Maximum number of attempts for a rebalance, including the first try.
    /// </summary>
    public static int REBALANCE_MAX_ATTEMPTS = 3;

    /// <summary>
    /// Delay before the first retry attempt, in seconds. Subsequent attempts use
    /// REBALANCE_RETRY_BACKOFF_MULTIPLIER for exponential backoff.
    /// </summary>
    public static int REBALANCE_INITIAL_RETRY_DELAY_SECONDS = 60;

    /// <summary>
    /// Multiplier applied to the retry delay for each subsequent retry. With the default
    /// 2.0 and an initial delay of 60s, retries fire at 60s, 120s, 240s, ...
    /// </summary>
    public static double REBALANCE_RETRY_BACKOFF_MULTIPLIER = 2.0;

    /// <summary>
    /// Smallest amount a rebalance attempt will shrink down to before giving up (the floor
    /// for the per-attempt amount backoff).
    /// </summary>
    public static long REBALANCE_MIN_AMOUNT_SATS = 10_000;

    /// <summary>
    /// Multiplier applied to the rebalanced amount on each retry attempt. Range: (0, 1].
    /// 1 = never shrink (every attempt retries the full requested amount); 0.8 = each retry
    /// is 20% smaller than the previous attempt (partial rebalancing); 0.5 = halve each time.
    /// The amount for attempt n is RequestedAmountSats × ratio^(n-1), floored at
    /// REBALANCE_MIN_AMOUNT_SATS.
    /// </summary>
    public static double REBALANCE_AMOUNT_BACKOFF_RATIO = 0.8;

    /// <summary>
    /// Maximum number of partial payments (MPP shards) LND may split a rebalance into. 1
    /// disables splitting; higher values let LND complete an amount across several routes.
    /// </summary>
    public static uint REBALANCE_MAX_PARTS = 32;

    /// <summary>
    /// How far back the MonitorRebalancesJob sweeps for already-terminal-but-possibly-wrong
    /// rebalances. Caps the window during which a Failed/Timeout/NoRoute row can still be
    /// corrected by LND truth — old rows are left alone.
    /// </summary>
    public static int REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS = 24;

    // ── Routing Engine (heuristic routing-optimization engine) ──────────────────────────

    /// <summary>
    /// Global kill switch for the whole routing engine. Every routing-engine job checks this
    /// first and returns immediately when false.
    /// </summary>
    public static bool ROUTING_ENGINE_ENABLED = false;

    /// <summary>
    /// Age gate for categorization: channels younger than this many blocks stay Uncategorized
    /// at target 0.5. Default 3024 = 21 block-days.
    /// </summary>
    public static uint ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 3024;

    /// <summary>
    /// Categorization + net-flow lookback window over ForwardingHtlcEvent, in days.
    /// </summary>
    public static int ROUTING_ENGINE_FLOW_WINDOW_DAYS = 21;

    /// <summary>
    /// Minimum total in-window flow (push+pull) in msat required to categorize a channel; below
    /// this it stays / decays to Uncategorized.
    /// </summary>
    public static long ROUTING_ENGINE_FLOW_MIN_MSAT = 10_000_000_000;

    /// <summary>
    /// |NetFlowRatio| beyond this classifies a channel as Sink (positive) or Source (negative);
    /// inside the band it is Bidirectional.
    /// </summary>
    public static double ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;

    /// <summary>
    /// Proportional gain mapping net-flow to target drift: target_goal = 0.5 + clamp(K·netFlowRatio, ±maxDrift). Simply, how far the target drifts from 0.5 (default) per unit of flow imbalance.
    /// </summary>
    public static double ROUTING_ENGINE_TARGET_K = 0.70;

    /// <summary>
    /// Maximum drift of target_goal away from 0.5 (reached at |netFlowRatio| = 0.5). Default 0.35,
    /// giving a target_goal range of [0.15, 0.85].
    /// </summary>
    public static double ROUTING_ENGINE_TARGET_MAX_DRIFT = 0.35;

    /// <summary>
    /// EWMA smoothing factor folding target_goal into the stored TargetLocalRatio. Default 0.10
    /// (19 cycles to converge). ⍺ = 2/(cycles+1). This smooths out the target ratio to avoid
    /// overreacting to transient flow spikes.
    /// </summary>
    public static double ROUTING_ENGINE_TARGET_ALPHA = 0.10;

    /// <summary>
    /// EWMA smoothing factor for EmaLocalRatio (~24h effective window at 30-min sampling).
    /// Default 0.04 (49 cycles to converge). ⍺ = 2/(cycles+1). It smooths out the observed
    /// local ratio to avoid overreacting to transient flow spikes.
    /// </summary>
    public static double ROUTING_ENGINE_FEE_EMA_ALPHA = 0.04;

    /// <summary>
    /// Consecutive divergent cycles required before a category flip commits (anti-flap hysteresis).
    /// </summary>
    public static int ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 3;

    /// <summary>
    /// Cadence of TargetRatioReevaluationJob and ChannelFeeOptimizerJob in prod, in minutes. Default 30. In dev
    /// (IS_DEV_ENVIRONMENT) the job runs every 5 minutes regardless.
    /// </summary>
    public static int ROUTING_ENGINE_JOB_INTERVAL_MINUTES = 30;

    // Both fees use integral control: each cycle the applied value is nudged by gain·deviation·baseline
    // off its previous value, so a persistent deviation keeps driving the fee until the channel balances.
    public static double ROUTING_ENGINE_FEE_OUTBOUND_INTEGRAL_GAIN = 0.8;
    public static double ROUTING_ENGINE_FEE_INBOUND_INTEGRAL_GAIN = 0.5;

    // Fee deadband: when |EmaLocalRatio − TargetLocalRatio| ≤ this, the optimizer does nothing (NoOp),
    // so it never reacts to tiny imbalance.
    public static double ROUTING_ENGINE_FEE_DEADBAND = 0.03;

    // The rebalancer's imbalance deadband is a separate, more aggressive threshold for triggering rebalances.
    public static double ROUTING_ENGINE_REBALANCE_TRIGGER = 0.15;

    // Max outbound ppm change applied in a single cycle (rate limiter / anti-jump).
    public static uint ROUTING_ENGINE_FEE_MAX_STEP_PPM = 50;

    // Max inbound ppm change applied in a single cycle (rate limiter / anti-jump).
    public static uint ROUTING_ENGINE_FEE_MAX_INBOUND_STEP_PPM = 50;

    // Min ppm delta worth writing: a computed change smaller than this is dropped to NoOp, avoiding
    // churny sub-threshold LND fee updates.
    public static uint ROUTING_ENGINE_FEE_MIN_DELTA_PPM = 5;

    // Lower clamp (floor) on outbound ppm.
    public static uint ROUTING_ENGINE_FEE_MIN_OUTBOUND_PPM = 0;

    // Upper clamp (ceiling) on outbound ppm.
    public static uint ROUTING_ENGINE_FEE_MAX_OUTBOUND_PPM = 3000;

    // Most-negative inbound ppm allowed — a discount to attract inbound routing.
    public static int ROUTING_ENGINE_FEE_MIN_INBOUND_PPM = -2000;

    // Most-positive inbound ppm allowed — a surcharge to repel inbound routing.
    public static int ROUTING_ENGINE_FEE_MAX_INBOUND_PPM = 1000;

    // Eligibility gate: only channels with capacity ≥ this (sats) are fee-managed.
    public static long ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS = 10_000_000;

    // Category baselines: each both scales that cycle's nudge AND seeds the "last applied" ppm on a
    // channel's first evaluation, so a freshly categorized channel starts near its category's baseline
    // rather than crawling up from the operator's pre-engine fee.

    // Outbound ppm baseline for Source channels: cheap outbound to drain surplus local liquidity.
    public static uint ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE = 50;
    // Outbound ppm baseline for Bidirectional channels (mid).
    public static uint ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL = 1500;
    // Outbound ppm baseline for Sink channels: expensive outbound to protect scarce local liquidity.
    public static uint ROUTING_ENGINE_FEE_BASELINE_PPM_SINK = 2500;
    // Outbound ppm baseline for not-yet-categorized channels (safe mid default).
    public static uint ROUTING_ENGINE_FEE_BASELINE_PPM_UNCATEGORIZED = 1500;

    public const string IsFrozenTag = "frozen";
    public const string IsManuallyFrozenTag = "manually_frozen";

    //  Constants for the NBXplorer API
    public static int SCAN_GAP_LIMIT = 1000;
    public static int SCAN_BATCH_SIZE = 1000;

    // DB Migration
    public static readonly string ALICE_PUBKEY = string.Empty;
    public static readonly string ALICE_HOST = string.Empty;
    public static readonly string ALICE_MACAROON = string.Empty;
    public static readonly string BOB_PUBKEY = string.Empty;
    public static readonly string BOB_HOST = string.Empty;
    public static readonly string BOB_MACAROON = string.Empty;
    public static readonly string BOB_LOOPD_HOST = string.Empty;
    public static readonly string BOB_LOOPD_MACAROON = string.Empty;
    public static readonly string BOB_LOOPD_TLS_CERT = string.Empty;

    public static readonly string CAROL_PUBKEY = string.Empty;
    public static readonly string CAROL_HOST = string.Empty;
    public static readonly string CAROL_MACAROON = string.Empty;
    public static readonly string CAROL_LOOPD_HOST = string.Empty;
    public static readonly string CAROL_LOOPD_MACAROON = string.Empty;
    public static readonly string CAROL_LOOPD_TLS_CERT = string.Empty;


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

        MONITOR_REBALANCES_CRON = Environment.GetEnvironmentVariable("MONITOR_REBALANCES_CRON") ?? MONITOR_REBALANCES_CRON;

        JOB_RETRY_INTERVAL_LIST_IN_MINUTES = Environment.GetEnvironmentVariable("JOB_RETRY_INTERVAL_LIST_IN_MINUTES") ?? JOB_RETRY_INTERVAL_LIST_IN_MINUTES;

        var sweepIntervalMinutes = Environment.GetEnvironmentVariable("SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES");
        if (sweepIntervalMinutes != null) SWEEP_ALL_NODES_WALLETS_INTERVAL_MINUTES = int.Parse(sweepIntervalMinutes);

        var autoLiquidityManagementIntervalMinutes = Environment.GetEnvironmentVariable("AUTO_LIQUIDITY_MANAGEMENT_INTERVAL_MINUTES");
        if (autoLiquidityManagementIntervalMinutes != null) AUTO_LIQUIDITY_MANAGEMENT_INTERVAL_MINUTES = int.Parse(autoLiquidityManagementIntervalMinutes);

        // Audit Log
        var auditLogRetentionDays = Environment.GetEnvironmentVariable("AUDIT_LOG_RETENTION_DAYS");
        if (auditLogRetentionDays != null) AUDIT_LOG_RETENTION_DAYS = int.Parse(auditLogRetentionDays);

        AUDIT_LOG_CLEANUP_CRON = Environment.GetEnvironmentVariable("AUDIT_LOG_CLEANUP_CRON") ?? AUDIT_LOG_CLEANUP_CRON;

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

        var defaultChannelFeePolicyTimelockDeltaBlocks = Environment.GetEnvironmentVariable("DEFAULT_CHANNEL_FEE_POLICY_TIMELOCK_DELTA_BLOCKS");
        if (defaultChannelFeePolicyTimelockDeltaBlocks != null) DEFAULT_CHANNEL_FEE_POLICY_TIMELOCK_DELTA_BLOCKS = int.Parse(defaultChannelFeePolicyTimelockDeltaBlocks);

        var defaultChannelFeePolicyBaseFeeMsat = Environment.GetEnvironmentVariable("DEFAULT_CHANNEL_FEE_POLICY_BASE_FEE_MSAT");
        if (defaultChannelFeePolicyBaseFeeMsat != null) DEFAULT_CHANNEL_FEE_POLICY_BASE_FEE_MSAT = long.Parse(defaultChannelFeePolicyBaseFeeMsat);

        var defaultChannelFeePolicyFeeRatePpm = Environment.GetEnvironmentVariable("DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM");
        if (defaultChannelFeePolicyFeeRatePpm != null) DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM = long.Parse(defaultChannelFeePolicyFeeRatePpm);

        var anchorClosingMinSats = GetEnvironmentalVariableOrThrowIfNotTesting("ANCHOR_CLOSINGS_MINIMUM_SATS");
        if (anchorClosingMinSats != null) ANCHOR_CLOSINGS_MINIMUM_SATS = long.Parse(anchorClosingMinSats); // Check https://github.com/lightningnetwork/lnd/issues/6505#issuecomment-1120364460 to understand, we need 100K+ to support anchor channel closings

        var sweepConfTarget = Environment.GetEnvironmentVariable("SWEEP_CONF_TARGET");
        if (sweepConfTarget != null) SWEEP_CONF_TARGET = int.Parse(sweepConfTarget);

        var minSweepTransactionAmount = Environment.GetEnvironmentVariable("MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS");
        if (minSweepTransactionAmount != null) MINIMUM_SWEEP_TRANSACTION_AMOUNT_SATS = long.Parse(minSweepTransactionAmount);

        var minimumUtxoValueSats = Environment.GetEnvironmentVariable("MINIMUM_UTXO_VALUE_SATS");
        if (minimumUtxoValueSats != null) MINIMUM_UTXO_VALUE_SATS = long.Parse(minimumUtxoValueSats);


        DEFAULT_DERIVATION_PATH = GetEnvironmentalVariableOrThrowIfNotTesting("DEFAULT_DERIVATION_PATH") ?? DEFAULT_DERIVATION_PATH;

        var timeout = Environment.GetEnvironmentVariable("SESSION_TIMEOUT_MILLISECONDS");
        if (timeout != null) SESSION_TIMEOUT_MILLISECONDS = int.Parse(timeout);

        // Swap out size limits for automatic liquidity management (Loop Out)
        var minSwapOutSizeBtc = Environment.GetEnvironmentVariable("MINIMUM_SWAP_OUT_SIZE_BTC");
        MINIMUM_SWAP_OUT_SIZE_BTC = minSwapOutSizeBtc != null ? decimal.Parse(minSwapOutSizeBtc, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : MINIMUM_SWAP_OUT_SIZE_BTC;

        var maxSwapOutSizeBtc = Environment.GetEnvironmentVariable("MAXIMUM_SWAP_OUT_SIZE_BTC");
        MAXIMUM_SWAP_OUT_SIZE_BTC = maxSwapOutSizeBtc != null ? decimal.Parse(maxSwapOutSizeBtc, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : MAXIMUM_SWAP_OUT_SIZE_BTC;

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

        // Swap configuration
        var swapMaxMinerFees = Environment.GetEnvironmentVariable("SWAP_MAX_MINER_FEES_SATS");
        SWAP_MAX_MINER_FEES_SATS = swapMaxMinerFees != null ? long.Parse(swapMaxMinerFees) : SWAP_MAX_MINER_FEES_SATS;

        var swapMaxServiceFeesPercent = Environment.GetEnvironmentVariable("SWAP_MAX_SERVICE_FEES_PERCENT");
        SWAP_MAX_SERVICE_FEES_PERCENT = swapMaxServiceFeesPercent != null ? decimal.Parse(swapMaxServiceFeesPercent, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : SWAP_MAX_SERVICE_FEES_PERCENT;

        var swapPrepayAmount = Environment.GetEnvironmentVariable("SWAP_PREPAY_AMOUNT_SATS");
        SWAP_PREPAY_AMOUNT_SATS = swapPrepayAmount != null ? long.Parse(swapPrepayAmount) : SWAP_PREPAY_AMOUNT_SATS;

        // Rebalance configuration
        var rebInitialPct = Environment.GetEnvironmentVariable("REBALANCE_DEFAULT_MAX_FEE_PCT");
        if (rebInitialPct != null) REBALANCE_DEFAULT_MAX_FEE_PCT = decimal.Parse(rebInitialPct, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var rebRetryPct = Environment.GetEnvironmentVariable("REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT");
        if (rebRetryPct != null) REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT = decimal.Parse(rebRetryPct, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var rebMaxAttempts = Environment.GetEnvironmentVariable("REBALANCE_MAX_ATTEMPTS");
        if (rebMaxAttempts != null) REBALANCE_MAX_ATTEMPTS = int.Parse(rebMaxAttempts);

        var rebInitialDelay = Environment.GetEnvironmentVariable("REBALANCE_INITIAL_RETRY_DELAY_SECONDS");
        if (rebInitialDelay != null) REBALANCE_INITIAL_RETRY_DELAY_SECONDS = int.Parse(rebInitialDelay);

        var rebBackoff = Environment.GetEnvironmentVariable("REBALANCE_RETRY_BACKOFF_MULTIPLIER");
        if (rebBackoff != null) REBALANCE_RETRY_BACKOFF_MULTIPLIER = double.Parse(rebBackoff, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var rebMinAmount = Environment.GetEnvironmentVariable("REBALANCE_MIN_AMOUNT_SATS");
        if (rebMinAmount != null) REBALANCE_MIN_AMOUNT_SATS = long.Parse(rebMinAmount);

        var rebProbeBackoff = Environment.GetEnvironmentVariable("REBALANCE_AMOUNT_BACKOFF_RATIO");
        if (rebProbeBackoff != null) REBALANCE_AMOUNT_BACKOFF_RATIO = double.Parse(rebProbeBackoff, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var rebMaxParts = Environment.GetEnvironmentVariable("REBALANCE_MAX_PARTS");
        if (rebMaxParts != null) REBALANCE_MAX_PARTS = uint.Parse(rebMaxParts);

        var rebReconcileWindow = Environment.GetEnvironmentVariable("REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS");
        if (rebReconcileWindow != null) REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS = int.Parse(rebReconcileWindow);

        ROUTING_ENGINE_ENABLED = StringHelper.IsTrue(Environment.GetEnvironmentVariable("ROUTING_ENGINE_ENABLED"));

        var reMinAgeBlocks = Environment.GetEnvironmentVariable("ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS");
        if (reMinAgeBlocks != null) ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = uint.Parse(reMinAgeBlocks);

        var reFlowWindowDays = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FLOW_WINDOW_DAYS");
        if (reFlowWindowDays != null) ROUTING_ENGINE_FLOW_WINDOW_DAYS = int.Parse(reFlowWindowDays);

        var reFlowMinMsat = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FLOW_MIN_MSAT");
        if (reFlowMinMsat != null) ROUTING_ENGINE_FLOW_MIN_MSAT = long.Parse(reFlowMinMsat);

        var reNetFlowThreshold = Environment.GetEnvironmentVariable("ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD");
        if (reNetFlowThreshold != null) ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = double.Parse(reNetFlowThreshold, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var reTargetK = Environment.GetEnvironmentVariable("ROUTING_ENGINE_TARGET_K");
        if (reTargetK != null) ROUTING_ENGINE_TARGET_K = double.Parse(reTargetK, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var reTargetMaxDrift = Environment.GetEnvironmentVariable("ROUTING_ENGINE_TARGET_MAX_DRIFT");
        if (reTargetMaxDrift != null) ROUTING_ENGINE_TARGET_MAX_DRIFT = double.Parse(reTargetMaxDrift, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var reTargetAlpha = Environment.GetEnvironmentVariable("ROUTING_ENGINE_TARGET_ALPHA");
        if (reTargetAlpha != null) ROUTING_ENGINE_TARGET_ALPHA = double.Parse(reTargetAlpha, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var reEmaAlpha = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_EMA_ALPHA");
        if (reEmaAlpha != null) ROUTING_ENGINE_FEE_EMA_ALPHA = double.Parse(reEmaAlpha, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var reFlipHysteresis = Environment.GetEnvironmentVariable("ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES");
        if (reFlipHysteresis != null) ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = int.Parse(reFlipHysteresis);

        var reJobInterval = Environment.GetEnvironmentVariable("ROUTING_ENGINE_JOB_INTERVAL_MINUTES");
        if (reJobInterval != null) ROUTING_ENGINE_JOB_INTERVAL_MINUTES = int.Parse(reJobInterval);

        // Routing Engine
        var feeOutboundIntegralGain = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_OUTBOUND_INTEGRAL_GAIN");
        if (feeOutboundIntegralGain != null) ROUTING_ENGINE_FEE_OUTBOUND_INTEGRAL_GAIN = double.Parse(feeOutboundIntegralGain, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var feeInboundIntegralGain = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_INBOUND_INTEGRAL_GAIN");
        if (feeInboundIntegralGain != null) ROUTING_ENGINE_FEE_INBOUND_INTEGRAL_GAIN = double.Parse(feeInboundIntegralGain, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var feeDeadband = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_DEADBAND");
        if (feeDeadband != null) ROUTING_ENGINE_FEE_DEADBAND = double.Parse(feeDeadband, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var rebalanceDeadband = Environment.GetEnvironmentVariable("ROUTING_ENGINE_REBALANCE_DEADBAND");
        if (rebalanceDeadband != null) ROUTING_ENGINE_REBALANCE_TRIGGER = double.Parse(rebalanceDeadband, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var feeMaxStep = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MAX_STEP_PPM");
        if (feeMaxStep != null) ROUTING_ENGINE_FEE_MAX_STEP_PPM = uint.Parse(feeMaxStep);

        var feeMaxInboundStep = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MAX_INBOUND_STEP_PPM");
        if (feeMaxInboundStep != null) ROUTING_ENGINE_FEE_MAX_INBOUND_STEP_PPM = uint.Parse(feeMaxInboundStep);

        var feeMinDelta = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MIN_DELTA_PPM");
        if (feeMinDelta != null) ROUTING_ENGINE_FEE_MIN_DELTA_PPM = uint.Parse(feeMinDelta);

        var feeMinOutbound = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MIN_OUTBOUND_PPM");
        if (feeMinOutbound != null) ROUTING_ENGINE_FEE_MIN_OUTBOUND_PPM = uint.Parse(feeMinOutbound);

        var feeMaxOutbound = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MAX_OUTBOUND_PPM");
        if (feeMaxOutbound != null) ROUTING_ENGINE_FEE_MAX_OUTBOUND_PPM = uint.Parse(feeMaxOutbound);

        var feeMinInbound = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MIN_INBOUND_PPM");
        if (feeMinInbound != null) ROUTING_ENGINE_FEE_MIN_INBOUND_PPM = int.Parse(feeMinInbound);

        var feeMaxInbound = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MAX_INBOUND_PPM");
        if (feeMaxInbound != null) ROUTING_ENGINE_FEE_MAX_INBOUND_PPM = int.Parse(feeMaxInbound);

        var feeMinChannelSize = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS");
        if (feeMinChannelSize != null) ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS = long.Parse(feeMinChannelSize);

        var feeBaselineSource = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE");
        if (feeBaselineSource != null) ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE = uint.Parse(feeBaselineSource);

        var feeBaselineBidirectional = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL");
        if (feeBaselineBidirectional != null) ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL = uint.Parse(feeBaselineBidirectional);

        var feeBaselineSink = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_BASELINE_PPM_SINK");
        if (feeBaselineSink != null) ROUTING_ENGINE_FEE_BASELINE_PPM_SINK = uint.Parse(feeBaselineSink);

        var feeBaselineUncategorized = Environment.GetEnvironmentVariable("ROUTING_ENGINE_FEE_BASELINE_PPM_UNCATEGORIZED");
        if (feeBaselineUncategorized != null) ROUTING_ENGINE_FEE_BASELINE_PPM_UNCATEGORIZED = uint.Parse(feeBaselineUncategorized);

        // DB Initialization
        ALICE_PUBKEY = Environment.GetEnvironmentVariable("ALICE_PUBKEY") ?? ALICE_PUBKEY;
        ALICE_HOST = Environment.GetEnvironmentVariable("ALICE_HOST") ?? ALICE_HOST;
        ALICE_MACAROON = Environment.GetEnvironmentVariable("ALICE_MACAROON") ?? ALICE_MACAROON;

        BOB_PUBKEY = Environment.GetEnvironmentVariable("BOB_PUBKEY") ?? BOB_PUBKEY;
        BOB_HOST = Environment.GetEnvironmentVariable("BOB_HOST") ?? BOB_HOST;
        BOB_MACAROON = Environment.GetEnvironmentVariable("BOB_MACAROON") ?? BOB_MACAROON;
        BOB_LOOPD_HOST = Environment.GetEnvironmentVariable("BOB_LOOPD_HOST") ?? BOB_LOOPD_HOST;
        BOB_LOOPD_MACAROON = Environment.GetEnvironmentVariable("BOB_LOOPD_MACAROON") ?? BOB_LOOPD_MACAROON;
        BOB_LOOPD_TLS_CERT = Environment.GetEnvironmentVariable("BOB_LOOPD_TLS_CERT") ?? BOB_LOOPD_TLS_CERT;

        CAROL_PUBKEY = Environment.GetEnvironmentVariable("CAROL_PUBKEY") ?? CAROL_PUBKEY;
        CAROL_HOST = Environment.GetEnvironmentVariable("CAROL_HOST") ?? CAROL_HOST;
        CAROL_MACAROON = Environment.GetEnvironmentVariable("CAROL_MACAROON") ?? CAROL_MACAROON;
        CAROL_LOOPD_HOST = Environment.GetEnvironmentVariable("CAROL_LOOPD_HOST") ?? CAROL_LOOPD_HOST;
        CAROL_LOOPD_MACAROON = Environment.GetEnvironmentVariable("CAROL_LOOPD_MACAROON") ?? CAROL_LOOPD_MACAROON;
        CAROL_LOOPD_TLS_CERT = Environment.GetEnvironmentVariable("CAROL_LOOPD_TLS_CERT") ?? CAROL_LOOPD_TLS_CERT;
    }

}

public class EnvironmentalVariableMissingException : ArgumentNullException
{
    public EnvironmentalVariableMissingException(string message) : base(message + " must be set")
    {
    }
}
