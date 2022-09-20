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
using FundsManager.Services;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Sentry;
using Sentry.Extensibility;

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
            builder.Services.AddTransient<ClipboardService>();
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
            builder.Services.AddTransient<ILightningService, LightningService>();
            builder.Services.AddTransient<IBitcoinService, BitcoinService>();

            //DbContext
            var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") ??
                                   "Host=localhost;Port=35433;Database=fundsmanager;Username=rw_dev;Password=rw_dev";
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                //options.EnableSensitiveDataLogging();
                //options.EnableDetailedErrors();
                options.UseNpgsql(connectionString);
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

                config.UsePostgreSqlStorage(connectionString);
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

            var app = builder.Build();

            //DbInitialisation & Migrations

            using (var scope = app.Services.CreateScope())
            {
                var servicesProvider = scope.ServiceProvider;
                try
                {
                    DbInitializer.Initialize(servicesProvider);

                    //Background job for ChannelAcceptor
                    var bgClient = servicesProvider.GetService<IBackgroundJobClient>();
                    var logger = servicesProvider.GetService<ILogger<Program>>();
                    if (bgClient != null)
                    {
                        var jobId = bgClient.Enqueue<ILightningService>(service => service.ChannelAcceptorJob());
                        logger?.LogInformation("Lifetime job for channel acceptor launched on jobId:{}", jobId);
                    }

                    //Recurring Jobs AKA Cron Jobs
                    var recurringJobManager = servicesProvider.GetService<IRecurringJobManager>();

                    recurringJobManager.AddOrUpdate<ILightningService>(nameof(LightningService.SweepNodeWalletsJob),
                        x => x.SweepNodeWalletsJob(),
                        Environment.GetEnvironmentVariable("SWEEPNODEWALLETSJOB_CRON"),
                        TimeZoneInfo.Utc);

                    recurringJobManager.AddOrUpdate<IBitcoinService>(nameof(BitcoinService.MonitorWithdrawals),
                        x => x.MonitorWithdrawals(),
                        Environment.GetEnvironmentVariable("MONITOR_WITHDRAWALS_CRON"),
                        TimeZoneInfo.Utc);
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