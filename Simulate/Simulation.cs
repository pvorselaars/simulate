using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Simulate
{
    /// <summary>
    /// Represents the outcome of a single simulation execution.
    /// </summary>
    /// <param name="Success">Indicates whether the simulation succeeded.</param>
    public record Result(bool Success);

    /// <summary>
    /// Runs a simulation scenario multiple times, tracking metrics and results.
    /// Supports parallel execution, metrics collection, and rate-controlled runs.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Simulation"/> class.
    /// </remarks>
    /// <param name="name">The name of the simulation scenario.</param>
    /// <param name="scenario">The asynchronous simulation function to execute.</param>
    public class Simulation(string name, Func<ValueTask<Result>> scenario)
    {
        private const string ServiceName = "Simulate";
        private const string ServiceVersion = "0.0.1";

        private static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
        private static readonly Meter Meter = new(ServiceName, ServiceVersion);
        private static readonly Counter<long> SuccessCounter = Meter.CreateCounter<long>("simulations.ok", description: "Total number of successful simulations");
        private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("simulations.fail", description: "Total number of failed simulations");
        private static readonly Histogram<long> DurationHistogram = Meter.CreateHistogram<long>("simulations.duration", unit: "ms", description: "Duration of simulations in milliseconds");

        private readonly List<SimulationInterval> intervals = [];

        /// <summary>
        /// Gets the aggregated results of the simulation runs.
        /// </summary>
        public SimulationResults Results { get; } = new(name);

        /// <summary>
        /// Executes the simulation copies in parallel, collecting metrics and results.
        /// </summary>
        private async Task Simulate(double copies)
        {
            var tags = KeyValuePair.Create<string, object?>("scenario", name);
            var simulations = new List<Task<Result>>(capacity: (int)copies);

            for (long i = 0; i < copies; i++)
            {
                var simulation = Task.Run(async () =>
                {
                    using var activity = ActivitySource.StartActivity(name);

                    var sw = Stopwatch.StartNew();

                    Result result;

                    try
                    {
                        result = await scenario.Invoke();
                    }
                    catch
                    {
                        result = new Result(false);
                    }


                    sw.Stop();

                    DurationHistogram.Record(sw.ElapsedMilliseconds, tags);
                    Results.Duration += sw.ElapsedMilliseconds;

                    if (result.Success)
                    {
                        Results.Success();
                        SuccessCounter.Add(1, tags);
                    }
                    else
                    {
                        Results.Failure();
                        FailureCounter.Add(1, tags);
                    }

                    Results.Count();

                    return result;
                });

                simulations.Add(simulation);
            }

            await Task.WhenAll(simulations);
        }

        /// <summary>
        /// Represents a configuration interval for running simulations,
        /// specifying the duration, initial number of copies, and the rate of increase.
        /// </summary>
        /// <param name="Duration">The length of time to run the simulation interval.</param>
        /// <param name="Copies">The initial number of simulation copies to run concurrently. Must be at least 1.</param>
        /// <param name="Rate">The rate at which the number of copies increases per second during the interval.</param>
        public class SimulationInterval(TimeSpan duration, double copies, double rate)
        {
            public TimeSpan Duration { get; set; } = duration;
            public double Copies { get; set; } = copies;
            public double Rate { get; set; } = rate;
        }

        /// <summary>
        /// Holds aggregated results of multiple simulation runs.
        /// Thread-safe incrementing for success and failure counts.
        /// </summary>
        /// <remarks>
        /// Initializes a new instance of the <see cref="Results"/> class.
        /// </remarks>
        /// <param name="name">Name of the simulation scenario.</param>
        public class SimulationResults(string name)
        {

            /// <summary>
            /// Gets the name of the simulation scenario.
            /// </summary>
            public string Name { get; set; } = name;

            private long _successes;
            private long _failures;
            private long _total;

            /// <summary>
            /// Gets the total number of successful simulation runs.
            /// </summary>
            public long Successes => _successes;

            /// <summary>
            /// Gets the total number of failed simulation runs.
            /// </summary>
            public long Failures => _failures;

            /// <summary>
            /// Gets the total number of simulation runs.
            /// </summary>
            public long Total => _total;

            /// <summary>
            /// Total duration in milliseconds
            /// </summary>
            internal long Duration;

            /// <summary>
            /// Gets the mean duration in milliseconds
            /// </summary>
            public long MeanDuration => meanDuration;

            /// <summary>
            /// Mean duration in milliseconds
            /// </summary>
            internal long meanDuration;

            /// <summary>
            /// Atomically increments the success count.
            /// </summary>
            internal void Success() => Interlocked.Increment(ref _successes);

            /// <summary>
            /// Atomically increments the failure count.
            /// </summary>
            internal void Failure() => Interlocked.Increment(ref _failures);

            /// <summary>
            /// Atomically increments the total count.
            /// </summary>
            internal void Count() => Interlocked.Increment(ref _total);
        }


        /// <summary>
        /// Runs the simulation according to configured copies, duration, and rate.
        /// If a duration is specified, increases the number of copies over time by the rate.
        /// </summary>
        /// <returns>The aggregated simulation results.</returns>
        public async Task<SimulationResults> Run()
        {
            if (intervals.Count == 0)
                intervals.Add(new SimulationInterval(TimeSpan.Zero, 1, 0));

            foreach (var interval in intervals)
            {
                if (interval.Duration == TimeSpan.Zero)
                {
                    await Simulate(interval.Copies);
                    continue;
                }


                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < interval.Duration)
                {
                    var copies = Math.Floor(stopwatch.Elapsed.TotalSeconds * interval.Rate);
                    await Simulate(interval.Copies + copies);
                }
            }

            Results.meanDuration = Results.Total > 0 ? Results.Duration / Results.Total : 0;

            return Results;
        }

        /// <summary>
        /// Configures the simulation to run for a given duration, initial number of copies, and rate of increase.
        /// </summary>
        /// <param name="duration">Duration to run the simulation for.</param>
        /// <param name="copies">Initial number of simulation copies per iteration (minimum 0).</param>
        /// <param name="rate">Rate to increase the number of copies per second.</param>
        /// <returns>The current simulation instance for fluent chaining.</returns>
        public Simulation RunFor(TimeSpan duration, long copies = 1, double rate = 0)
        {
            if (copies < 0)
                throw new ArgumentOutOfRangeException("A negative number of simulation copies is not supported.");

            intervals.Add(new SimulationInterval(duration, copies, rate));

            return this;
        }
    }
}
