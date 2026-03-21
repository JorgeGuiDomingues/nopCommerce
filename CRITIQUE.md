# Critique

## What in nopCommerce's design helped or hindered instrumentation?

**What helped.** nopCommerce follows a strict layered architecture — `Nop.Core` (domain), `Nop.Data` (persistence), `Nop.Services` (business logic), and `Nop.Web.Framework`/`Nop.Web` (presentation). Each layer communicates through interfaces registered in the DI container. This is the single biggest enabler for non-invasive instrumentation: because every service is resolved through an interface (`IProductService`, `IPriceCalculationService`, `IStaticCacheManager`, `IRepository<T>`), we can replace or wrap any implementation at the DI level without touching the original class. Additionally, nopCommerce marks its service methods as `virtual`, which allowed us to use the subclass-override pattern — `InstrumentedProductService` extends `ProductService` and overrides `SearchProductsAsync` and `GetProductByIdAsync` to add tracing spans and metrics before delegating to `base`. The `INopStartup` interface with its `Order` property gave us a clean insertion point: our `OpenTelemetryStartup` (Order 2001) runs after the default `NopStartup` (Order 2000), so it can remove and replace DI registrations without modifying the original startup class.

**What hindered.** The data layer uses Linq2DB, which — unlike Entity Framework — does not emit `DiagnosticSource` events. This means there is no built-in hook to observe database queries. We had to create `InstrumentedRepository<T>`, a decorator around `EntityRepository<T>`, that manually wraps every `IRepository<T>` method to record spans and durations. This required one surgical change to `NopDbStartup.cs` (see below). The static cache manager (`IStaticCacheManager`) also posed a challenge: it has no built-in observability, and because it uses a callback-based acquire pattern (`GetAsync(key, acquireFunc)`), detecting hits vs. misses requires intercepting whether the acquire function was actually invoked. Our `InstrumentedStaticCacheManager` decorator uses a boolean flag (`wasMiss`) toggled inside a wrapped callback to distinguish the two cases. Finally, nopCommerce's `IEventPublisher` system — which fires `EntityInserted`, `EntityUpdated`, and `EntityDeleted` events — could theoretically serve as an instrumentation hook, but in practice it only covers write operations and does not expose read-path events like searches or product views, which are the core of our chosen flow.

## What would you change to make nopCommerce more observable?

**1. Adopt an ORM that emits diagnostic events.** Replacing Linq2DB with Entity Framework Core (or contributing `DiagnosticSource` support to Linq2DB) would eliminate the need for the repository decorator entirely. EF Core publishes query events via `DiagnosticListener`, which OpenTelemetry can subscribe to out of the box. The cost: a significant migration effort and potential performance differences, since Linq2DB is chosen for its lightweight, expression-tree-based query generation.

**2. Extend the event system to cover reads.** Currently, `IEventPublisher` only fires on entity mutations. Adding events like `ProductSearched` or `ProductViewed` would allow instrumentation via event consumers rather than service subclassing. The cost is modest — a few `PublishAsync` calls in service methods — but it changes the contract of those services and adds overhead to every read operation, even when no consumer is listening.

**3. Native support for cache observability.** The `IStaticCacheManager` interface could expose hit/miss counters or accept a callback for telemetry, rather than requiring an external decorator to infer cache behaviour from the acquire pattern. This is a low-cost change to the interface, but it breaks all existing implementations.

Each of these changes trades development effort and coupling for better observability. For a production system, the event-system extension (option 2) offers the best cost/benefit ratio — it is additive, does not require replacing infrastructure, and makes instrumentation a first-class concern.

## Where did you make surgical changes, and why?

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
