using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Simulate
{
    /// <summary>
    /// Runs a scenario with a number of independent virtual users. Every user is set up once
    /// (for example a logged in browser), then loops over the scenario on its own, so a slow
    /// iteration of one user never holds back the others.
    /// </summary>
    /// <typeparam name="TUser">The state one virtual user carries between iterations.</typeparam>
    /// <remarks>
    /// Without <see cref="RunFor(TimeSpan)"/> or <see cref="RunFor(uint)"/> every user runs the scenario once.
    /// Use <see cref="Simulation"/> when the scenario needs no per-user state.
    /// </remarks>
    public class Simulation<TUser>
    {
        private readonly string name;
        private readonly Func<int, ILogger, ValueTask<TUser>> setup;
        private readonly Func<TUser, ILogger, ValueTask<bool>> scenario;
        private readonly Func<TUser, ValueTask>? teardown;
        private readonly ILogger logger;

        private int users = 1;
        private TimeSpan rampUp = TimeSpan.Zero;
        private TimeSpan thinkMin = TimeSpan.Zero;
        private TimeSpan thinkMax = TimeSpan.Zero;
        private uint warmupIterations;
        private int maxParallelSetup = 4;
        private TimeSpan? duration;
        private uint? iterationsPerUser;

        /// <param name="name">The name of the simulation scenario.</param>
        /// <param name="setup">Creates the state of one user. Receives the zero-based user index. Runs before the timed part and is not measured.</param>
        /// <param name="scenario">One iteration for one user.</param>
        /// <param name="teardown">Cleans up one user. Runs even when the simulation fails.</param>
        /// <param name="loggerFactory">Where to log to; defaults to the console.</param>
        public Simulation(string name,
                          Func<int, ILogger, ValueTask<TUser>> setup,
                          Func<TUser, ILogger, ValueTask<bool>> scenario,
                          Func<TUser, ValueTask>? teardown = null,
                          ILoggerFactory? loggerFactory = null)
        {
            this.name = name;
            this.setup = setup;
            this.scenario = scenario;
            this.teardown = teardown;

            loggerFactory ??= LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddConsole();
            });

            logger = loggerFactory.CreateLogger("Simulate.Simulation");
        }

        /// <summary>Gets the aggregated results after <see cref="Run"/>. Warm-up iterations are not included.</summary>
        public SimulationResults Results { get; } = new();

        /// <summary>Sets the number of concurrent virtual users (at least 1, default 1).</summary>
        public Simulation<TUser> WithUsers(int users)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(users, 1);
            this.users = users;
            return this;
        }

        /// <summary>
        /// Spreads the start of the users evenly over this period: user i starts after i / users of it,
        /// so the first user starts immediately. Counts towards the duration set by <see cref="RunFor(TimeSpan)"/>.
        /// </summary>
        public Simulation<TUser> RampUp(TimeSpan period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero);
            rampUp = period;
            return this;
        }

        /// <summary>Waits a fixed time between the iterations of a user.</summary>
        public Simulation<TUser> ThinkTime(TimeSpan time) => ThinkTime(time, time);

        /// <summary>Waits a random time between min and max between the iterations of a user.</summary>
        public Simulation<TUser> ThinkTime(TimeSpan min, TimeSpan max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(min, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
            thinkMin = min;
            thinkMax = max;
            return this;
        }

        /// <summary>
        /// Runs this many first iterations per user that are not recorded (no metrics, not in the results),
        /// so cold caches and JIT do not skew the numbers. They still run, and their traces carry <c>simulate.warmup</c>.
        /// </summary>
        public Simulation<TUser> Warmup(uint iterationsPerUser)
        {
            warmupIterations = iterationsPerUser;
            return this;
        }

        /// <summary>Limits how many users are set up at the same time (default 4), for example to not flood a login server.</summary>
        public Simulation<TUser> MaxParallelSetup(int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            maxParallelSetup = count;
            return this;
        }

        /// <summary>
        /// Stops starting new iterations after this time, measured from the start of the timed part
        /// (after all users are set up). Iterations in flight complete.
        /// </summary>
        public Simulation<TUser> RunFor(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            this.duration = duration;
            return this;
        }

        /// <summary>Stops each user after this many iterations, warm-up iterations included. Can be combined with a duration: whichever comes first.</summary>
        public Simulation<TUser> RunFor(uint iterationsPerUser)
        {
            ArgumentOutOfRangeException.ThrowIfZero(iterationsPerUser);
            this.iterationsPerUser = iterationsPerUser;
            return this;
        }

        /// <summary>
        /// Sets up the users, runs them concurrently and tears them down. If a user cannot be set up,
        /// the exception is thrown after the users that were set up are torn down; no iterations run.
        /// Cancelling stops the users after their current iteration and returns the results so far.
        /// </summary>
        public async Task<SimulationResults> Run(CancellationToken cancellationToken = default)
        {
            var iterations = duration is null ? iterationsPerUser ?? 1 : iterationsPerUser;

            var state = new TUser?[users];
            var ready = new bool[users];

            try
            {
                await SetupUsers(state, ready, cancellationToken);

                var clock = Stopwatch.StartNew();
                var perUser = await Task.WhenAll(Enumerable.Range(0, users)
                    .Select(index => Task.Run(() => RunUser(index, state[index]!, iterations, clock, cancellationToken), CancellationToken.None)));

                var results = perUser.SelectMany(r => r).ToList();

                Results.Name = name;
                Results.Users = users;
                Results.Elapsed = clock.Elapsed;

                if (results.Count == 0)
                    return Results;

                Results.Summarize(results);
                Results.Print();

                return Results;
            }
            finally
            {
                await TeardownUsers(state, ready);
            }
        }

        private async Task SetupUsers(TUser?[] state, bool[] ready, CancellationToken cancellationToken)
        {
            using var gate = new SemaphoreSlim(maxParallelSetup);

            await Task.WhenAll(Enumerable.Range(0, users).Select(async index =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    state[index] = await setup(index, logger);
                    ready[index] = true;
                    logger.LogInformation("Set up user {User} of scenario '{Scenario}'", index, name);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        private async Task TeardownUsers(TUser?[] state, bool[] ready)
        {
            if (teardown is null)
                return;

            await Task.WhenAll(Enumerable.Range(0, users).Where(index => ready[index]).Select(async index =>
            {
                try
                {
                    await teardown(state[index]!);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Teardown of user {User} of scenario '{Scenario}' failed:", index, name);
                }
            }));
        }

        private async Task<List<SimulationResult>> RunUser(int index, TUser user, uint? iterations, Stopwatch clock, CancellationToken cancellationToken)
        {
            // This task has its own copy of the execution context: dropping the caller's activity here
            // makes every iteration a root trace without touching the caller.
            Activity.Current = null;

            var results = new List<SimulationResult>();

            try
            {
                var startDelay = TimeSpan.FromTicks(rampUp.Ticks * index / users);
                if (startDelay > TimeSpan.Zero)
                    await Task.Delay(startDelay, cancellationToken);

                for (uint iteration = 1; !cancellationToken.IsCancellationRequested; iteration++)
                {
                    if (iterations is not null && iteration > iterations)
                        break;

                    if (duration is not null && clock.Elapsed >= duration)
                        break;

                    var warmup = iteration <= warmupIterations;
                    var result = await RunIteration(index, iteration, user, warmup);

                    if (!warmup)
                        results.Add(result);

                    var think = NextThinkTime();
                    if (think > TimeSpan.Zero && (iterations is null || iteration < iterations))
                    {
                        if (duration is not null)
                            think = TimeSpan.FromTicks(Math.Min(think.Ticks, (duration.Value - clock.Elapsed).Ticks));

                        if (think > TimeSpan.Zero)
                            await Task.Delay(think, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // stopped on request, keep what has been measured
            }

            return results;
        }

        private async Task<SimulationResult> RunIteration(int index, uint iteration, TUser user, bool warmup)
        {
            // Activity.Current is cleared per user task (see RunUser), so every iteration is its own trace.
            using var activity = SimulateTelemetry.ActivitySource.StartActivity(name);
            activity?.SetTag("simulate.user", index);
            activity?.SetTag("simulate.iteration", iteration);
            if (warmup)
                activity?.SetTag("simulate.warmup", true);

            var sw = Stopwatch.StartNew();

            bool success;
            string? failure = null;

            try
            {
                success = await scenario(user, logger);
                if (!success)
                    failure = "The scenario returned false";
            }
            catch (Exception e)
            {
                logger.LogError(e, "Scenario '{Scenario}' failed for user {User}:", name, index);
                activity?.AddException(e);
                success = false;
                failure = e.Message;
            }

            sw.Stop();

            if (!success)
                activity?.SetStatus(ActivityStatusCode.Error, failure);

            if (!warmup)
            {
                var scenarioTag = KeyValuePair.Create<string, object?>("scenario", name);
                var outcomeTag = KeyValuePair.Create<string, object?>("success", success);

                SimulateTelemetry.DurationHistogram.Record(sw.ElapsedMilliseconds, scenarioTag, outcomeTag);

                if (success)
                    SimulateTelemetry.SuccessCounter.Add(1, scenarioTag);
                else
                    SimulateTelemetry.FailureCounter.Add(1, scenarioTag);
            }

            return new SimulationResult
            {
                Iteration = iteration,
                Success = success,
                Duration = sw.ElapsedMilliseconds,
                User = index
            };
        }

        private TimeSpan NextThinkTime()
        {
            if (thinkMax <= thinkMin)
                return thinkMin;

            return TimeSpan.FromTicks(Random.Shared.NextInt64(thinkMin.Ticks, thinkMax.Ticks + 1));
        }
    }

    /// <summary>
    /// A <see cref="Simulation{TUser}"/> for scenarios that need no per-user state.
    /// </summary>
    public class Simulation : Simulation<object?>
    {
        /// <param name="name">The name of the simulation scenario.</param>
        /// <param name="scenario">One iteration.</param>
        /// <param name="loggerFactory">Where to log to; defaults to the console.</param>
        public Simulation(string name,
                          Func<ILogger, ValueTask<bool>> scenario,
                          ILoggerFactory? loggerFactory = null)
            : base(name,
                   (_, _) => ValueTask.FromResult<object?>(null),
                   (_, logger) => scenario(logger),
                   teardown: null,
                   loggerFactory)
        {
        }
    }
}
