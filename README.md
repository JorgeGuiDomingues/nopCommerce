# nopCommerce — Catalog Flow Observability

**Assignment 01 — Observability in the Wild** | Software Architecture, 2024/2025

## About

[nopCommerce](https://github.com/nopSolutions/nopCommerce) is an open-source e-commerce platform built with ASP.NET Core, following a layered architecture: `Nop.Core` (domain), `Nop.Data` (persistence via Linq2DB), `Nop.Services` (business logic), and `Nop.Web` (presentation). It uses SQL Server as its database and a static in-memory cache for performance.

This project adds **end-to-end observability** to nopCommerce using OpenTelemetry, with distributed tracing (exported to Jaeger) and custom metrics (scraped by Prometheus), visualised in a Grafana dashboard.

### Chosen Flow: Customer searches and views a product

The instrumented flow covers three service layers:

- **Search** — the customer types a query in the search bar, which triggers `ProductService.SearchProductsAsync()` to query the database and return matching products
- **Catalogue** — the customer clicks on a product from the results, which triggers `ProductService.GetProductByIdAsync()` to load the full product details
- **Pricing** — while rendering the product page, `PriceCalculationService.GetFinalPriceAsync()` computes the displayed price (applying discounts, tier prices, etc.)

This flow was chosen because it exercises all architectural layers (HTTP → Services → Cache → Database) and is the most common user journey in any e-commerce platform.

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

- [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download)
- [Docker](https://docs.docker.com/engine/install/)
- [Docker Compose](https://docs.docker.com/compose/install/)
- [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/)

---

## Quick Start

### 0. Clone the Repository

```bash
git clone https://github.com/JorgeGuiDomingues/nopCommerce.git
cd nopCommerce
```

### 1. Start All Infrastructure (Database + Observability)

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
```

This starts everything in one command:
| Service          | URL / Port                 | Purpose                       |
|------------------|----------------------------|-------------------------------|
| **SQL Server**   | localhost:1433              | Database (SQL Server 2019 Express) |
| **Jaeger**       | http://localhost:16686      | Distributed traces UI         |
| **Prometheus**   | http://localhost:9090       | Metrics storage               |
| **Grafana**      | http://localhost:3000       | Dashboards (admin/admin)      |

### 2. Build and Run nopCommerce

The application is run **locally** (not inside Docker) to avoid networking issues between containers. Running locally, the app connects to SQL Server on `localhost:1433` and exports traces to Jaeger on `localhost:4317` without needing to reconfigure service hostnames. The existing `docker-compose.yml` includes a `nopcommerce_web` service, but it exposes port 80 (not 5050) and would require adjusting the database and OTLP connection strings for inter-container networking.

```bash
cd src
dotnet restore Nop.sln
dotnet run --project Presentation/Nop.Web/Nop.Web.csproj
```

The application starts at **http://localhost:5050**.

> **Note:** On macOS, port 5000 is used by AirPlay. The app is configured to use port 5050 in `src/Presentation/Nop.Web/App_Data/appsettings.json`.

![e-commerce_main_page](images/e-commerce_main_page.png)

### 3. View the Grafana Dashboard

1. Open http://localhost:3000 (login: `admin` / `admin`)
2. Go to **Dashboards** → **nopCommerce** → **NopCommerce - Catalog Flow Observability**

The dashboard is auto-provisioned via `observability/grafana/provisioning/` and contains 4 sections with 13 content panels (1 welcome text + 3 stat + 3 timeseries/heatmap + 3 heatmap + 4 Jaeger trace tables, plus 4 section rows). It tells the story of the catalog flow health — someone unfamiliar with the code can look at it and understand what is happening.

#### Section 1 — Overview Stats (3 stat panels)

| Panel | Metric | Query | What it shows |
|-------|--------|-------|---------------|
| P95 Product View Latency (ms) | `product_view_duration_ms` | `histogram_quantile(0.95, ...)` | How fast product pages load at the 95th percentile |
| Current Error Rate (Errors/min) | `catalog.errors` | `sum(rate(...[5m])) * 60` | Number of exceptions per minute in catalog operations |
| Static Cache Hit Ratio (Last 5m) | `cache.hits` / `cache.misses` | `hits / (hits + misses) * 100` | Percentage of cache hits — drops indicate cache storms |

#### Section 2 — Application Health & Activity (3 panels)

| Panel | Type | What it shows |
|-------|------|---------------|
| Search Operations Error Rate | Timeseries | Error rate over time, broken down by `error_type` and `operation_name` |
| Static Cache Hit/Miss Rate | Timeseries | Two lines: hits/sec vs misses/sec — shows cache effectiveness |
| Search Results Count Distribution | Heatmap | Distribution of how many products each search returns |

#### Section 3 — Performance & Latency (3 panels)

| Panel | Type | What it shows |
|-------|------|---------------|
| Product View P95 Latency | Timeseries (line) | P95 latency trend over time for product page loads |
| Product View Duration (Latency Distribution) | Heatmap | Full latency distribution for `product_view_duration_ms` |
| DB Latency Distribution for Catalog | Heatmap | Full latency distribution for `database.query_duration_ms` |

#### Section 4 — Distributed Traces / Jaeger (4 trace tables)

Each table queries Jaeger for a specific operation, covering all layers of the flow:

| Panel | Jaeger Operation | Layer | Purpose |
|-------|-----------------|-------|---------|
| Search Flow Traces | `GET /search/` | HTTP (auto-instrumented) | Entry point — full search request traces |
| Catalogue Core Spans | `Catalogue` | Service (`InstrumentedProductService`) | Product detail business logic |
| Pricing Calculation Traces | `Pricing` | Service (`InstrumentedPriceCalculationService`) | Price calculation spans |
| Database Layer Traces | `Repository.GetAllPaged` | Data (`InstrumentedRepository<T>`) | Paginated DB queries |

**Exported dashboard JSON:** [`observability/grafana/dashboards/nopcommerce.json`](observability/grafana/dashboards/nopcommerce.json)

![Grafana Dashboard — Metrics panels](images/dashboard_grafana_1.png)
![Grafana Dashboard — Jaeger trace tables](images/dashboard_grafana_2.png)
![Grafana Dashboard - Jaeger trace ID details](images/dashboard_grafana_3.png)

### 4. Run the Load Test

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

### 5. Verify Traces in Jaeger

1. Open http://localhost:16686
2. Select service: `nopcommerce-web`
3. Click **Find Traces**
4. You should see hierarchical spans:
   - `GET /search` (HTTP) → `Search` (service) → `Repository.GetAllPaged` (DB)
   - `GET /product-slug` (HTTP) → `Catalogue` (service) → `Pricing` (service) → `Repository.GetById` (DB)

### 6. Verify Metrics in Prometheus

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

## Sensitive Data Exclusion (PII)

The `PiiSanitizingProcessor` (`BaseProcessor<Activity>`) runs centrally in the OpenTelemetry export pipeline — before any span reaches Jaeger. This acts as a safety net regardless of what individual instrumentation points emit.

**What is filtered:**
- **Blocked attributes** (removed entirely): `customer.email`, `customer.name`, `payment.card_number`, `payment.cvv`, `http.request.header.cookie`, `http.request.header.authorization`, etc.
- **Pattern-based blocking**: any attribute whose name contains `password`, `token`, `secret`, `creditcard`, `ssn`
- **Email detection in values**: regex replaces email patterns (`user@domain.com`) with `[EMAIL_REDACTED]` in any string attribute value

**How to verify:** Search for `test@example.com` in nopCommerce, then find the trace in Jaeger — the search keywords attribute will show `[EMAIL_REDACTED]` instead of the email.

![PII_demonstration](images/PII_demonstration.png)

---

## Observability Stack

All observability infrastructure runs in Docker via `docker-compose.observability.yml`:

```
┌──────────────────────────────────────────────────────────────┐
│  nopCommerce (.NET 9, port 5050)                              │
│  ├── /metrics  (Prometheus scrape endpoint)                   │
│  └── OTLP gRPC (traces → localhost:4317)                      │
└──────────┬────────────────────┬───────────────────────────────┘
           │                    │
  ┌────────▼────────┐  ┌───────▼────────┐
  │   Prometheus     │  │    Jaeger       │
  │   :9090          │  │    :16686       │
  │   scrapes /metrics│  │    receives OTLP│
  │   every 15s      │  │    traces       │
  └────────┬────────┘  └───────┬────────┘
           └────────┬──────────┘
              ┌─────▼─────┐
              │  Grafana   │
              │  :3000     │
              │  admin/admin│
              └───────────┘
```

**Provisioning (auto-configured on startup):**
- `observability/grafana/provisioning/datasources/datasources.yml` — registers Prometheus (uid: `prometheus`) and Jaeger (uid: `jaeger`) datasources
- `observability/grafana/provisioning/dashboards/dashboards.yml` — loads dashboards from `/var/lib/grafana/dashboards`
- `observability/grafana/dashboards/nopcommerce.json` — the full dashboard definition (14 panels)
- `observability/prometheus.yml` — scrapes `host.docker.internal:5050/metrics` every 15s

---

## Critique

### What in nopCommerce's design helped or hindered instrumentation?

**What helped.** nopCommerce follows a strict layered architecture — `Nop.Core` (domain), `Nop.Data` (persistence), `Nop.Services` (business logic), and `Nop.Web.Framework`/`Nop.Web` (presentation). Each layer communicates through interfaces registered in the DI container. This is the single biggest enabler for non-invasive instrumentation: because every service is resolved through an interface (`IProductService`, `IPriceCalculationService`, `IStaticCacheManager`, `IRepository<T>`), we can replace or wrap any implementation at the DI level without touching the original class. Additionally, nopCommerce marks its service methods as `virtual`, which allowed us to use the subclass-override pattern — `InstrumentedProductService` extends `ProductService` and overrides `SearchProductsAsync` and `GetProductByIdAsync` to add tracing spans and metrics before delegating to `base`. The `INopStartup` interface with its `Order` property gave us a clean insertion point: our `OpenTelemetryStartup` (Order 2001) runs after the default `NopStartup` (Order 2000), so it can remove and replace DI registrations without modifying the original startup class.

**What hindered.** The data layer uses Linq2DB, which — unlike Entity Framework — does not emit `DiagnosticSource` events. This means there is no built-in hook to observe database queries. We had to create `InstrumentedRepository<T>`, a decorator around `EntityRepository<T>`, that manually wraps every `IRepository<T>` method to record spans and durations. This required one surgical change to `NopDbStartup.cs` (see below). The static cache manager (`IStaticCacheManager`) also posed a challenge: it has no built-in observability, and because it uses a callback-based acquire pattern (`GetAsync(key, acquireFunc)`), detecting hits vs. misses requires intercepting whether the acquire function was actually invoked. Our `InstrumentedStaticCacheManager` decorator uses a boolean flag (`wasMiss`) toggled inside a wrapped callback to distinguish the two cases. Finally, nopCommerce's `IEventPublisher` system — which fires `EntityInserted`, `EntityUpdated`, and `EntityDeleted` events — could theoretically serve as an instrumentation hook, but in practice it only covers write operations and does not expose read-path events like searches or product views, which are the core of our chosen flow.

### What would you change to make nopCommerce more observable?

**1. Adopt an ORM that emits diagnostic events.** Replacing Linq2DB with Entity Framework Core (or contributing `DiagnosticSource` support to Linq2DB) would eliminate the need for the repository decorator entirely. EF Core publishes query events via `DiagnosticListener`, which OpenTelemetry can subscribe to out of the box. The cost: a significant migration effort and potential performance differences, since Linq2DB is chosen for its lightweight, expression-tree-based query generation.

**2. Extend the event system to cover reads.** Currently, `IEventPublisher` only fires on entity mutations. Adding events like `ProductSearched` or `ProductViewed` would allow instrumentation via event consumers rather than service subclassing. The cost is modest — a few `PublishAsync` calls in service methods — but it changes the contract of those services and adds overhead to every read operation, even when no consumer is listening.

**3. Native support for cache observability.** The `IStaticCacheManager` interface could expose hit/miss counters or accept a callback for telemetry, rather than requiring an external decorator to infer cache behaviour from the acquire pattern. This is a low-cost change to the interface, but it breaks all existing implementations.

Each of these changes trades development effort and coupling for better observability. For a production system, the event-system extension (option 2) offers the best cost/benefit ratio — it is additive, does not require replacing infrastructure, and makes instrumentation a first-class concern.

### Where did you make surgical changes, and why?

We modified exactly **two original nopCommerce files**. Everything else is additive (new files only).

**1. `src/Libraries/Nop.Data/NopDbStartup.cs` (line 63–65)** — Changed the `IRepository<>` registration from:
```csharp
services.AddScoped(typeof(IRepository<>), typeof(EntityRepository<>));
```
to:
```csharp
services.AddScoped(typeof(EntityRepository<>));
services.AddScoped(typeof(IRepository<>), typeof(InstrumentedRepository<>));
```
**Why:** Linq2DB has no diagnostic events, so there is no non-invasive way to observe database operations. The decorator pattern requires the DI container to resolve `InstrumentedRepository<T>` (which wraps `EntityRepository<T>`) instead of `EntityRepository<T>` directly. This two-line change was the minimal modification needed to instrument the entire data layer — every repository call across the application now produces tracing spans and duration metrics, without any service-layer code being aware of it.

**2. `src/Presentation/Nop.Web.Framework/Nop.Web.Framework.csproj` (lines 22–25)** — Added four OpenTelemetry NuGet package references:
```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.12.0-rc.1" />
```
**Why:** The OpenTelemetry SDK must be referenced somewhere in the dependency chain. We chose `Nop.Web.Framework` because it already depends on `Nop.Services` and `Nop.Data`, and it is the layer responsible for cross-cutting infrastructure concerns like startup configuration. This avoided adding dependencies to the core domain or service layers.

**Impact minimisation:** Both changes are isolated, small, and backwards-compatible. The DI registration change preserves the existing `IRepository<T>` contract — all consumers still receive an `IRepository<T>`, they are simply unaware that it is now instrumented. The package references are additive and do not affect any existing code. All other instrumentation — `InstrumentedProductService`, `InstrumentedPriceCalculationService`, `InstrumentedStaticCacheManager`, `PiiSanitizingProcessor`, `CatalogInstrumentation`, `DataInstrumentation`, and `OpenTelemetryStartup` — lives in new files that can be removed without affecting the rest of the application.

---

## Stopping Everything

```bash
# Stop all infrastructure (SQL Server + observability)
docker compose -f docker-compose.yml -f docker-compose.observability.yml down

# Stop nopCommerce (Ctrl+C in the terminal running dotnet)
```
