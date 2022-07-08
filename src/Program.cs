using Blazorise;
using Blazorise.Bootstrap;
using Blazorise.Icons.FontAwesome;
using FundsManager.Areas.Identity;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Lnrpc;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FundsManager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();
            builder.Services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = false)
                .AddRoles<IdentityRole>().AddRoleManager<RoleManager<IdentityRole>>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            builder.Services.AddRazorPages();
            builder.Services.AddServerSideBlazor().AddCircuitOptions(options => { options.DetailedErrors = true; });
            builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<IdentityUser>>();

            //Dependency Injection

            //Repos DI
            builder.Services.AddTransient(typeof(IRepository<>), typeof(Repository<>));
            builder.Services.AddTransient<IApplicationUserRepository, ApplicationUserRepository>();
            builder.Services.AddTransient<IChannelOperationRequestRepository, ChannelOperationRequestRepository>();
            builder.Services.AddTransient<IChannelOperationRequestSignatureRepository, ChannelOperationRequestSignatureRepository>();
            builder.Services.AddTransient<IChannelRepository, ChannelRepository>();
            builder.Services.AddTransient<IKeyRepository, KeyRepository>();
            builder.Services.AddTransient<INodeRepository, NodeRepository>();
            builder.Services.AddTransient<IWalletRepository, WalletRepository>();
            builder.Services.AddTransient<IInternalWalletRepository, InternalWalletRepository>();

            //Service DI
            builder.Services.AddTransient<ILndService, LndService>();

            //gRPC
            builder.Services.AddGrpcClient<Lightning.LightningClient>(options =>
            {
                options.Address = new Uri("https://host.docker.internal:10001");
            });

            //DbContext
            var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") ?? "Host=localhost;Port=35433;Database=fundsmanager;Username=rw_dev;Password=rw_dev";
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
                    //options.EnableSensitiveDataLogging();
                    //options.EnableDetailedErrors();
                    options.UseNpgsql(connectionString);
                }, ServiceLifetime.Transient);

            //Blazorise

            builder.Services
                .AddBlazorise(options =>
                {
                    options.Immediate = true;
                })
                .AddBootstrapProviders()
                .AddFontAwesomeIcons();

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

            app.Run();
        }
    }
}