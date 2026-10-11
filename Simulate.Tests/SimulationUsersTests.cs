using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Simulate.Tests;

[TestClass]
public class SimulationUsersTests
{
    private static Simulation<int> Create(string name, Func<int, ValueTask<bool>> scenario, Func<int, ValueTask>? teardown = null) =>
        new(name, (index, _) => ValueTask.FromResult(index), (user, _) => scenario(user), teardown);

    private static string UniqueName() => "user-simulation-" + Guid.NewGuid().ToString("N");

    [TestMethod]
    [Description("Ensures every user is set up once, runs its own iterations and is torn down.")]
    public async Task Should_Set_Up_Run_And_Tear_Down_Every_User()
    {
        var setups = new ConcurrentBag<int>();
        var teardowns = new ConcurrentBag<int>();

        var results = await new Simulation<int>(UniqueName(),
                (index, _) => { setups.Add(index); return ValueTask.FromResult(index); },
                (_, _) => ValueTask.FromResult(true),
                user => { teardowns.Add(user); return ValueTask.CompletedTask; })
            .WithUsers(3)
            .RunFor(iterationsPerUser: 2)
            .Run();

        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, setups.ToArray());
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, teardowns.ToArray());
        Assert.AreEqual(6U, results.TotalIterations);
        Assert.AreEqual(6, results.Successes);
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, results.Results.Select(r => r.User).Distinct().ToArray());
    }

    [TestMethod]
    [Description("Ensures a slow user does not hold back the others, as it would with lock-step batches.")]
    public async Task Users_Should_Run_Independently()
    {
        var completions = new ConcurrentQueue<int>();

        await Create(UniqueName(), async user =>
            {
                if (user == 0)
                    await Task.Delay(200);

                completions.Enqueue(user);
                return true;
            })
            .WithUsers(2)
            .RunFor(iterationsPerUser: 5)
            .Run();

        var order = completions.ToList();
        var fifthFastCompletion = IndexOfNth(order, 1, 5);
        var secondSlowCompletion = IndexOfNth(order, 0, 2);

        Assert.IsTrue(fifthFastCompletion < secondSlowCompletion,
            $"The fast user should have finished all iterations before the slow user finished its second: {string.Join(",", order)}");
    }

    [TestMethod]
    [Description("Ensures the start of the users is spread over the ramp-up period.")]
    public async Task Should_Stagger_Users_During_Ramp_Up()
    {
        var starts = new ConcurrentDictionary<int, long>();
        var clock = Stopwatch.StartNew();

        await Create(UniqueName(), user =>
            {
                starts.TryAdd(user, clock.ElapsedMilliseconds);
                return ValueTask.FromResult(true);
            })
            .WithUsers(3)
            .RampUp(TimeSpan.FromMilliseconds(600))
            .RunFor(iterationsPerUser: 1)
            .Run();

        var ordered = starts.OrderBy(s => s.Key).Select(s => s.Value).ToArray();

        Assert.AreEqual(3, ordered.Length);
        Assert.IsTrue(ordered[1] - ordered[0] >= 150, $"Expected ~200 ms between user 0 and 1: {string.Join(",", ordered)}");
        Assert.IsTrue(ordered[2] - ordered[1] >= 150, $"Expected ~200 ms between user 1 and 2: {string.Join(",", ordered)}");
    }

    [TestMethod]
    [Description("Ensures warm-up iterations run but are not recorded.")]
    public async Task Should_Exclude_Warmup_From_Results_And_Metrics()
    {
        var name = UniqueName();
        var invocations = 0;
        var durations = new ConcurrentBag<IEnumerable<KeyValuePair<string, object?>>>();
        var counted = 0L;

        using var meterListener = new MeterListener { InstrumentPublished = (instrument, l) => l.EnableMeasurementEvents(instrument) };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var tagList = tags.ToArray();
            if (!tagList.Any(t => t.Key == "scenario" && (string?)t.Value == name))
                return;

            if (instrument.Name == "simulations.duration")
                durations.Add(tagList);
            else if (instrument.Name == "simulations.ok")
                Interlocked.Add(ref counted, measurement);
        });
        meterListener.Start();

        var results = await Create(name, _ =>
            {
                Interlocked.Increment(ref invocations);
                return ValueTask.FromResult(true);
            })
            .WithUsers(2)
            .Warmup(2)
            .RunFor(iterationsPerUser: 5)
            .Run();

        meterListener.Dispose();

        Assert.AreEqual(10, invocations);
        Assert.AreEqual(6U, results.TotalIterations);
        Assert.IsTrue(results.Results.All(r => r.Iteration > 2));
        Assert.AreEqual(6, durations.Count);
        Assert.AreEqual(6L, counted);
        Assert.IsTrue(durations.All(tags => tags.Any(t => t.Key == "success" && t.Value is true)));
    }

    [TestMethod]
    [Description("Ensures failed and throwing iterations are counted and marked as errors on their trace.")]
    public async Task Should_Mark_Failed_Iterations_As_Errors_In_Traces()
    {
        var name = UniqueName();
        var recorded = new ConcurrentBag<(ActivityStatusCode Status, object? User, object? Iteration)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Simulate",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == name)
                    recorded.Add((activity.Status, activity.GetTagItem("simulate.user"), activity.GetTagItem("simulate.iteration")));
            }
        };
        ActivitySource.AddActivityListener(listener);

        var calls = 0;
        var results = await Create(name, _ =>
            {
                return Interlocked.Increment(ref calls) switch
                {
                    1 => ValueTask.FromResult(false),
                    2 => throw new InvalidOperationException("boom"),
                    _ => ValueTask.FromResult(true),
                };
            })
            .RunFor(iterationsPerUser: 3)
            .Run();

        Assert.AreEqual(2, results.Failures);
        Assert.AreEqual(1, results.Successes);
        Assert.AreEqual(3, recorded.Count);
        Assert.AreEqual(2, recorded.Count(r => r.Status == ActivityStatusCode.Error));
        Assert.IsTrue(recorded.All(r => (int?)r.User == 0));
    }

    [TestMethod]
    [Description("Ensures each iteration is its own trace, even when the caller is inside an activity.")]
    public async Task Iterations_Should_Be_Root_Traces()
    {
        var name = UniqueName();
        var recorded = new ConcurrentBag<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Simulate",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == name)
                    recorded.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var caller = new Activity("caller").Start();
        try
        {
            await Create(name, _ => ValueTask.FromResult(true))
                .WithUsers(2)
                .RunFor(iterationsPerUser: 2)
                .Run();
        }
        finally
        {
            caller.Stop();
        }

        Assert.AreEqual(4, recorded.Count);
        Assert.IsTrue(recorded.All(a => a.ParentId is null));
        Assert.IsTrue(recorded.All(a => a.TraceId != caller.TraceId));
        Assert.AreEqual(4, recorded.Select(a => a.TraceId).Distinct().Count());
    }

    [TestMethod]
    [Description("Ensures the think time is waited between iterations.")]
    public async Task Should_Wait_Think_Time_Between_Iterations()
    {
        var sw = Stopwatch.StartNew();

        await Create(UniqueName(), _ => ValueTask.FromResult(true))
            .ThinkTime(TimeSpan.FromMilliseconds(100))
            .RunFor(iterationsPerUser: 3)
            .Run();

        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds >= 190, $"Two pauses of 100 ms expected, took {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    [Description("Ensures a long think time does not run past the duration.")]
    public async Task Think_Time_Should_Not_Run_Past_The_Duration()
    {
        var sw = Stopwatch.StartNew();

        var results = await Create(UniqueName(), _ => ValueTask.FromResult(true))
            .ThinkTime(TimeSpan.FromSeconds(30))
            .RunFor(TimeSpan.FromMilliseconds(300))
            .Run();

        sw.Stop();

        Assert.IsTrue(results.TotalIterations >= 1);
        Assert.IsTrue(sw.ElapsedMilliseconds < 5000, $"Took {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    [Description("Ensures users keep iterating until the duration has passed.")]
    public async Task Should_Run_Users_For_A_Duration()
    {
        var results = await Create(UniqueName(), async _ =>
            {
                await Task.Delay(100);
                return true;
            })
            .WithUsers(2)
            .RunFor(TimeSpan.FromMilliseconds(500))
            .Run();

        foreach (var user in new[] { 0, 1 })
            Assert.IsTrue(results.Results.Count(r => r.User == user) >= 3, $"User {user} ran too few iterations");
    }

    [TestMethod]
    [Description("Ensures a failing setup stops the run and still tears down the users that were set up.")]
    public async Task Failing_Setup_Should_Stop_The_Run_And_Tear_Down_The_Others()
    {
        var iterations = 0;
        var teardowns = new ConcurrentBag<int>();

        var simulation = new Simulation<int>(UniqueName(),
                (index, _) => index == 1 ? throw new InvalidOperationException("login failed") : ValueTask.FromResult(index),
                (_, _) =>
                {
                    Interlocked.Increment(ref iterations);
                    return ValueTask.FromResult(true);
                },
                user => { teardowns.Add(user); return ValueTask.CompletedTask; })
            .WithUsers(3)
            .RunFor(iterationsPerUser: 1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => simulation.Run());

        Assert.AreEqual(0, iterations);
        CollectionAssert.AreEquivalent(new[] { 0, 2 }, teardowns.ToArray());
    }

    [TestMethod]
    [Description("Ensures teardown also runs when the iterations throw.")]
    public async Task Should_Tear_Down_When_The_Scenario_Throws()
    {
        var teardowns = 0;

        var results = await Create(UniqueName(), _ => throw new InvalidOperationException(),
                _ => { Interlocked.Increment(ref teardowns); return ValueTask.CompletedTask; })
            .WithUsers(2)
            .RunFor(iterationsPerUser: 1)
            .Run();

        Assert.AreEqual(2, results.Failures);
        Assert.AreEqual(2, teardowns);
    }

    [TestMethod]
    [Description("Ensures the number of users that are set up at the same time can be limited.")]
    public async Task Should_Limit_Parallel_Setup()
    {
        var running = 0;
        var maxRunning = 0;

        await new Simulation<int>(UniqueName(),
                async (index, _) =>
                {
                    var now = Interlocked.Increment(ref running);
                    InterlockedMax(ref maxRunning, now);
                    await Task.Delay(50);
                    Interlocked.Decrement(ref running);
                    return index;
                },
                (_, _) => ValueTask.FromResult(true))
            .WithUsers(6)
            .MaxParallelSetup(2)
            .RunFor(iterationsPerUser: 1)
            .Run();

        Assert.IsTrue(maxRunning is >= 1 and <= 2, $"At most 2 setups at a time expected, saw {maxRunning}");
    }

    [TestMethod]
    [Description("Ensures cancelling stops the users and keeps the results so far.")]
    public async Task Cancelling_Should_Return_Results_So_Far()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var results = await Create(UniqueName(), async _ =>
            {
                await Task.Delay(50);
                return true;
            })
            .WithUsers(2)
            .RunFor(TimeSpan.FromMinutes(5))
            .Run(cts.Token);

        Assert.IsTrue(results.TotalIterations >= 2);
    }

    [TestMethod]
    [Description("Ensures the user is part of the CSV export.")]
    public async Task Should_Export_The_User_To_Csv()
    {
        var results = await Create(UniqueName(), _ => ValueTask.FromResult(true))
            .WithUsers(2)
            .RunFor(iterationsPerUser: 1)
            .Run();

        var path = Path.GetTempFileName();
        try
        {
            results.ToCSV(path);
            var lines = File.ReadAllLines(path);

            Assert.AreEqual("Iteration,Success,Duration,User", lines[0]);
            Assert.AreEqual(3, lines.Length);
            Assert.IsTrue(lines.Skip(1).Any(l => l.EndsWith(",1")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [Description("Ensures invalid configuration is rejected.")]
    public void Should_Reject_Invalid_Configuration()
    {
        var simulation = Create(UniqueName(), _ => ValueTask.FromResult(true));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.WithUsers(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.RunFor(0U));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.RunFor(TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.RampUp(TimeSpan.FromSeconds(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.ThinkTime(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.MaxParallelSetup(0));
    }

    [TestMethod]
    [Description("Ensures the report contains the totals and a breakdown per user.")]
    public async Task Report_Should_Contain_Totals_And_A_Breakdown_Per_User()
    {
        var results = await Create("report-test", _ => ValueTask.FromResult(true))
            .WithUsers(2)
            .RunFor(iterationsPerUser: 3)
            .Run();

        var report = results.ToReport();

        StringAssert.Contains(report, "Simulation 'report-test': 6 iterations by 2 user(s)");
        StringAssert.Contains(report, "Successes: 6, failures: 0");
        StringAssert.Contains(report, "p95");
        StringAssert.Contains(report, "IQR");
        StringAssert.Contains(report, "standard error");
        StringAssert.Contains(report, "median skewness");
        StringAssert.Contains(report, "mode skewness");
        StringAssert.Contains(report, "Per user:");
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(report, @"^\s+[01]\s+3\s+0\s", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [TestMethod]
    [Description("Ensures a single user report has no per user breakdown.")]
    public async Task Report_Should_Skip_The_Breakdown_For_One_User()
    {
        var results = await new Simulation("solo", _ => ValueTask.FromResult(true)).Run();

        var report = results.ToReport();

        StringAssert.Contains(report, "Simulation 'solo': 1 iterations by 1 user(s)");
        Assert.IsFalse(report.Contains("Per user:"));
    }

    private static int IndexOfNth(List<int> items, int value, int n)
    {
        var seen = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] == value && ++seen == n)
                return i;
        }

        throw new AssertFailedException($"Expected at least {n} completions of user {value}: {string.Join(",", items)}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
