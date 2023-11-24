using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data;

public class DataProtectionKeysContext : DbContext, IDataProtectionKeyContext
{
    // A recommended constructor overload when using EF Core 
    // with dependency injection.
    public DataProtectionKeysContext(DbContextOptions<DataProtectionKeysContext> options)
        : base(options)
    {
    }

    // This maps to the table that stores keys.
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
}