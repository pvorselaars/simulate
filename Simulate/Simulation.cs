using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Simulate;

public record Result(bool Success);

public class Simulation(string name, Func<ILogger, Task<Result>> scenario)
{
    private static readonly string ServiceName = "Simulation";
    private static readonly string ServiceVersion = "0.1.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    private static readonly Counter<long> SuccessCounter = Meter.CreateCounter<long>(
        "simulation_success_total",
        description: "Total number of successful simulations");

    private readonly ILogger Logger = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
    }).CreateLogger<Simulation>();

    public async Task Run()
    {
        using var activity = ActivitySource.StartActivity(name);

        var sw = Stopwatch.StartNew();

        var result = await scenario.Invoke(Logger);

        if (result.Success)
        {
            SuccessCounter.Add(1, KeyValuePair.Create<string, object?>("scenario", name));
        }

        sw.Stop();
    }
}
