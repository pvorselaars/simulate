# Simulate

**Simulate** is a lightweight .NET framework for running and measuring scenarios with one or many independent virtual users. It supports structured logging, metrics, and tracing to help you analyze simulation outcomes and performance with full observability.

## Features

- Run a scenario with any number of **virtual users**, each with its own state and its own loop (no lock-step between users)
- **Ramp-up**, **think time**, **warm-up**, a **duration** and/or a number of **iterations per user**
- Per-user **setup and teardown** (for example a logged in browser), with limited parallel setup
- Supports:
  - `System.Diagnostics.Metrics` for metrics
  - `System.Diagnostics.ActivitySource` for tracing
  - `ILogger` for structured logging
- Emits OpenTelemetry-compatible metrics and spans; every iteration is its own trace
- Easy to test and extend

## Installation

Add the package via NuGet:

```bash
dotnet package add Simulate
```

## Usage

A scenario without state runs once by default:

```csharp
var results = await new Simulation("example", async logger =>
{
    logger.LogInformation("Relevant information");
    await Task.Delay(100); // Simulated task
    return true;           // Successful simulation result
})
.RunFor(TimeSpan.FromSeconds(5))
.Run();

Console.WriteLine($"Successes: {results.Successes}");
Console.WriteLine($"Failures: {results.Failures}");
```

## Virtual users

Use `Simulation<TUser>` when every user carries state between iterations (a logged in browser, a session) and
users should work independently:

```csharp
var results = await new Simulation<IPage>("view assessment",
        setup: async (index, logger) => await LoginAsync(index),   // once per user, not measured
        scenario: async (page, logger) =>
        {
            await page.GotoAsync("/dashboard");
            return true;
        },
        teardown: async page => await page.CloseAsync())             // always runs
    .WithUsers(10)
    .RampUp(TimeSpan.FromSeconds(30))                                // user i starts after i/10 of 30 s
    .ThinkTime(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))     // random pause between iterations
    .Warmup(1)                                                       // first iteration per user is not recorded
    .MaxParallelSetup(2)                                             // do not flood a login server
    .RunFor(TimeSpan.FromMinutes(5))
    .Run();
```

- Every user runs its own loop; there is no barrier between users, so a slow iteration of one user does not hold back the others.
- Without `RunFor`, every user runs the scenario once. `RunFor(duration)` counts from the end of the setup phase and includes the ramp-up; iterations in flight complete. `RunFor(iterationsPerUser)` can be combined with a duration: whichever comes first.
- Every iteration is its own trace (`simulate.user` and `simulate.iteration` tags, error status on failure), so `Activity.Current` inside the scenario is the iteration to propagate, for example as a `traceparent` header.
- `results.Results` holds one entry per recorded iteration including the `User` index (also in `ToCSV`). Warm-up iterations are not included.
- If a user cannot be set up, `Run` throws after tearing down the users that were set up. Cancelling the token passed to `Run` stops the users after their current iteration and returns the results so far.
- Need several load levels? Run one simulation per level and give each its own name.

## Metrics

Simulate emits three main metrics:

| Metric Name            | Type      | Description                     |
| ---------------------- | --------- | ------------------------------- |
| `simulations.ok`       | Counter   | Total number of successful runs |
| `simulations.fail`     | Counter   | Total number of failed runs     |
| `simulations.duration` | Histogram | Time taken per iteration (ms)   |

All metrics are tagged with:

* `scenario` – the name of the simulation

`simulations.duration` is also tagged with `success` (`true` or `false`).

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
var serviceVersion = "0.1.0";

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
```

Place this configuration before running your simulation:

```csharp
await new Simulation("example", async _ =>
{
    await Task.Delay(100);
    return true;
}, loggerFactory)
.WithUsers(5)
.RunFor(TimeSpan.FromSeconds(3))
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

    var results = await new Simulation("test", async _ =>
    {
        await Task.Delay(10);
        return true;
    })
    .RunFor(TimeSpan.FromSeconds(1))
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
