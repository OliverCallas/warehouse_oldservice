// ReSharper disable ClassNeverInstantiated.Global

namespace CachedInventory.Tests;

public class SingleRetrieval
{
  [Fact(DisplayName = "retirar un producto")]
  public static async Task Test() => await TestApiPerformance.Test(1, [3], false, 2_000);
}

public class FourRetrievalsInParallel
{
  [Fact(DisplayName = "retirar cuatro productos en paralelo")]
  public static async Task Test() => await TestApiPerformance.Test(2, [1, 2, 3, 4], true, 1_000);
}

public class FourRetrievalsSequentially
{
  [Fact(DisplayName = "retirar cuatro productos secuencialmente")]
  public static async Task Test() => await TestApiPerformance.Test(3, [1, 2, 3, 4], false, 1_000);
}

public class SevenRetrievalsInParallel
{
  [Fact(DisplayName = "retirar siete productos en paralelo")]
  public static async Task Test() => await TestApiPerformance.Test(4, [1, 2, 3, 4, 5, 6, 7], true, 500);
}

public class SevenRetrievalsSequentially
{
  [Fact(DisplayName = "retirar siete productos secuencialmente")]
  public static async Task Test() => await TestApiPerformance.Test(5, [1, 2, 3, 4, 5, 6, 7], false, 500);
}

/*
Test(int productId, int[] retrievals, bool isParallel, long expectedPerformance): Método que ejecuta las pruebas para los 
diferentes casos. Realiza las siguientes tareas:
-Inicializa el entorno de pruebas.
-Repone el stock necesario.
-Verifica que el stock en el archivo coincida con el esperado.
-Ejecuta las solicitudes de retiro de stock, ya sea en paralelo o secuencialmente.
-Verifica que el stock final sea cero y que la duración promedio de las solicitudes sea menor que el rendimiento esperado.
-Verifica que el stock en el archivo sea cero después de las operaciones.
*/
internal static class TestApiPerformance
{
  internal static async Task Test(int productId, int[] retrievals, bool isParallel, long expectedPerformance)
  {
    await using var setup = await TestSetup.Initialize();
    await setup.Restock(productId, retrievals.Sum());
    await setup.VerifyStockFromFile(productId, retrievals.Sum());
    var tasks = new List<Task>();
    foreach (var retrieval in retrievals)
    {
      var task = setup.Retrieve(productId, retrieval);
      if (!isParallel)
      {
        await task;
      }

      tasks.Add(task);
    }

    await Task.WhenAll(tasks);
    var finalStock = await setup.GetStock(productId);
    Assert.True(finalStock == 0, $"El stock final no es 0, sino {finalStock}.");
    Assert.True(
      setup.AverageRequestDuration < expectedPerformance,
      $"Duración promedio: {setup.AverageRequestDuration}ms, se esperaba un máximo de {expectedPerformance}ms.");
    await setup.VerifyStockFromFile(productId, 0);
  }
}
