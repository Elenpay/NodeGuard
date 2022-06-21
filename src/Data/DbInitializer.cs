using FundsManager.Data.Models;
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
            const ApplicationUserRole nodeManager = ApplicationUserRole.NodeManager;

            var roles = applicationDbContext.Roles.ToList();
            if (roles.FirstOrDefault(x=> x.Name == nodeManager.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = nodeManager.ToString("G"),
                    NormalizedName = nodeManager.ToString("G").ToUpper()
                });
            }

            const ApplicationUserRole superadmin = ApplicationUserRole.Superadmin;

            if (roles.FirstOrDefault(x => x.Name == superadmin.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = superadmin.ToString("G"),
                    NormalizedName = superadmin.ToString("G").ToUpper()
                });
            }

            const ApplicationUserRole trustedFinanceUser = ApplicationUserRole.TrustedFinanceUser;

            if (roles.FirstOrDefault(x => x.Name == trustedFinanceUser.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = trustedFinanceUser.ToString("G"),
                    NormalizedName = trustedFinanceUser.ToString("G").ToUpper()
                });
            }

            applicationDbContext.SaveChanges();
        }
    }
}
