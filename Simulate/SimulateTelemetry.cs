using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Simulate
{
    /// <summary>
    /// The activity source and metrics shared by all simulation types, so every type reports
    /// through the same instruments and exporters only have to subscribe to one source and one meter.
    /// </summary>
    internal static class SimulateTelemetry
    {
        internal const string ServiceName = "Simulate";
        internal const string ServiceVersion = "0.1.0";

        internal static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
        internal static readonly Meter Meter = new(ServiceName, ServiceVersion);
        internal static readonly Counter<long> SuccessCounter = Meter.CreateCounter<long>("simulations.ok", description: "Total number of successful simulations");
        internal static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("simulations.fail", description: "Total number of failed simulations");
        internal static readonly Histogram<long> DurationHistogram = Meter.CreateHistogram<long>("simulations.duration", unit: "ms", description: "Duration of simulations in milliseconds");
    }
}
