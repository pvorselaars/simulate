# Simulate

**Simulate** is a lightweight and extensible .NET simulation framework designed for running and measuring concurrent task executions. It supports structured logging, metrics, and tracing to help you analyze simulation outcomes and performance with full observability.

## Features

- Run simulations with a configurable **duration**, **number of iterations**, **copy count**, and **increasing rate**
- Supports:
  - `System.Diagnostics.Metrics` for metrics
  - `System.Diagnostics.ActivitySource` for tracing
  - `ILogger` for structured logging
- Emits OpenTelemetry-compatible metrics and spans
- Easy to test and extend

## Installation

Add the package via NuGet:

```bash
dotnet package add Simulate
````

## Usage

```csharp
var simulation = await new Simulation("example", async logger =>
{
    logger.LogInformation("Relevant information");
    await Task.Delay(100); // Simulated task
    return true; // Successful simulation result
})
.RunFor(duration: TimeSpan.FromSeconds(5), copies: 5, rate: 2.0)
.Run();

Console.WriteLine($"Successes: {simulation.Results.Successes}");
Console.WriteLine($"Failures: {simulation.Results.Failures}");
```

## Metrics

Simulate emits three main metrics:

| Metric Name            | Type      | Description                     |
| ---------------------- | --------- | ------------------------------- |
| `simulations.ok`       | Counter   | Total number of successful runs |
| `simulations.fail`     | Counter   | Total number of failed runs     |
| `simulations.duration` | Histogram | Time taken per simulation (ms)  |

All metrics are tagged with:

* `scenario` – the name of the simulation

## Exporting with OpenTelemetry

You can integrate OpenTelemetry to export traces, metrics, and logs from `Simulate` to the console or any OpenTelemetry-compatible backend (like Jaeger, Prometheus, or OTLP).

### Example: Export to Console

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using Microsoft.Extensions.Logging;

var serviceName = "Simulate";
var serviceVersion = "0.0.7";

// Configure logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddOpenTelemetry(options =>
    {
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.AddConsoleExporter();
    });
});

// Configure tracing
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(serviceName)
    .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .AddConsoleExporter()
    .Build();

// Configure metrics
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(serviceName)
    .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .AddConsoleExporter()
    .Build();

// Configure logging
using var loggerFactory = LoggerFactory.Create(builder =>
                            {
                                builder.AddOpenTelemetry(logging =>
                                {
                                    logging.AddOtlpExporter(options => options.Endpoint = new Uri("http://your-otlp-endpoint:4317"));
                                }
                            });
```

Place this configuration before running your simulation:

```csharp
await new Simulation("example", async _ =>
{
    await Task.Delay(100);
    return true;
}, loggerFactory)
.RunFor(TimeSpan.FromSeconds(3), copies: 5, rate: 2)
.Run();
```

## Testing

Use `MeterListener`, `ActivityListener`, and structured log capturing to test your simulation results.

```csharp
[TestMethod]
public async Task Should_Record_Metrics()
{
    var metrics = new List<string>();
    using var listener = new MeterListener();

    listener.InstrumentPublished = (inst, _) => listener.EnableMeasurementEvents(inst);
    listener.SetMeasurementEventCallback<long>((inst, measurement, tags, _) =>
    {
        metrics.Add($"{inst.Name}: {measurement}");
    });
    listener.Start();

    var results = await new Simulation("test", async () =>
    {
        await Task.Delay(10);
        return true;
    })
    .RunFor(TimeSpan.FromSeconds(1), copies: 1)
    .Run();

    Assert.IsTrue(results.Successes > 0);
    Assert.IsTrue(metrics.Count > 0);
}
```

## 📄 License

This project is licensed under the MIT License. See [LICENSE](./LICENSE) for details.

## 🤝 Contributing

Contributions, suggestions, and issues are welcome. Please open a PR or file an issue in GitHub.

## 📬 Contact

Questions? Reach out on GitHub or submit an issue.
