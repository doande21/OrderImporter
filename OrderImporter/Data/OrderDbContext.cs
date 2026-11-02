using Microsoft.EntityFrameworkCore;
using OrderImporter.Entities;

namespace OrderImporter.Data
{
    public class OrderDbContext : DbContext
    {
        public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
        {
        }

        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.OrderCode).IsUnique();
                entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,2)");
            });
        }
    }
}