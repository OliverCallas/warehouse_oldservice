namespace CachedInventory.Tests;

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

public record TestSetup(string Url) : IAsyncDisposable
{

  //PortSemaphore: Semáforo para gestionar el acceso a puertos libres.
  private static readonly SemaphoreSlim PortSemaphore = new(1);

  //Random: Generador de números aleatorios para seleccionar puertos.
  private static readonly Random Random = new();

  //Client: Cliente HTTP para realizar solicitudes a la API.
  private static readonly HttpClient Client = new();

  //requestDurations: Lista para almacenar la duración de las solicitudes.
  private readonly List<long> requestDurations = [];

  //AverageRequestDuration: Propiedad que calcula la duración promedio de las solicitudes.
  public long AverageRequestDuration => Convert.ToInt64(requestDurations.Average());

  //DisposeAsync(): Método para limpiar y liberar recursos. Llama a WebAppTracker.TryDispose() para detener la aplicación web.
  public async ValueTask DisposeAsync()
  {
    await WebAppTracker.TryDispose();
    GC.SuppressFinalize(this);
  }

  //VerifyStockFromFile(int productId, int expectedStock): Verifica que el stock del producto en el archivo de almacenamiento 
  //coincida con el stock esperado después de un tiempo de espera de 10.5 segundos.

  //ALERTA, ESTE ES EL MENSAJE MAS CONCURRENTE CON ERRORES:
  public async Task VerifyStockFromFile(int productId, int expectedStock)
  {
    // Tiempo de latencia permitido para actualizar el fichero.
    await Task.Delay(10_500);
    var fileStock = await WarehouseStockSystemClient.GetStockDirectlyFromFile(productId);
    Assert.True(
      fileStock == expectedStock,
      $"El fichero no se actualizó correctamente. Stock en el fichero: {fileStock}. Stock esperado: {expectedStock}.");
  }

  //GetStock(int productId, bool isFirst = false): Obtiene el stock de un producto desde la API y mide la duración de la solicitud.
  public async Task<int> GetStock(int productId, bool isFirst = false)
  {
    var stopwatch = Stopwatch.StartNew();
    var response = await Client.GetAsync($"{Url}stock/{productId}");
    var content = await response.Content.ReadAsStringAsync();
    stopwatch.Stop();
    if (!isFirst)
    {
      requestDurations.Add(stopwatch.ElapsedMilliseconds);
    }
    Assert.True(response.IsSuccessStatusCode, $"Error al obtener el stock del producto {productId}.");
    return int.Parse(content);
  }

  //Restock(int productId, int totalToRetrieve): Repone el stock de un producto hasta una cantidad específica.
  public async Task Restock(int productId, int totalToRetrieve)
  {
    var currentStock = await GetStock(productId, true);
    var missingStock = totalToRetrieve - currentStock;
    switch (missingStock)
    {
      case > 0:
      {
        var restockRequest = new RestockRequest(productId, missingStock);
        var restockRequestJson = JsonSerializer.Serialize(restockRequest);
        var restockRequestContent = new StringContent(restockRequestJson);
        restockRequestContent.Headers.ContentType = new("application/json");
        var stopwatch = Stopwatch.StartNew();
        var response = await Client.PostAsync($"{Url}stock/restock", restockRequestContent);
        stopwatch.Stop();
        requestDurations.Add(stopwatch.ElapsedMilliseconds);
        Assert.True(response.IsSuccessStatusCode, $"Error al reponer el stock del producto {productId}.");
        return;
      }
      case < 0:
        await Retrieve(productId, -missingStock);
        break;
    }
  }

  //Retrieve(int productId, int amount): Retira una cantidad específica de stock de un producto.
  public async Task Retrieve(int productId, int amount)
  {
    var retrieveRequest = new { productId, amount };
    var retrieveRequestJson = JsonSerializer.Serialize(retrieveRequest);
    var retrieveRequestContent = new StringContent(retrieveRequestJson);
    retrieveRequestContent.Headers.ContentType = new("application/json");
    var stopwatch = Stopwatch.StartNew();
    var response = await Client.PostAsync($"{Url}stock/retrieve", retrieveRequestContent);
    stopwatch.Stop();
    requestDurations.Add(stopwatch.ElapsedMilliseconds);
    Assert.True(response.IsSuccessStatusCode, $"Error al retirar el stock del producto {productId}.");
  }

  //GetRandomPort(): Obtiene un puerto aleatorio.
  private static int GetRandomPort() => Random.Next(30000) + 10000;

  //GetFreePort(): Obtiene un puerto libre utilizando un semáforo para evitar conflictos.
  private static async Task<int> GetFreePort()
  {
    await PortSemaphore.WaitAsync();

    try
    {
      return Go(GetRandomPort());

      int Go(int pn) => GetConnectionInfo().Any(ci => ci.LocalEndPoint.Port == pn) ? Go(GetRandomPort()) : pn;
    }
    finally
    {
      PortSemaphore.Release();
    }
  }

  // ReSharper disable once ReturnTypeCanBeEnumerable.Local
  private static TcpConnectionInformation[] GetConnectionInfo() =>
    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();

  //Initialize(): Inicializa la configuración de pruebas y devuelve una instancia de TestSetup.
  public static async Task<TestSetup> Initialize()
  {
    var baseUrl = await WebAppTracker.Get();
    return new(baseUrl);
  }

  private static class WebAppTracker
  {
    private static int handleCount;
    private static (WebApplication app, string url)? setup;
    private static readonly SemaphoreSlim TrackerSemaphore = new(1);

    internal static async Task<string> Get()
    {
      await TrackerSemaphore.WaitAsync();
      try
      {
        Interlocked.Increment(ref handleCount);
        if (setup.HasValue)
        {
          return setup.Value.url;
        }

        var sitePort = await GetFreePort();
        var baseUrl = $"http://localhost:{sitePort}/";
        var app = CachedInventoryApiBuilder.Build(["--urls", baseUrl]);
        await app.StartAsync();
        setup = (app, baseUrl);

        return setup.Value.url;
      }
      finally { TrackerSemaphore.Release(); }
    }

    internal static async Task TryDispose()
    {
      await TrackerSemaphore.WaitAsync();
      try
      {
        Interlocked.Decrement(ref handleCount);
        if (handleCount == 0 && setup.HasValue)
        {
          await setup.Value.app.DisposeAsync();
          setup = null;
        }
      }
      finally { TrackerSemaphore.Release(); }
    }
  }
}
