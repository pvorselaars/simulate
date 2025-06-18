﻿using System.Diagnostics.Metrics;
using System.Collections.Generic;
using System.Diagnostics;

namespace Simulate.Tests;

[TestClass]
public class SimulationTests
{
    public record MetricSample(string InstrumentName, object Value, IEnumerable<KeyValuePair<string, object?>> Tags);
    public record ActivitySample(string Name, ActivityKind Kind, ActivityStatusCode Status, string? StatusDescription);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Description("Ensures that simulations succeed correctly without options.")]
    public async Task Should_Succeed_Correctly_By_Default()
    {

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(0);
            return new Result(true);
        })
        .Run();

        Assert.IsTrue(results.Successes == 1);
        Assert.IsTrue(results.Failures == 0);
    }

    [TestMethod]
    [Description("Ensures that simulations fail correctly without options.")]
    public async Task Should_Fail_Correctly_By_Default()
    {

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(0);
            return new Result(false);
        })
        .Run();

        Assert.IsTrue(results.Successes == 0);
        Assert.IsTrue(results.Failures == 1);
    }

    [TestMethod]
    [Description("Ensures that simulations correctly record metrics.")]
    public async Task Should_Record_Metrics()
    {
        var recordedMetrics = new List<MetricSample>();

        var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) => l.EnableMeasurementEvents(instrument)
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            recordedMetrics.Add(new MetricSample(instrument.Name, measurement, tags.ToArray()));
        });

        meterListener.Start();

        var recordedActivities = new List<ActivitySample>();

        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Simulate",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                recordedActivities.Add(new ActivitySample(activity.DisplayName, activity.Kind, activity.Status, activity.StatusDescription));
            }
        };

        ActivitySource.AddActivityListener(activityListener);

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(10);
            return new Result(true);
        })
        .Run();

        meterListener.Dispose();

        long totalSuccesses = recordedMetrics
            .Where(m => m.InstrumentName == "simulations.ok")
            .Where(m => m.Tags.Any(t => t.Key == "scenario" && (string?)t.Value == "test"))
            .Sum(m => Convert.ToInt64(m.Value));

        long totalFailures = recordedMetrics
            .Where(m => m.InstrumentName == "simulations.fail")
            .Where(m => m.Tags.Any(t => t.Key == "scenario" && (string?)t.Value == "test"))
            .Sum(m => Convert.ToInt64(m.Value));

        Assert.AreEqual(1, totalSuccesses, "Total success count does not match.");
        Assert.AreEqual(0, totalFailures, "Total failure count does not match.");
    }

    [TestMethod]
    [Description("Ensures that simulations correctly record traces.")]
    public async Task Should_Record_Traces()
    {
        var recordedActivities = new List<ActivitySample>();

        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Simulate",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                recordedActivities.Add(new ActivitySample(activity.DisplayName, activity.Kind, activity.Status, activity.StatusDescription));
            }
        };

        ActivitySource.AddActivityListener(activityListener);

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(10);
            return new Result(true);
        })
        .Run();

        activityListener.Dispose();

        Assert.IsTrue(recordedActivities.Count == 1);
    }

    [TestMethod]
    [Description("Ensures that simulations correctly keep the rate constant.")]
    public async Task Should_Keep_Constant_Rate()
    {

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(1000);
            return new Result(true);
        })
        .RunFor(TimeSpan.FromSeconds(3), 1)
        .Run();

        Assert.IsTrue(results.Successes == 3);
    }

    [TestMethod]
    [Description("Ensures that simulations correctly increase the rate.")]
    public async Task Should_Increase_Rate()
    {

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(1000);
            return new Result(true);
        })
        .RunFor(duration: TimeSpan.FromSeconds(3), copies: 1, rate: 1)
        .Run();

        Assert.IsTrue(results.Successes == 6);
    }

    [TestMethod]
    [Description("Ensures that simulation intervals are correctly handled.")]
    public async Task Should_Accept_Multiple_Intervals()
    {

        var results = await new Simulation("test", async () =>
        {
            await Task.Delay(1000);
            return new Result(true);
        })
        .RunFor(duration: TimeSpan.FromSeconds(3), copies: 1, rate: 1)
        .RunFor(duration: TimeSpan.FromSeconds(3), copies: 1, rate: 1)
        .Run();

        Assert.IsTrue(results.Successes == 12);
    }

}
