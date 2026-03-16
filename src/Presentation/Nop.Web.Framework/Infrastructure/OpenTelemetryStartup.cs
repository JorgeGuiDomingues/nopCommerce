using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nop.Core.Caching;
using Nop.Core.Infrastructure;
using Nop.Services.Catalog;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// INopStartup implementation that wires the OpenTelemetry SDK (tracing + metrics),
/// registers instrumented service overrides, and maps the Prometheus scraping endpoint.
/// 
/// Order = 2001 ensures this runs AFTER NopStartup (Order 2000) so we can replace
/// the IProductService and IStaticCacheManager DI registrations.
/// </summary>
public class OpenTelemetryStartup : INopStartup
{
    private const string ServiceName = "nopcommerce-web";
    private const string ServiceVersion = "5.0.0";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // --- 1. Configure OpenTelemetry SDK ---
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName, serviceVersion: ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Exclude health-check and static files from traces
                        options.Filter = httpContext =>
                        {
                            var path = httpContext.Request.Path.Value;
                            return path != null
                                   && !path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
                                   && !path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                                   && !path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                                   && !path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
                                   && !path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                                   && !path.Equals("/health", StringComparison.OrdinalIgnoreCase);
                        };

                        // Enrich HTTP spans with sanitised route info
                        options.EnrichWithHttpRequest = (activity, request) =>
                        {
                            activity.SetTag("http.request.method", request.Method);
                        };
                    })
                    // Register our custom ActivitySources
                    .AddSource(CatalogInstrumentation.ActivitySourceName)
                    .AddSource("NopCommerce.Data")
                    // Add the PII sanitisation processor
                    .AddProcessor<PiiSanitizingProcessor>()
                    // Export to Jaeger via OTLP
                    .AddOtlpExporter(options =>
                    {
                        // Default: http://localhost:4317 (gRPC)
                        // Can be overridden via OTEL_EXPORTER_OTLP_ENDPOINT env var
                        options.Endpoint = new Uri(
                            configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317");
                    });
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(CatalogInstrumentation.MeterName)
                    .AddMeter(Nop.Data.DataInstrumentation.MeterName)
                    // Prometheus exporter for scraping
                    .AddPrometheusExporter();
            });

        // --- 2. Replace IProductService with instrumented subclass ---
        // Remove the existing registration (from NopStartup, Order 2000)
        services.RemoveAll<IProductService>();
        services.AddScoped<IProductService, InstrumentedProductService>();

        // --- 3. Wrap IStaticCacheManager with instrumented decorator ---
        // We decorate the existing IStaticCacheManager by resolving the originally
        // registered implementation and wrapping it with our decorator.
        // First, find the existing registration to capture the implementation type.
        var existingDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IStaticCacheManager));
        if (existingDescriptor != null)
        {
            services.RemoveAll<IStaticCacheManager>();

            if (existingDescriptor.ImplementationType != null)
            {
                // Re-register the concrete type so it can be resolved
                var lifetime = existingDescriptor.Lifetime;
                services.Add(new ServiceDescriptor(
                    existingDescriptor.ImplementationType,
                    existingDescriptor.ImplementationType,
                    lifetime));

                // Register the decorator
                services.Add(new ServiceDescriptor(
                    typeof(IStaticCacheManager),
                    sp => new InstrumentedStaticCacheManager(
                        (IStaticCacheManager)sp.GetRequiredService(existingDescriptor.ImplementationType)),
                    lifetime));
            }
            else if (existingDescriptor.ImplementationFactory != null)
            {
                var lifetime = existingDescriptor.Lifetime;
                services.Add(new ServiceDescriptor(
                    typeof(IStaticCacheManager),
                    sp => new InstrumentedStaticCacheManager(
                        (IStaticCacheManager)existingDescriptor.ImplementationFactory(sp)),
                    lifetime));
            }
        }
    }

    public void Configure(IApplicationBuilder application)
    {
    }

    /// <summary>
    /// Must run AFTER NopStartup (Order 2000) so we can override its DI registrations.
    /// </summary>
    public int Order => 2001;
}
