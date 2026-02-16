using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Simulate
{
    /// <summary>
    /// Runs a simulation scenario multiple times, tracking metrics and results.
    /// Supports parallel execution, metrics collection, and rate-controlled runs.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Simulation"/> class.
    /// </remarks>
    /// <param name="name">The name of the simulation scenario.</param>
    /// <param name="scenario">The asynchronous simulation function to execute.</param>
    public class Simulation
    {
        private const string ServiceName = "Simulate";
        private const string ServiceVersion = "0.0.6";

        private readonly Func<ILogger, ValueTask<bool>> scenario;
        private readonly string name;

        private static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
        private static readonly Meter Meter = new(ServiceName, ServiceVersion);
        private readonly ILogger Logger;
        private static readonly Counter<long> SuccessCounter = Meter.CreateCounter<long>("simulations.ok", description: "Total number of successful simulations");
        private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("simulations.fail", description: "Total number of failed simulations");
        private static readonly Histogram<long> DurationHistogram = Meter.CreateHistogram<long>("simulations.duration", unit: "ms", description: "Duration of simulations in milliseconds");
        private readonly List<SimulationInterval> intervals = [];

        public Simulation(string name,
                          Func<ILogger, ValueTask<bool>> scenario,
                          ILoggerFactory? loggerFactory = null)
        {
            this.name = name;
            this.scenario = scenario;

            // Use provided factory or default to console
            loggerFactory ??= LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddConsole();
            });

            Logger = loggerFactory.CreateLogger<Simulation>();
        }

        /// <summary>
        /// Gets the aggregated results of the simulation after execution.
        /// </summary>
        public SimulationResults Results { get; set; } = new();

        /// <summary>
        /// Executes the simulation copies in parallel, collecting metrics and results.
        /// </summary>
        private async Task<IEnumerable<SimulationResult>> Simulate(double copies, uint iteration = 1)
        {
            var tags = KeyValuePair.Create<string, object?>("scenario", name);
            var simulations = new List<Task<SimulationResult>>(capacity: (int)copies);

            for (long i = 0; i < copies; i++)
            {
                var simulation = Task.Run(async () =>
                {
                    using var activity = ActivitySource.StartActivity(name);

                    var sw = Stopwatch.StartNew();

                    bool result;

                    try
                    {
                        result = await scenario.Invoke(Logger);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Scenario '{Scenario}' failed:", name);
                        result = false;
                    }

                    sw.Stop();

                    DurationHistogram.Record(sw.ElapsedMilliseconds, tags);

                    if (result)
                    {
                        SuccessCounter.Add(1, tags);
                    }
                    else
                    {
                        FailureCounter.Add(1, tags);
                    }

                    return new SimulationResult
                    {
                        Iteration = iteration,
                        Success = result,
                        Duration = sw.ElapsedMilliseconds
                    };
                });

                simulations.Add(simulation);
            }

            return await Task.WhenAll(simulations);
        }

        /// <summary>
        /// Represents a configuration interval for running simulations,
        /// specifying the duration, initial number of copies, and the rate of increase.
        /// </summary>
        /// <param name="Duration">The length of time to run the simulation interval.</param>
        /// <param name="Copies">The initial number of simulation copies to run concurrently. Must be at least 1.</param>
        /// <param name="Rate">The rate at which the number of copies increases per second during the interval.</param>
        public class SimulationInterval(TimeSpan duration, double copies, double rate, uint iterations = uint.MaxValue)
        {
            public TimeSpan Duration { get; set; } = duration;
            public double Copies { get; set; } = copies;
            public double Rate { get; set; } = rate;
            public uint Iterations { get; set; } = iterations;
        }

        /// <summary>
        /// Represents the result of a single simulation execution, including whether it succeeded and how long it took.
        /// </summary>
        public class SimulationResult
        {
            public uint Iteration { get; set; }
            public bool Success { get; set; }
            public long Duration { get; set; }
        }

        /// <summary>
        /// Represents the aggregated results of a simulation run, including total iterations, successes, failures, and duration statistics.
        /// </summary>
        public class SimulationResults
        {
            public uint TotalIterations { get; set; } = 0;
            public long Successes { get; set; } = 0;
            public long Failures { get; set; } = 0;
            public double MeanDuration { get; set; } = 0;
            public double StandardDeviation { get; set; } = 0;
            public long MedianDuration { get; set; } = 0;
            public long MaxDuration { get; set; } = 0;
            public long MinDuration { get; set; } = 0;
        }

        /// <summary>
        /// Runs the simulation according to configured copies, duration, and rate.
        /// If a duration is specified, increases the number of copies over time by the rate.
        /// </summary>
        /// <returns>The aggregated simulation results.</returns>
        public async Task<SimulationResults> Run()
        {
            var results = new List<SimulationResult>();
            if (intervals.Count == 0)
                intervals.Add(new SimulationInterval(TimeSpan.Zero, 1, 0));

            foreach (var interval in intervals)
            {
                if (interval.Duration == TimeSpan.Zero)
                {
                    var run = await Simulate(interval.Copies);
                    results.AddRange(run);
                    continue;
                }

                var stopwatch = Stopwatch.StartNew();
                uint iteration = 1;
                while (stopwatch.Elapsed < interval.Duration && iteration++ <= interval.Iterations)
                {
                    var copies = Math.Floor(stopwatch.Elapsed.TotalSeconds * interval.Rate);
                    var run = await Simulate(interval.Copies + copies, iteration);
                    results.AddRange(run);
                }
            }

            Results.TotalIterations = (uint)results.Count;
            Results.Successes = results.Count(r => r.Success);
            Results.Failures = results.Count(r => !r.Success);
            Results.MeanDuration = results.Count > 0 ? results.Average(r => r.Duration) : 0;
            Results.StandardDeviation = results.Count > 0 ? Math.Sqrt(results.Average(r => Math.Pow(r.Duration - Results.MeanDuration, 2))) : 0;
            Results.MedianDuration = results.Count > 0 ? results.OrderBy(r => r.Duration).ElementAt(results.Count / 2).Duration : 0;
            Results.MaxDuration = results.Count > 0 ? results.Max(r => r.Duration) : 0;
            Results.MinDuration = results.Count > 0 ? results.Min(r => r.Duration) : 0;

            Console.WriteLine("Total Iterations: {0}", Results.TotalIterations);
            Console.WriteLine("Successes: {0}", Results.Successes);
            Console.WriteLine("Failures: {0}", Results.Failures);
            Console.WriteLine("Mean Duration (ms): {0}", Results.MeanDuration);
            Console.WriteLine("Standard Deviation (ms): {0}", Results.StandardDeviation);
            Console.WriteLine("Median Duration (ms): {0}", Results.MedianDuration);
            Console.WriteLine("Max Duration (ms): {0}", Results.MaxDuration);
            Console.WriteLine("Min Duration (ms): {0}", Results.MinDuration);

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
                throw new ArgumentOutOfRangeException(nameof(copies), "A negative number of simulation copies is not supported.");

            intervals.Add(new SimulationInterval(duration, copies, rate));

            return this;
        }

        /// <summary> 
        /// Configures the simulation to run for a given number of iterations, with an optional number of copies and rate of increase.
        /// Each iteration runs the specified number of copies concurrently.
        /// </summary>
        /// <param name="iterations">Number of iterations to run the simulation for (minimum 1).</param>
        /// <param name="copies">Number of simulation copies to run concurrently for each iteration (minimum 0).</param>
        /// <param name="rate">Rate to increase the number of copies per second during the iterations.</param>
        public Simulation RunFor(uint iterations, long copies = 1, double rate = 0)
        {
            if (copies < 0)
                throw new ArgumentOutOfRangeException(nameof(copies), "A negative number of simulation copies is not supported.");

            if (iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(iterations), "A negative or zero number of simulation iterations is not supported.");

            intervals.Add(new SimulationInterval(TimeSpan.MaxValue, copies, rate, iterations));

            return this;
        }
    }
}
