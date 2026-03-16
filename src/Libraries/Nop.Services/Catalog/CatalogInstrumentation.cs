using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nop.Services.Catalog;

/// <summary>
/// Defines shared OpenTelemetry instrumentation primitives for the Catalog domain.
/// ActivitySource is used for distributed tracing; Meter is used for custom metrics.
/// </summary>
public static class CatalogInstrumentation
{
    public const string ActivitySourceName = "NopCommerce.Catalog";
    public const string MeterName = "NopCommerce.Catalog";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // --- Custom Metrics ---

    /// <summary>
    /// Histogram: number of products returned per search query.
    /// Justification: if the median drops to zero, the catalogue or search index is broken.
    /// An operator can correlate this with a deployment or data migration.
    /// </summary>
    public static readonly Histogram<int> SearchResultsCount =
        Meter.CreateHistogram<int>(
            "nopcommerce.search.results_count",
            unit: "{products}",
            description: "Number of products returned by a search query");

    /// <summary>
    /// Histogram: duration (ms) of building a product detail model.
    /// Justification: if p95 spikes, it indicates a regression in pricing, picture loading or DB —
    /// before users start seeing timeouts.
    /// </summary>
    public static readonly Histogram<double> ProductViewDuration =
        Meter.CreateHistogram<double>(
            "nopcommerce.catalog.product_view_duration_ms",
            unit: "ms",
            description: "Time to retrieve and build the product detail model");

    /// <summary>
    /// Counter: cache hits for the static cache manager.
    /// Justification: a sudden drop in hit rate means cache is being invalidated too aggressively
    /// or a deployment cleared it — an operator should check recent changes.
    /// </summary>
    public static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>(
            "nopcommerce.cache.hits",
            unit: "{hits}",
            description: "Number of cache hits in the static cache manager");

    /// <summary>
    /// Counter: cache misses for the static cache manager.
    /// </summary>
    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>(
            "nopcommerce.cache.misses",
            unit: "{misses}",
            description: "Number of cache misses in the static cache manager");

    /// <summary>
    /// Counter: track search and view errors.
    /// Justification: allows creating critical alerts. If error rate rises above 1%, pager alerts support.
    /// </summary>
    public static readonly Counter<long> ErrorCount =
        Meter.CreateCounter<long>(
            "nopcommerce.catalog.errors",
            unit: "{errors}",
            description: "Number of errors encountered in catalog operations");
}
