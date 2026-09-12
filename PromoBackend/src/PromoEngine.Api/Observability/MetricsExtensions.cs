using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace PromoEngine.Api.Observability;

/// <summary>
/// Prometheus metrics for the API.
///
/// Nothing here touches business logic. The numbers come from the meters that
/// ASP.NET Core and the .NET runtime already publish on their own - request
/// duration, active requests, status codes, GC, thread pool. All this code does
/// is collect them and expose them in Prometheus text format on /metrics.
///
/// That endpoint is deliberately NOT proxied by the frontend Nginx server, so it
/// is reachable only from inside the Docker network - which is exactly where
/// Prometheus runs. It is anonymous on purpose: a scraper cannot hold a tenant
/// JWT, and adding auth would mean putting a credential in the Prometheus
/// configuration file for no security gain over network isolation.
/// </summary>
public static class MetricsExtensions
{
    /// <summary>Path Prometheus scrapes. Must match monitoring/prometheus/prometheus.yml.</summary>
    public const string ScrapeEndpointPath = "/metrics";

    private const string ServiceName = "promoengine-backend";

    /// <summary>
    /// Set Metrics__Enabled=false to turn the whole thing off without rebuilding
    /// the image. Both halves read the same key, so the exporter and the endpoint
    /// can never disagree about whether metrics exist.
    /// </summary>
    private static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue("Metrics:Enabled", defaultValue: true);

    public static IServiceCollection AddPromoEngineMetrics(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        // The deployed image tag, so a dashboard can tell which release produced
        // a spike. The pipeline sets it; a hand-run container simply has none.
        var version = configuration.GetValue<string>("Metrics:ServiceVersion")
                      ?? typeof(MetricsExtensions).Assembly.GetName().Version?.ToString()
                      ?? "unknown";

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: version,
                // Stable across restarts. The default is a fresh GUID per process,
                // which would make every restart look like a brand-new target.
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics => metrics
                // Gives http_server_request_duration_seconds (a histogram, so
                // P50/P95/P99 are derivable) and http_server_active_requests.
                // The route label is the ROUTE TEMPLATE - /api/promotions/{id},
                // never /api/promotions/42 - so tenant ids and record ids cannot
                // blow up cardinality.
                .AddAspNetCoreInstrumentation()
                // dotnet_gc_*, dotnet_thread_pool_*, dotnet_monitor_lock_contentions_total.
                // This is what distinguishes "the app is slow" from "the app is
                // starved of threads" during an incident.
                .AddRuntimeInstrumentation()
                // kestrel_active_connections / kestrel_queued_connections: queueing
                // here means the server ran out of capacity before any handler ran.
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddPrometheusExporter());

        return services;
    }

    public static IEndpointRouteBuilder MapPromoEngineMetrics(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            return app;
        }

        app.MapPrometheusScrapingEndpoint(ScrapeEndpointPath)
           // Explicit rather than implied. If a future change adds a global
           // authorization requirement, scraping keeps working instead of
           // silently returning 401 and emptying every dashboard.
           .AllowAnonymous()
           .ExcludeFromDescription();

        return app;
    }
}
