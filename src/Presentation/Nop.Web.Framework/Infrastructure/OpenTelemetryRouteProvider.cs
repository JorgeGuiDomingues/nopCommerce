using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Mvc.Routing;
using OpenTelemetry.Metrics;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// Registers the Prometheus metrics scraping endpoint in the NopCommerce routing pipeline.
/// </summary>
public class OpenTelemetryRouteProvider : IRouteProvider
{
    public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // Map the Prometheus scraping endpoint at /metrics
        // This makes sure it's registered within the correct ASP.NET Core endpoint routing block
        endpointRouteBuilder.MapPrometheusScrapingEndpoint("/metrics");
    }

    // High priority so it gets mapped before the catch-all "page not found" route
    public int Priority => 1000;
}
