using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Simuload;

public record Result;
public record Outcome;

public class Simulation(Func<object, Task<Outcome>> scenario)
{

    public ILogger logger = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information)
                .AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                                .AddService("Simuload"))
                                .AddConsoleExporter();
                });

        }).CreateLogger<Simulation>();

    public async Task Run()
    {

        Stopwatch sw = new();

        logger.LogTrace("Starting simulation");
        sw.Start();
        await scenario.Invoke(logger);
        sw.Stop();
        logger.LogTrace("Ending simulation");
        logger.LogInformation($"Elapsed={sw.Elapsed}");
    }

};
