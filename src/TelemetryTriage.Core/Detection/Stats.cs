namespace TelemetryTriage.Core.Detection;

internal static class Stats
{
    /// <summary>Sample standard deviation (n - 1); 0 for fewer than two values.</summary>
    public static double StdDev(IReadOnlyCollection<double> values, double mean) =>
        values.Count < 2 ? 0 : Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
}
