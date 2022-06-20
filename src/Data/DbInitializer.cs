using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data
{
    public static class DbInitializer
    {
        public static void Initialize(IServiceProvider serviceProvider)
        {
            //DI
            var applicationDbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();



            //Migrations
            var isConnected = false;
            while (isConnected == false)
            {
                try
                {
                    applicationDbContext.Database.Migrate();
                    isConnected = true;
                }
                catch
                {
                }
                Thread.Sleep(1_000);

            }

            //Roles

            applicationDbContext.Roles.Add(new IdentityRole
            {
                Name = ApplicationUserRole.NodeManager.ToString("G"),
                NormalizedName = ApplicationUserRole.NodeManager.ToString("G").ToUpper()
            });
            
            applicationDbContext.Roles.Add(new IdentityRole
            {
                Name = ApplicationUserRole.Superadmin.ToString("G"),
                NormalizedName = ApplicationUserRole.Superadmin.ToString("G").ToUpper()
            }); 

            applicationDbContext.Roles.Add(new IdentityRole
            {
                Name = ApplicationUserRole.TrustedFinanceUser.ToString("G"),
                NormalizedName = ApplicationUserRole.TrustedFinanceUser.ToString("G").ToUpper()
            });

            applicationDbContext.SaveChanges();
        }
    }
}
