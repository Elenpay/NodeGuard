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
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quartz;
using Sentry;
using Sentry.Extensibility;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Exporter;

namespace FundsManager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            //Identity
            builder.Services
                .AddDefaultIdentity<ApplicationUser>(options =>
                {
                    options.SignIn.RequireConfirmedAccount = false;
                })
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

            //BlazoredToast
            builder.Services.AddBlazoredToast();

            //Service DI
            builder.Services.AddTransient<ClipboardService>();
            builder.Services.AddTransient<ILightningService, LightningService>();
            builder.Services.AddTransient<IBitcoinService, BitcoinService>();
            builder.Services.AddTransient<NotificationService, NotificationService>();

            //DbContext
            var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") ??
                                   "Host=localhost;Port=35433;Database=fundsmanager;Username=rw_dev;Password=rw_dev";
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                //options.EnableSensitiveDataLogging();
                //options.EnableDetailedErrors();
                options.UseNpgsql(connectionString, options =>
                    {
                        options.UseQuerySplittingBehavior(QuerySplittingBehavior
                            .SingleQuery); // Slower but integrity is ensured
                    });
            }, ServiceLifetime.Transient);

            //DBContextFactory
            builder.Services.AddDbContextFactory<ApplicationDbContext>(
                options =>
                {
                    options.EnableSensitiveDataLogging();
                    options.EnableDetailedErrors();
                    options.UseNpgsql(connectionString, options =>
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

                //q.UsePersistentStore(options =>
                //{
                //    options.UseProperties = true;
                //    options.UseJsonSerializer();

                //    options.UsePostgres(Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING"));
                //});

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
                        .StartNow().WithSimpleSchedule(scheduleBuilder =>
                        {
                            scheduleBuilder.WithIntervalInMinutes(1).RepeatForever();
                        });
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
                        .StartNow().WithCronSchedule(Environment.GetEnvironmentVariable("MONITOR_WITHDRAWALS_CRON"));
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
            });

            // ASP.NET Core hosting
            builder.Services.AddQuartzServer(options =>
            {
                // when shutting down we want jobs to complete gracefully
                options.WaitForJobsToComplete = true;
                options.AwaitApplicationStarted = true;
            });

            //Hangfire Job system

            builder.Services.AddHangfire((provider, config) =>
            {
                config.UseSerializerSettings(new JsonSerializerSettings()
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                });
                config.UseFilter(new AutomaticRetryAttribute
                {
                    LogEvents = true,
                    Attempts = 20,
                    OnAttemptsExceeded = AttemptsExceededAction.Fail,
                });

                config.UseRedisStorage(Environment.GetEnvironmentVariable("REDIS_CONNECTIONSTRING") ??
                                       throw new ArgumentException("Redis env var not set"));
            });

            builder.Services.AddHangfireServer();

            //Automapper
            builder.Services.AddAutoMapper(typeof(MapperProfile));

            // Sentry
            builder.Services.AddSentry();
            builder.WebHost.UseSentry(options =>
            {
                options.Dsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
                options.Environment = Environment.GetEnvironmentVariable("SENTRY_ENVIRONMENT");
                options.TracesSampleRate = 1;
                options.SendDefaultPii = true;
                options.AttachStacktrace = true;
                options.MaxRequestBodySize = RequestSize.Medium;
                options.MinimumBreadcrumbLevel = LogLevel.Debug;
                options.MinimumEventLevel = LogLevel.Warning;
                options.DiagnosticLevel = SentryLevel.Error;
                options.Debug = Convert.ToBoolean(Environment.GetEnvironmentVariable("SENTRY_DEBUG_ENABLED"));
            });

            //We need to expand the env-var with %ENV_VAR% for K8S
            var otelCollectorEndpointToBeExpanded = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (otelCollectorEndpointToBeExpanded != null)
            {
                var otelCollectorEndpoint = Environment.ExpandEnvironmentVariables(otelCollectorEndpointToBeExpanded);

                if (!string.IsNullOrEmpty(otelCollectorEndpoint))
                {
                    const string otelResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";
                    var expandedResourceAttributes = Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable(otelResourceAttributes));
                    Environment.SetEnvironmentVariable(otelResourceAttributes, expandedResourceAttributes);
                    
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
                                options.Endpoint = new Uri(otelCollectorEndpoint);
                            })
                            .AddEntityFrameworkCoreInstrumentation()
                            .AddHangfireInstrumentation(options =>
                            {
                                options.RecordException = true;
                            })
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
                                options.Endpoint = new Uri(otelCollectorEndpoint);
                            })
                    );
                }
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
                app.UseSentryTracing();
            }

            //app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapBlazorHub();
            app.MapFallbackToPage("/_Host");

            //Hangfire
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = new[] { new MyAuthorizationFilter() }
            });

            app.Run();
        }

        public class MyAuthorizationFilter : IDashboardAuthorizationFilter
        {
            public bool Authorize(DashboardContext context)
            {
                var httpContext = context.GetHttpContext();

                // Allow server admins
                return httpContext.User.Identity != null &&
                       httpContext.User.Identity.IsAuthenticated &&
                       httpContext.User.IsInRole("Superadmin");
            }
        }
    }
}