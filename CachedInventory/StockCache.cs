using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CachedInventory
{
    public class StockCache : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StockCache> _logger;
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new ConcurrentDictionary<int, SemaphoreSlim>();
        private Timer _syncTimer;
        private CancellationTokenSource _cancellationTokenSource;

        public StockCache(IServiceProvider serviceProvider, IServiceScopeFactory scopeFactory, ILogger<StockCache> logger)
        {
            _serviceProvider = serviceProvider;
            _scopeFactory = scopeFactory;
            _logger = logger;

            // Ensure database is created
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Database.EnsureCreated();
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _syncTimer = new Timer(SyncAllStocks, null, TimeSpan.Zero, TimeSpan.FromSeconds(9.0));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _syncTimer?.Change(Timeout.Infinite, 0);
            _cancellationTokenSource?.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _syncTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        public async Task<int> GetStock(int productId)
        {
            _logger.LogInformation($"Getting stock for product {productId}");

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var productStock = await dbContext.ProductStocks.FindAsync(productId);
                if (productStock != null)
                {
                    _logger.LogInformation($"Stock for product {productId} found in database: {productStock.Stock}");
                    return productStock.Stock;
                }

                var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
                var stock = await warehouseClient.GetStock(productId);
                productStock = new ProductStock { ProductId = productId, Stock = stock };
                dbContext.ProductStocks.Add(productStock);
                await dbContext.SaveChangesAsync();
                _logger.LogInformation($"Stock for product {productId} added to database: {stock}");
                return stock;
            }
        }

        public async Task<bool> TryUpdateStock(int productId, int quantity)
        {
            _logger.LogInformation($"Trying to update stock for product {productId} by {quantity}");
            var semaphore = _locks.GetOrAdd(productId, new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();

            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var productStock = await dbContext.ProductStocks.FindAsync(productId);
                    if (productStock != null && productStock.Stock >= quantity)
                    {
                        productStock.Stock -= quantity;
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Stock for product {productId} updated successfully. New stock: {productStock.Stock}");
                        return true;
                    }
                    _logger.LogWarning($"Insufficient stock for product {productId}");
                    return false;
                }
            }
            finally
            {
                semaphore.Release();
                _ = SyncStockWithWarehouse(productId);
            }
        }

        public async Task AddStock(int productId, int quantity)
        {
            _logger.LogInformation($"Adding stock for product {productId} by {quantity}");
            var semaphore = _locks.GetOrAdd(productId, new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();

            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var productStock = await dbContext.ProductStocks.FindAsync(productId);
                    if (productStock != null)
                    {
                        productStock.Stock += quantity;
                        _logger.LogInformation($"Stock for product {productId} incremented. New stock: {productStock.Stock}");
                    }
                    else
                    {
                        var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
                        var stock = await warehouseClient.GetStock(productId);
                        productStock = new ProductStock { ProductId = productId, Stock = stock + quantity };
                        dbContext.ProductStocks.Add(productStock);
                        _logger.LogInformation($"Stock for product {productId} added to database: {stock + quantity}");
                    }
                    await dbContext.SaveChangesAsync();
                }
            }
            finally
            {
                semaphore.Release();
                _ = SyncStockWithWarehouse(productId);
            }
        }

        private async void SyncAllStocks(object? state)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var productStocks = await dbContext.ProductStocks.ToListAsync();
                foreach (var productStock in productStocks)
                {
                    await SyncStockWithWarehouse(productStock.ProductId);
                }
            }
        }

        private async Task SyncStockWithWarehouse(int productId)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
                var productStock = await dbContext.ProductStocks.FindAsync(productId);
                if (productStock != null)
                {
                    _logger.LogInformation($"Synchronizing stock for product {productId} with warehouse. Stock: {productStock.Stock}");
                    await warehouseClient.UpdateStock(productId, productStock.Stock);
                }
            }
        }
    }
}
