using System.Diagnostics.Metrics;
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

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            return true;
        })
        .Run();

        Assert.AreEqual(1, results.Successes);
        Assert.AreEqual(0, results.Failures);
    }

    [TestMethod]
    [Description("Ensures that simulations fail correctly without options.")]
    public async Task Should_Fail_Correctly_By_Default()
    {

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            return false;
        })
        .Run();

        Assert.AreEqual(0, results.Successes);
        Assert.AreEqual(1, results.Failures);
    }

    [TestMethod]
    [Description("Ensures that simulations that throw fail correctly without options.")]
    public async Task Uncaught_Exception_Should_Fail_Correctly_By_Default()
    {

        var results = await new Simulation("test", _ =>
        {
            throw new Exception();
        })
        .Run();

        Assert.AreEqual(0, results.Successes);
        Assert.AreEqual(1, results.Failures);
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

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            return true;
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

        Assert.AreEqual(1, totalSuccesses);
        Assert.AreEqual(0, totalFailures);
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

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            return true;
        })
        .Run();

        activityListener.Dispose();

        Assert.AreEqual(1, recordedActivities.Count);
    }

    [TestMethod]
    [Description("Ensures that a simulation keeps iterating until the duration has passed.")]
    public async Task Should_Keep_Iterating_For_The_Duration()
    {

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(100);
            return true;
        })
        .RunFor(TimeSpan.FromMilliseconds(1000))
        .Run();

        Assert.IsTrue(results.Successes is >= 9 and <= 11, $"Expected about 10 iterations, ran {results.Successes}");
    }

    [TestMethod]
    [Description("Ensures that a specific number of iterations is supported.")]
    public async Task Should_Support_Specific_Number_Of_Iterations()
    {

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            return true;
        })
        .RunFor(iterationsPerUser: 3)
        .Run();

        Assert.AreEqual(3U, results.TotalIterations);
        Assert.AreEqual(3, results.Successes);
    }

    [TestMethod]
    [Description("Ensures that every user runs the scenario once when no duration or iterations are set.")]
    public async Task Should_Run_Once_Per_User_By_Default()
    {
        var invocations = 0;

        var results = await new Simulation("test", async _ =>
        {
            await Task.Delay(0);
            Interlocked.Increment(ref invocations);
            return true;
        })
        .WithUsers(3)
        .Run();

        Assert.AreEqual(3, invocations);
        Assert.AreEqual(3U, results.TotalIterations);
    }

}
