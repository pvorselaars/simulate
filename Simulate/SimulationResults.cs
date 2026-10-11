namespace Simulate
{
    /// <summary>
    /// Represents the result of a single iteration, including whether it succeeded and how long it took.
    /// </summary>
    public class SimulationResult
    {
        public uint Iteration { get; set; }
        public bool Success { get; set; }
        public long Duration { get; set; }

        /// <summary>Zero-based index of the virtual user that ran this iteration.</summary>
        public int User { get; set; }
    }

    /// <summary>
    /// Represents the aggregated results of a simulation run, including total iterations, successes, failures, and duration statistics.
    /// </summary>
    public class SimulationResults
    {
        /// <summary>The name of the simulation these results belong to.</summary>
        public string Name { get; set; } = "";

        /// <summary>The number of virtual users.</summary>
        public int Users { get; set; }

        /// <summary>How long the timed part took, from the first user starting to the last user finishing.</summary>
        public TimeSpan Elapsed { get; set; }

        public uint TotalIterations { get; set; } = 0;
        public long Successes { get; set; } = 0;
        public long Failures { get; set; } = 0;
        public double MeanDuration { get; set; } = 0;
        public double StandardDeviation { get; set; } = 0;
        public long MedianDuration { get; set; } = 0;
        public long MaxDuration { get; set; } = 0;
        public long MinDuration { get; set; } = 0;

        public Dictionary<string, long> Quantiles = new Dictionary<string, long>();
        public long IQR => Quantiles.TryGetValue("p75", out long p75) && Quantiles.TryGetValue("p25", out long p25) ? p75 - p25 : 0;
        public double MedianSkewness => StandardDeviation > 0 ? 3 * (MeanDuration - MedianDuration) / StandardDeviation : 0;
        public double ModeSkewness => StandardDeviation > 0 ? (MeanDuration - MinDuration) / StandardDeviation : 0;
        public double StandardError => StandardDeviation / Math.Sqrt(TotalIterations);

        public IEnumerable<SimulationResult> Results { get; set; } = [];

        internal void Summarize(List<SimulationResult> results)
        {
            Results = results;
            TotalIterations = (uint)results.Count;
            Successes = results.Count(r => r.Success);
            Failures = results.Count(r => !r.Success);
            MeanDuration = results.Average(r => r.Duration);
            StandardDeviation = Math.Sqrt(results.Average(r => Math.Pow(r.Duration - MeanDuration, 2)));
            MaxDuration = results.Max(r => r.Duration);
            MinDuration = results.Min(r => r.Duration);

            var sortedResults = results.OrderBy(r => r.Duration).ToList();
            MedianDuration = sortedResults[results.Count / 2].Duration;
            Quantiles["p25"] = sortedResults[(int)(results.Count * 0.25)].Duration;
            Quantiles["p50"] = MedianDuration;
            Quantiles["p75"] = sortedResults[(int)(results.Count * 0.75)].Duration;
            Quantiles["p80"] = sortedResults[(int)(results.Count * 0.8)].Duration;
            Quantiles["p90"] = sortedResults[(int)(results.Count * 0.9)].Duration;
            Quantiles["p95"] = sortedResults[(int)(results.Count * 0.95)].Duration;
            Quantiles["p99"] = sortedResults[(int)(results.Count * 0.99)].Duration;
            Quantiles["p99.9"] = sortedResults[(int)(results.Count * 0.999)].Duration;
        }

        internal void Print() => Console.WriteLine(ToReport());

        /// <summary>
        /// Builds a readable report: totals, throughput, duration statistics and a breakdown per virtual user.
        /// Print it to the console, or hand it to a test framework's output.
        /// </summary>
        public string ToReport()
        {
            var report = new System.Text.StringBuilder();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var perSecond = Elapsed > TimeSpan.Zero ? TotalIterations / Elapsed.TotalSeconds : 0;

            report.AppendLine(culture, $"Simulation '{Name}': {TotalIterations} iterations by {Users} user(s) in {Elapsed.ToString(@"hh\:mm\:ss", culture)} ({perSecond:0.00} iterations/s)");
            report.AppendLine(culture, $"  Successes: {Successes}, failures: {Failures}");
            report.AppendLine(culture, $"  Duration (ms): mean {MeanDuration:0}, standard deviation {StandardDeviation:0}, median {MedianDuration}, min {MinDuration}, max {MaxDuration}");
            report.AppendLine(culture, $"  Spread: IQR {IQR} ms, standard error {StandardError:0.0} ms, median skewness {MedianSkewness:0.00}, mode skewness {ModeSkewness:0.00}");

            if (Quantiles.Count > 0)
                report.AppendLine("  Quantiles (ms): " + string.Join(", ", Quantiles.Select(q => $"{q.Key} {q.Value}")));

            var perUser = Results.GroupBy(r => r.User).OrderBy(g => g.Key).ToList();
            if (perUser.Count > 1)
            {
                report.AppendLine("  Per user:");
                report.AppendLine("    user  iterations  failures  mean (ms)  p95 (ms)  max (ms)");

                foreach (var user in perUser)
                {
                    var durations = user.Select(r => r.Duration).OrderBy(d => d).ToList();
                    var p95 = durations[Math.Min((int)(durations.Count * 0.95), durations.Count - 1)];

                    report.AppendLine(culture, $"    {user.Key,4}  {durations.Count,10}  {user.Count(r => !r.Success),8}  {durations.Average(),9:0}  {p95,8}  {durations[^1],8}");
                }
            }

            return report.ToString().TrimEnd();
        }

        public void ToCSV(string filePath)
        {
            using var writer = new StreamWriter(filePath);
            writer.WriteLine("Iteration,Success,Duration,User");
            foreach (var result in Results)
            {
                writer.WriteLine($"{result.Iteration},{result.Success},{result.Duration},{result.User}");
            }
        }
    }
}
