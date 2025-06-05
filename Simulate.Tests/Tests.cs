using System.Diagnostics.Metrics;
using Simulate;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Simulate.Tests;

[TestClass]
public class SimulationTests
{
    // Record to store metric info
    public record MetricSample(string InstrumentName, object Value, IEnumerable<KeyValuePair<string, object?>> Tags);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Should_Record_All_Metrics()
    {
        var recordedMetrics = new List<MetricSample>();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) => l.EnableMeasurementEvents(instrument)
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            recordedMetrics.Add(new MetricSample(instrument.Name, measurement, tags.ToArray()));
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            recordedMetrics.Add(new MetricSample(instrument.Name, measurement, tags.ToArray()));
        });

        listener.Start();

        var simulation = new Simulation("test", async _ =>
        {
            await Task.Delay(10);
            return new Result(true);
        });

        await simulation.Run();

        listener.Dispose();

        foreach (var metric in recordedMetrics)
        {
            TestContext.WriteLine($"Metric: {metric.InstrumentName}, Value: {metric.Value}, Tags: {string.Join(", ", metric.Tags)}");
        }

        Assert.IsTrue(recordedMetrics.Count > 0, "Expected at least one metric to be recorded.");
    }
}
