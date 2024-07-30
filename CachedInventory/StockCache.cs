namespace CachedInventory;

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
public class StockCache : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory serviceScopeFactory;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> currentLocks = new();
    private Timer? syncTimer;
    private CancellationTokenSource? cancellationTokenSource;

    public StockCache(IServiceScopeFactory scopeFactory)
    {
        serviceScopeFactory = scopeFactory;

        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = dbContext.Database.EnsureCreated();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        syncTimer = new Timer(SyncAllStocks, null, TimeSpan.Zero, TimeSpan.FromSeconds(9.0));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = (syncTimer?.Change(Timeout.Infinite, 0));
        cancellationTokenSource?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        syncTimer?.Dispose();
        cancellationTokenSource?.Dispose();
    }

    public async Task<int> GetStock(int productId)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var productStock = await dbContext.ProductStocks.FindAsync(productId);
        if (productStock != null)
        {
            return productStock.Stock;
        }
        var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
        var stock = await warehouseClient.GetStock(productId);
        productStock = new ProductStock { ProductId = productId, Stock = stock };
        _ = dbContext.ProductStocks.Add(productStock);
        _ = await dbContext.SaveChangesAsync();
        return stock;
    }

    public async Task<bool> TryUpdateStock(int productId, int quantity)
    {
        var semaphore = currentLocks.GetOrAdd(productId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var productStock = await dbContext.ProductStocks.FindAsync(productId);
            if (productStock != null && productStock.Stock >= quantity)
            {
                productStock.Stock -= quantity;
                _ = await dbContext.SaveChangesAsync();
                return true;
            }
            return false;
        }
        finally
        {
            _ = semaphore.Release();
            _ = SyncStockWithWarehouse(productId);
        }
    }

    public async Task AddStock(int productId, int quantity)
    {
        var semaphore = currentLocks.GetOrAdd(productId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var productStock = await dbContext.ProductStocks.FindAsync(productId);
            if (productStock != null)
            {
                productStock.Stock += quantity;
            }
            else
            {
                var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
                var stock = await warehouseClient.GetStock(productId);
                productStock = new ProductStock { ProductId = productId, Stock = stock + quantity };
                _ = dbContext.ProductStocks.Add(productStock);
            }
            _ = await dbContext.SaveChangesAsync();
        }
        finally
        {
            _ = semaphore.Release();
            _ = SyncStockWithWarehouse(productId);
        }
    }

    private async void SyncAllStocks(object? state)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var productStocks = await dbContext.ProductStocks.ToListAsync();
        foreach (var productStock in productStocks)
        {
            await SyncStockWithWarehouse(productStock.ProductId);
        }
    }

    private async Task SyncStockWithWarehouse(int productId)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var warehouseClient = scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();
        var productStock = await dbContext.ProductStocks.FindAsync(productId);
        if (productStock != null)
        {
            await warehouseClient.UpdateStock(productId, productStock.Stock);
        }
    }
}
