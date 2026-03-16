using System.Diagnostics.Metrics;

namespace Nop.Data;

public static class DataInstrumentation
{
    public const string MeterName = "NopCommerce.Data";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Histogram: exact duration of database queries.
    /// Justification: pinpoint whether slow performance is due to DB latency or application code.
    /// </summary>
    public static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>(
            "nopcommerce.database.query_duration_ms",
            unit: "ms",
            description: "Duration of database queries in milliseconds");
}
