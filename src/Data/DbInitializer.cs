using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
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
            var nodeRepository= serviceProvider.GetRequiredService<INodeRepository>();

            var webHostEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();


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
            SetRoles(applicationDbContext);

            if (webHostEnvironment.IsDevelopment())
            {
                //Testing node from Polar (ALICE) LND 0.14.3 -> check devnetwork.zip polar file
                var nodes = Task.Run(()=>nodeRepository.GetAll()).Result;


               
                var alice = new Node
                {
                    ChannelAdminMacaroon =
                        "0201036c6e6402f801030a108be5b2928f746a822b04a9b2848eb0321201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e6572617465120472656164000006208e8b02d4bc0efd4f15a52946c5ef23f2954f8a07ed800733554a11a190cb71b4",
                    //THIS MIGHT CHANGE ON YOUR MACHINE!!
                    Endpoint = "host.docker.internal:10001",
                    Name = "Alice",
                    CreationDatetime = DateTimeOffset.UtcNow,
                    PubKey = "0201036c6e6402f801030a10dc64226b045d25f090b114baebcbf04c1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620a21b8cc8c071aa5104b706b751aede972f642537c05da31450fb4b02c6da776e",


                };
                if (!nodes.Any(x => x.PubKey == alice.PubKey))
                {
                    var valueTuple = Task.Run(() => nodeRepository.AddAsync(alice)).Result;

                }

                //Testing node from Polar (CAROL) LND 0.14.3 -> check devnetwork.zip polar file
                var carol = new Node
                {
                    ChannelAdminMacaroon = "0201036c6e6402f801030a10dc64226b045d25f090b114baebcbf04c1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620a21b8cc8c071aa5104b706b751aede972f642537c05da31450fb4b02c6da776e",
                    //THIS MIGHT CHANGE ON YOUR MACHINE!!
                    Endpoint = "host.docker.internal:10003",
                    Name = "Carol",
                    CreationDatetime = DateTimeOffset.UtcNow,
                    PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
                    
                };
                
                if (!nodes.Any(x => x.PubKey == carol.PubKey))
                {
                    var valueTuple = Task.Run(() => nodeRepository.AddAsync(carol)).Result;

                }
            }
            else
            {
                
                
            }

            applicationDbContext.SaveChanges();
        }

        private static void SetRoles(ApplicationDbContext applicationDbContext)
        {
            const ApplicationUserRole nodeManager = ApplicationUserRole.NodeManager;

            var roles = applicationDbContext.Roles.ToList();
            if (roles.FirstOrDefault(x => x.Name == nodeManager.ToString("G")) == null)
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
        }
    }
}
