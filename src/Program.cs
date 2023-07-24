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

using Blazored.Toast;
using Blazorise;
using Blazorise.Bootstrap;
using Blazorise.Icons.FontAwesome;
using FundsManager.Areas.Identity;
using FundsManager.Automapper;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Jobs;
using FundsManager.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Quartz;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Exporter;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using FundsManager.Helpers;
using FundsManager.Rpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace FundsManager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.With(new DatadogLogEnricher())
                .WriteTo.Console(new JsonFormatter())
                .CreateLogger();

            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger);

            // Add services to the container.

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            //Identity
            builder.Services
                .AddDefaultIdentity<ApplicationUser>(options => { options.SignIn.RequireConfirmedAccount = false; })
                .AddRoles<IdentityRole>()
                .AddRoleManager<RoleManager<IdentityRole>>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            builder.Services.Configure<SecurityStampValidatorOptions>(options =>
            {
                // enables immediate logout, after updating the user's stat.
                options.ValidationInterval = TimeSpan.Zero;
            });

            builder.Services.AddRazorPages();
            builder.Services.AddServerSideBlazor().AddCircuitOptions(options => { options.DetailedErrors = true; });

            //Dependency Injection
            builder.Services
                .AddScoped<AuthenticationStateProvider,
                    RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();
            //Repos DI
            builder.Services.AddTransient(typeof(IRepository<>), typeof(Repository<>));
            builder.Services.AddTransient<IApplicationUserRepository, ApplicationUserRepository>();
            builder.Services.AddTransient<IChannelOperationRequestRepository, ChannelOperationRequestRepository>();
            builder.Services
                .AddTransient<IChannelOperationRequestPSBTRepository, ChannelOperationRequestPSBTRepository>();
            builder.Services.AddTransient<IChannelRepository, ChannelRepository>();
            builder.Services.AddTransient<IKeyRepository, KeyRepository>();
            builder.Services.AddTransient<INodeRepository, NodeRepository>();
            builder.Services.AddTransient<IWalletRepository, WalletRepository>();
            builder.Services.AddTransient<IInternalWalletRepository, InternalWalletRepository>();
            builder.Services.AddTransient<IFMUTXORepository, FUTXORepository>();
            builder.Services.AddTransient<IWalletWithdrawalRequestPsbtRepository, WalletWithdrawalRequestPsbtRepository>();
            builder.Services.AddTransient<IWalletWithdrawalRequestRepository, WalletWithdrawalRequestRepository>();
            builder.Services.AddTransient<IRemoteSignerService, RemoteSignerServiceService>();
            builder.Services.AddTransient<ILiquidityRuleRepository, LiquidityRuleRepository>();
            builder.Services.AddTransient<ICoinSelectionService, CoinSelectionService>();

            //BlazoredToast
            builder.Services.AddBlazoredToast();

            //Service DI
            builder.Services.AddTransient<ClipboardService>();
            builder.Services.AddTransient<ILocalStorageService, LocalStorageService>();
            builder.Services.AddTransient<ILightningService, LightningService>();
            builder.Services.AddTransient<IBitcoinService, BitcoinService>();
            builder.Services.AddTransient<NotificationService, NotificationService>();
            builder.Services.AddTransient<INBXplorerService, NBXplorerService>();

            //DbContext
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                //options.EnableSensitiveDataLogging();
                //options.EnableDetailedErrors();
                options.UseNpgsql(Constants.POSTGRES_CONNECTIONSTRING, options =>
                {
                    options.UseQuerySplittingBehavior(QuerySplittingBehavior
                        .SingleQuery); // Slower but integrity is ensured
                });
            }, ServiceLifetime.Transient);

            //HTTPClient factory
            builder.Services.AddHttpClient();

            //gRPC
            builder.Services.AddGrpc(options =>
            {
                var messageSize = 200 * 1024 * 1024; // 200 MB
                options.MaxReceiveMessageSize = messageSize;
                options.MaxSendMessageSize = messageSize;
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                // Setup a HTTP/2 endpoint without TLS.
                options.ListenAnyIP(50051, o => o.Protocols =
                    HttpProtocols.Http2);
                options.ListenAnyIP(int.Parse(Environment.GetEnvironmentVariable("HTTP1_LISTEN_PORT") ?? "80"), o => o.Protocols =
                    HttpProtocols.Http1);
            });

            //DBContextFactory
            builder.Services.AddDbContextFactory<ApplicationDbContext>(
                options =>
                {
                    options.EnableSensitiveDataLogging();
                    options.EnableDetailedErrors();
                    options.UseNpgsql(Constants.POSTGRES_CONNECTIONSTRING, options =>
                    {
                        options.UseQuerySplittingBehavior(QuerySplittingBehavior
                            .SingleQuery); // Slower but integrity is ensured
                    });
                }, ServiceLifetime.Transient);

            //Blazorise

            builder.Services
                .AddBlazorise(options => { options.Immediate = true; })
                .AddBootstrapProviders()
                .AddFontAwesomeIcons();

            builder.Services.AddQuartz(q =>
            {
                //Right now we are using in-memory storage
                q.UsePersistentStore(options =>
                {
                    options.UseProperties = false;
                    options.RetryInterval = TimeSpan.FromSeconds(15);
                    options.UsePostgres(Constants.POSTGRES_CONNECTIONSTRING);
                    options.UseJsonSerializer();
                });

                q.UseDedicatedThreadPool(x => { x.MaxConcurrency = 500; });

                //This allows DI in jobs
                q.UseMicrosoftDependencyInjectionJobFactory();

                //Sweep Job
                q.AddJob<SweepAllNodesWalletsJob>(opts =>
                {
                    opts.DisallowConcurrentExecution();
                    opts.WithIdentity(nameof(SweepAllNodesWalletsJob));
                });

                q.AddTrigger(opts =>
                {
                    opts.ForJob(nameof(SweepAllNodesWalletsJob)).WithIdentity($"{nameof(SweepAllNodesWalletsJob)}Trigger")
                        .StartNow().WithSimpleSchedule(scheduleBuilder => { scheduleBuilder.WithIntervalInMinutes(1).RepeatForever(); });
                });

                //Monitor Withdrawals Job
                q.AddJob<MonitorWithdrawalsJob>(opts =>
                {
                    opts.DisallowConcurrentExecution();
                    opts.WithIdentity(nameof(MonitorWithdrawalsJob));
                });

                q.AddTrigger(opts =>
                {
                    opts.ForJob(nameof(MonitorWithdrawalsJob)).WithIdentity($"{nameof(MonitorWithdrawalsJob)}Trigger")
                        .StartNow().WithCronSchedule(Constants.MONITOR_WITHDRAWALS_CRON);
                });

                // ChannelAcceptorJob

                q.AddJob<ChannelAcceptorJob>(opts =>
                {
                    opts.DisallowConcurrentExecution();
                    opts.WithIdentity(nameof(ChannelAcceptorJob));
                });

                q.AddTrigger(opts =>
                {
                    opts.ForJob(nameof(ChannelAcceptorJob)).WithIdentity($"{nameof(ChannelAcceptorJob)}Trigger")
                        .StartNow();
                });

                // NodeChannelSubscribeJob
                q.AddJob<NodeSubscriptorJob>(opts => { opts.WithIdentity(nameof(NodeSubscriptorJob)); });

                q.AddTrigger(opts =>
                {
                    opts.ForJob(nameof(NodeSubscriptorJob)).WithIdentity($"{nameof(NodeSubscriptorJob)}Trigger")
                        .StartNow();
                });

                // MonitorChannelsJob
                q.AddJob<MonitorChannelsJob>(opts => { opts.WithIdentity(nameof(MonitorChannelsJob)); });

                q.AddTrigger(opts =>
                {
                    opts.ForJob(nameof(MonitorChannelsJob)).WithIdentity($"{nameof(MonitorChannelsJob)}Trigger")
                        .StartNow().WithCronSchedule(Constants.MONITOR_CHANNELS_CRON);;
                });
            });

            // ASP.NET Core hosting
            builder.Services.AddQuartzServer(options =>
            {
                // when shutting down we want jobs to complete gracefully
                options.WaitForJobsToComplete = true;
                options.AwaitApplicationStarted = true;
            });

            //Automapper
            builder.Services.AddAutoMapper(typeof(MapperProfile));

            if (Constants.OTEL_EXPORTER_ENDPOINT != null)
            {
                builder.Services
                    .AddOpenTelemetryTracing((builder) => builder
                        // Configure the resource attribute `service.name` to MyServiceName
                        //.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BtcPayServer"))
                        .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddEnvironmentVariableDetector())
                        // Add tracing of the AspNetCore instrumentation library
                        .AddAspNetCoreInstrumentation()
                        .AddOtlpExporter(options =>
                        {
                            options.Protocol = OtlpExportProtocol.Grpc;
                            options.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
                            options.Endpoint = new Uri(Constants.OTEL_EXPORTER_ENDPOINT);
                        })
                        .AddEntityFrameworkCoreInstrumentation()
                        .AddQuartzInstrumentation()
                    );

                builder.Services
                    .AddOpenTelemetryMetrics(builder => builder
                        // Configure the resource attribute `service.name` to MyServiceName
                        //.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BtcPayServer"))
                        // Add metrics from the AspNetCore instrumentation library
                        .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddEnvironmentVariableDetector())
                        .AddAspNetCoreInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddOtlpExporter(options =>
                        {
                            options.Protocol = OtlpExportProtocol.Grpc;
                            options.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
                            options.Endpoint = new Uri(Constants.OTEL_EXPORTER_ENDPOINT);
                        })
                    );
            }


            var app = builder.Build();

            //DbInitialisation & Migrations

            using (var scope = app.Services.CreateScope())
            {
                var servicesProvider = scope.ServiceProvider;
                try
                {
                    DbInitializer.Initialize(servicesProvider);
                }
                catch (Exception ex)
                {
                    var logger = servicesProvider.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "An error occurred while seeding the database.");
                    throw;
                }
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseMigrationsEndPoint();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            //app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapBlazorHub();
            app.MapFallbackToPage("/_Host");

            //Grpc services
            //TODO Auth in the future, DAPR(?)
            app.MapGrpcService<NodeGuardService>();

            app.Run();
        }
    }
}