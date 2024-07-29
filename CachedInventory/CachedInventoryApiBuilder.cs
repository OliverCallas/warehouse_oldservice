using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CachedInventory;

using Microsoft.AspNetCore.Mvc;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();

    // Configurar SQLite
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite("Data Source=stockCache.db"));

    builder.Services.AddSingleton<StockCache>();
    builder.Services.AddHostedService<StockCache>();
    builder.Services.AddLogging(logging => logging.AddConsole());

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      app.UseSwagger();
      app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    app.MapGet("/stock/{productId:int}", async ([FromServices] StockCache stockCache, int productId) =>
    {
      var stock = await stockCache.GetStock(productId);
      return Results.Ok(stock);
    })
    .WithName("GetStock")
    .WithOpenApi();

    app.MapPost("/stock/retrieve", async ([FromServices] StockCache stockCache, [FromBody] RetrieveStockRequest req) =>
    {
      var success = await stockCache.TryUpdateStock(req.ProductId, req.Amount);
      if (!success)
      {
        return Results.BadRequest("Not enough stock.");
      }

      return Results.Ok();
    })
    .WithName("RetrieveStock")
    .WithOpenApi();

    app.MapPost("/stock/restock", async ([FromServices] StockCache stockCache, [FromBody] RestockRequest req) =>
    {
      await stockCache.AddStock(req.ProductId, req.Amount);
      return Results.Ok();
    })
    .WithName("Restock")
    .WithOpenApi();

    return app;
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);