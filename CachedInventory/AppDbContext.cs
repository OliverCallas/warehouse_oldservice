using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CachedInventory
{
    public class AppDbContext : DbContext
    {
        public DbSet<ProductStock> ProductStocks { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProductStock>().HasKey(ps => ps.ProductId);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseLoggerFactory(LoggerFactory.Create(builder => { builder.AddConsole(); }));
        }
    }

    public class ProductStock
    {
        public int ProductId { get; set; }
        public int Stock { get; set; }
    }
}