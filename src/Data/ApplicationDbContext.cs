using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            //modelBuilder.Entity<ChannelOperationRequest>().Ignore(cor => cor.DestNode);
            //modelBuilder.Entity<ChannelOperationRequest>().Ignore(cor => cor.SourceNode);

            //modelBuilder.Entity<Node>().Ignore(node => node.ChannelOperationRequestsAsSource);
            //modelBuilder.Entity<Node>().Ignore(node => node.ChannelOperationRequestsAsDestination);

            //modelBuilder.Entity<Node>()
            //    .HasMany(node => node.ChannelOperationRequestsAsSource)
            //    .WithOne(cor => cor.SourceNode)
            //    .HasForeignKey(cor => cor.DestNode);
            //modelBuilder.Entity<Node>()
            //    .HasMany(node => node.ChannelOperationRequestsAsDestination)
            //    .WithOne(cor => cor.DestNode)
            //    .HasForeignKey(cor => cor.DestNode);

            modelBuilder.Entity<ChannelOperationRequest>()
                .HasOne(cor => cor.SourceNode)
                .WithMany(node => node.ChannelOperationRequestsAsSource)
                .HasForeignKey(cor => cor.SourceNodeId);
            modelBuilder.Entity<ChannelOperationRequest>()
                .HasOne(cor => cor.DestNode)
                .WithMany(node => node.ChannelOperationRequestsAsDestination)
                .HasForeignKey(cor => cor.DestNodeId);
            base.OnModelCreating(modelBuilder);

        }

        public DbSet<ApplicationUser> ApplicationUsers { get; set; }

        public DbSet<Key> Keys { get; set; }

        public DbSet<Node> Nodes { get; set; }

        public DbSet<Wallet> Wallets { get; set; }

        public DbSet<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        public DbSet<ChannelOperationRequestSignature> ChannelOperationRequestSignatures { get; set; }

        public DbSet<Channel> Channels { get; set; }

    }
}