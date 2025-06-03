using Simuload;
namespace Tests;

[TestClass]
public sealed class Tests
{
    [TestMethod]
    public async Task CreateSimulation()
    {
        await new Simulation(async ctx =>
        {
            await Task.Delay(1000);
            return new Outcome();
        })
        .Run();
    }
}
