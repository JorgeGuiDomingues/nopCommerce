# nopCommerce — Catalog Flow Observability

**Assignment 01 — Observability in the Wild**

Instrumented flow: **Customer searches and views a product** (Catalogue, Search, Pricing)

---

## Architecture Diagram

<!-- TODO: Replace with actual architecture diagram image -->
*[Insert architecture diagram here]*

```
┌─────────────────────────────────────────────────────────────────┐
│  Browser / k6 Load Test                                         │
│  GET /search?q=laptop    GET /product-slug                      │
└──────────────┬──────────────────────┬───────────────────────────┘
               │                      │
┌──────────────▼──────────────────────▼───────────────────────────┐
│  Nop.Web  (ASP.NET Core — auto-instrumented HTTP spans)         │
│  CatalogController.Search()    ProductController.ProductDetails()│
└──────────────┬──────────────────────┬───────────────────────────┘
               │                      │
┌──────────────▼──────────────────────▼───────────────────────────┐
│  Nop.Web.Framework                                              │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ OpenTelemetryStartup (INopStartup, Order=2001)             │ │
│  │ InstrumentedProductService         → spans: Search,        │ │
│  │                                      Catalogue             │ │
│  │ InstrumentedPriceCalculationService → span: Pricing        │ │
│  │ InstrumentedStaticCacheManager      → metrics: cache.hits, │ │
│  │                                       cache.misses         │ │
│  │ PiiSanitizingProcessor              → redacts PII          │ │
│  └────────────────────────────────────────────────────────────┘ │
└──────────────┬──────────────────────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────────────────────┐
│  Nop.Services                                                   │
│  CatalogInstrumentation (shared ActivitySource + Meter)          │
│  ProductService (base class — virtual methods)                  │
│  PriceCalculationService (base class — virtual methods)         │
└──────────────┬──────────────────────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────────────────────┐
│  Nop.Data                                                       │
│  InstrumentedRepository<T> (decorator over EntityRepository<T>) │
│  → spans: Repository.GetById, Repository.GetAllPaged, etc.     │
│  → metric: database.query_duration_ms                           │
│  EntityRepository<T> → Linq2DB → SQL Server 2019 Express        │
└─────────────────────────────────────────────────────────────────┘

         Telemetry Export
         ┌──────┴──────┐
    OTLP gRPC     Prometheus
    (traces)      /metrics
         │             │
    ┌────▼───┐   ┌─────▼──────┐
    │ Jaeger │   │ Prometheus │
    │ :16686 │   │   :9090    │
    └────┬───┘   └─────┬──────┘
         └──────┬──────┘
           ┌────▼────┐
           │ Grafana │
           │  :3000  │
           └─────────┘
```

---

## Prerequisites

- **Docker** and **Docker Compose** (for SQL Server, Jaeger, Prometheus, Grafana)
- **.NET 9 SDK** (to build and run nopCommerce)
- **k6** (for load testing) — install with `brew install k6` on macOS

---

## Quick Start

### 1. Start SQL Server (Database)

```bash
docker compose up -d
```

This starts **SQL Server 2019 Express** on port `1433` (container: `nopcommerce_mssql_server`).

### 2. Start the Observability Stack

```bash
docker compose -f docker-compose.observability.yml up -d
```

This starts:
| Service      | URL                        | Purpose                |
|--------------|----------------------------|------------------------|
| **Jaeger**   | http://localhost:16686      | Distributed traces UI  |
| **Prometheus** | http://localhost:9090     | Metrics storage        |
| **Grafana**  | http://localhost:3000       | Dashboards (admin/admin) |

### 3. Build and Run nopCommerce

```bash
cd src
dotnet restore Nop.sln
dotnet run --project Presentation/Nop.Web/Nop.Web.csproj
```

The application starts at **http://localhost:5050**.

> **Note:** On macOS, port 5000 is used by AirPlay. The app is configured to use port 5050 in `src/Presentation/Nop.Web/App_Data/appsettings.json`.

### 4. View the Grafana Dashboard

1. Open http://localhost:3000 (login: `admin` / `admin`)
2. Go to **Dashboards** → **nopCommerce** → **NopCommerce - Catalog Flow Observability**

The dashboard is auto-provisioned and contains 4 sections:
- **Overview Stats** — P95 latency, error rate, cache hit ratio
- **Application Health & Activity** — error rate timeseries, cache hit/miss rate, search results distribution
- **Performance & Latency** — P95 latency line chart, product view duration heatmap, DB latency heatmap
- **Distributed Traces (Jaeger)** — trace tables for Search, Catalogue, Pricing, and Database layers

### 5. Run the Load Test

```bash
k6 run observability/loadtest/search-flow.js
```

The load test simulates the full user journey:
1. Homepage visit
2. Product search (`GET /search?q={term}`)
3. Autocomplete (`GET /catalog/searchtermautocomplete`)
4. Product detail page (triggers Catalogue + Pricing spans)
5. Category and manufacturer browsing
6. Invalid requests (to stress error handling and cache)

**Load profile:** ramps up to 250 concurrent virtual users over ~4 minutes with spike patterns.

> Run the load test while watching the Grafana dashboard to see metrics and traces populate in real time.

### 6. Verify Traces in Jaeger

1. Open http://localhost:16686
2. Select service: `nopcommerce-web`
3. Click **Find Traces**
4. You should see hierarchical spans:
   - `GET /search` (HTTP) → `Search` (service) → `Repository.GetAllPaged` (DB)
   - `GET /product-slug` (HTTP) → `Catalogue` (service) → `Pricing` (service) → `Repository.GetById` (DB)

### 7. Verify Metrics in Prometheus

Open http://localhost:9090 and query:
- `nopcommerce_search_results_count_bucket` — search results histogram
- `nopcommerce_catalog_product_view_duration_ms_milliseconds_bucket` — product view latency
- `nopcommerce_cache_hits_total` / `nopcommerce_cache_misses_total` — cache counters
- `nopcommerce_catalog_errors_total` — error counter
- `nopcommerce_database_query_duration_ms_milliseconds_bucket` — DB query latency

---

## What Was Changed

Only **2 original nopCommerce files** were modified. Everything else is new (additive).

| File | Change |
|------|--------|
| `Nop.Web.Framework.csproj` | Added 4 OpenTelemetry NuGet packages |
| `Nop.Data/NopDbStartup.cs` | Changed `IRepository<>` registration to use `InstrumentedRepository<>` decorator (2 lines) |

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `CatalogInstrumentation.cs` | Services | Shared `ActivitySource` and `Meter` + 5 metric definitions |
| `DataInstrumentation.cs` | Data | `Meter` for DB query duration metric |
| `InstrumentedRepository.cs` | Data | Decorator wrapping `EntityRepository<T>` with tracing spans |
| `InstrumentedProductService.cs` | Web.Framework | Overrides `SearchProductsAsync` and `GetProductByIdAsync` with spans and metrics |
| `InstrumentedPriceCalculationService.cs` | Web.Framework | Overrides `GetFinalPriceAsync` with `Pricing` span |
| `InstrumentedStaticCacheManager.cs` | Web.Framework | Decorator tracking cache hit/miss metrics |
| `PiiSanitizingProcessor.cs` | Web.Framework | Redacts PII (emails, tokens, etc.) from traces before export |
| `OpenTelemetryStartup.cs` | Web.Framework | `INopStartup` (Order=2001) configuring OpenTelemetry SDK and DI replacements |
| `docker-compose.observability.yml` | Root | Jaeger + Prometheus + Grafana stack |
| `observability/prometheus.yml` | Root | Prometheus scrape config for `/metrics` endpoint |
| `observability/grafana/provisioning/` | Root | Auto-provisioning for Grafana datasources and dashboards |
| `observability/loadtest/search-flow.js` | Root | k6 load test script |

---

## Custom Metrics

| Metric | Type | Justification |
|--------|------|---------------|
| `search.results_count` | Histogram | Detects empty search results from corrupted indexes or failed migrations |
| `product_view_duration_ms` | Histogram | Signals performance regressions in pricing/DB before users notice |
| `cache.hits` / `cache.misses` | Counters | Detects cache storms after deployments or TTL misconfigurations |
| `catalog.errors` | Counter | Enables alerting when the catalog flow degrades (timeouts, SQL errors) |
| `database.query_duration_ms` | Histogram | Isolates DB vs application-layer latency for faster root cause analysis |

---

## Stopping Everything

```bash
# Stop SQL Server
docker compose down

# Stop observability stack
docker compose -f docker-compose.observability.yml down

# Stop nopCommerce (Ctrl+C in the terminal running dotnet)
```
