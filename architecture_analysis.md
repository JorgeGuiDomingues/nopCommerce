# Architecture Analysis — nopCommerce v5.00

> This document is an architectural reading of the nopCommerce codebase. Every claim is verified against the actual source code — file paths, class names, and line numbers are provided where relevant.

---

## 1. How Are the Layers Organised and What Are the Dependency Rules Between Them?

### 1.1 Physical Layer Structure

The `src/` directory is organised into four top-level folders, each representing an architectural boundary:

```
src/
├── Libraries/          ← Core logic (no UI, no HTTP)
│   ├── Nop.Core        ← Domain model + Infrastructure (zero project dependencies)
│   ├── Nop.Data        ← Data access via Linq2DB (depends only on Nop.Core)
│   └── Nop.Services    ← Business logic (depends on Nop.Core + Nop.Data)
├── Presentation/       ← Web-facing code
│   ├── Nop.Web.Framework  ← Shared web infrastructure (depends on all Libraries)
│   └── Nop.Web            ← ASP.NET Core MVC host (depends on everything)
├── Plugins/            ← 31 modular extensions (each depends on Libraries)
└── Tests/
    └── Nop.Tests       ← Unit + integration test project
```

### 1.2 Dependency Rules (Verified via .csproj ProjectReferences)

The dependency graph is **strictly acyclic and top-down**. This was verified by inspecting each `.csproj` file:

| Project | References | Source |
|---------|-----------|--------|
| **Nop.Core** | **None** (zero `<ProjectReference>` entries) | `Nop.Core.csproj` |
| **Nop.Data** | Nop.Core | `Nop.Data.csproj:26` |
| **Nop.Services** | Nop.Core, Nop.Data | `Nop.Services.csproj:32-33` |
| **Nop.Web.Framework** | Nop.Core, Nop.Data, Nop.Services | `Nop.Web.Framework.csproj:24-26` |
| **Nop.Web** | Nop.Core, Nop.Data, Nop.Services, Nop.Web.Framework | `Nop.Web.csproj:22-25` |

**The key rule**: dependencies always point **downward**. `Nop.Core` never references any other nopCommerce project. `Nop.Data` never references `Nop.Services`. And so on. This is a textbook Layered Architecture enforcement. Note that `Nop.Web` explicitly references all four lower-level projects (not just `Nop.Web.Framework`), which means it has direct access to services, data, and core — a practical convenience that slightly weakens strict layer isolation in favour of development flexibility.

There is also a notable **architectural tension**: `Nop.Data` (the data access layer) directly references `IEventPublisher` from `Nop.Core.Events` — the `EntityRepository<TEntity>` constructor injects `IEventPublisher` and calls `EntityInsertedAsync()`, `EntityUpdatedAsync()`, and `EntityDeletedAsync()` after CRUD operations (`EntityRepository.cs:19,29,349,402,452`). This means the data layer actively participates in the event system. Although the **interface** lives in `Nop.Core` (which `Nop.Data` is allowed to reference), the **concrete implementation** (`EventPublisher`) lives in `Nop.Services`. This design is intentional: it allows the repository to fire domain events while keeping the dependency direction legal, but it means the data layer has a cross-cutting concern (event publishing) baked into every write operation.

**Plugin dependency rules**: Notably, plugins reference `Nop.Web.csproj` directly (e.g., `Nop.Plugin.Payments.PayPalCommerce.csproj:92`), which transitively gives them access to all layers. The plugin compiled DLLs are output into `Nop.Web/Plugins/` directories (`OutputPath = $(SolutionDir)\Presentation\Nop.Web\Plugins\...`), and a post-build target (`ClearPluginAssemblies`) removes duplicate shared DLLs to keep plugin folders lean.

### 1.3 Layer Responsibilities

**Nop.Core** — the innermost layer, holding:
- **Domain entities** in `Domain/` — 29 subdomains (`Catalog`, `Customers`, `Orders`, `Payments`, `Shipping`, etc.), each containing POCO classes that inherit from `BaseEntity` (an abstract class providing `int Id` — `BaseEntity.cs:6,11`).
- **Marker interfaces** that declare cross-cutting entity behaviours: `ISoftDeletedEntity` (soft-delete via `Deleted` flag), `ILocalizedEntity` (multi-language), `IAclSupported` (role-based access), `IStoreMappingSupported` (multi-tenant), `ISlugSupported` (SEO URLs), `IDiscountSupported<T>` (discounts), `IMetaTagsSupported` (HTML meta tags). These are verified on `Product.cs:13` which implements all seven.
- **Infrastructure contracts**: `IEngine`, `ITypeFinder`, `INopStartup`, `IStartupTask`, `IOrderedMapperProfile`.
- **Event contracts**: `IEventPublisher` (`Nop.Core/Events/IEventPublisher.cs`).
- **Caching contracts**: `IStaticCacheManager`, `IShortTermCacheManager`, `ICacheKeyService` with several implementations including `DistributedCacheManager`, `MemoryCacheManager`.
- **The engine**: `NopEngine.cs` (bootstrapper) and `EngineContext.cs` (static singleton accessor via `Singleton<IEngine>.Instance`).

**Nop.Data** — a thin persistence layer:
- `IRepository<TEntity>` interface and its single concrete implementation `EntityRepository<TEntity>` (529 lines).
- `INopDataProvider` interface (342 lines) with three providers: `MsSqlDataProvider`, `MySqlDataProvider`, `PostgreSqlDataProvider` — all using **Linq2DB** (not Entity Framework).
- **FluentMigrator** for schema migrations (`MigrationManager.cs`, `NopMigrationAttribute`, versioned migration folders `UpgradeTo440` through `UpgradeTo500`).

**Nop.Services** — the business-logic layer, containing 38 subdirectories that mirror and extend the domain:
- Service pairs like `IProductService`/`ProductService`, `IOrderService`/`OrderService`, etc.
- `IConsumer<T>` interface and `EventPublisher` implementation.
- 75+ `CacheEventConsumer` classes for cache invalidation.
- Plugin management (`IPlugin`, `BasePlugin`, `PluginManager<T>`, and 10 specialised plugin managers).
- Scheduling (`IScheduleTask`, `ScheduleTaskRunner`).
- The custom `ILogger` interface (database-backed, 116 lines) — this is **not** `Microsoft.Extensions.Logging.ILogger`.

**Nop.Web.Framework** — shared web infrastructure:
- 10 `INopStartup` implementations (e.g., `NopMvcStartup`, `ErrorHandlerStartup`, `AuthenticationStartup`, `NopRoutingStartup`).
- Base controllers: `BaseController`, `BasePluginController`, `BasePaymentController`.
- 4 custom middleware classes: `KeepAliveMiddleware`, `ThemesMiddleware`, `InstallUrlMiddleware`, `AuthenticationMiddleware`.
- The central DI registration file `NopStartup.cs` (335 lines, `Order = 2000`) — this is where **every** service interface is manually mapped to its implementation (line 73–312), and where all `IConsumer<T>` implementations are auto-discovered and registered (line 305–312).

**Nop.Web** — the executable host:
- `Program.cs` (52 lines) bootstraps the application.
- `Areas/Admin/` — a full MVC Area for the backoffice, with its own `BaseAdminController`.
- `Factories/` — 42 ViewModel factory classes (`IProductModelFactory`, `ICatalogModelFactory`, etc.) implementing the **Thin Controller** pattern.
- `Themes/DefaultClean/` — a single bundled theme with `Content/` and `Views/`.

### 1.4 Cross-Layer Communication

Communication between layers happens through **three mechanisms**:

1. **Constructor injection (DI)**: The primary mechanism. `NopStartup.cs` registers all bindings. Services receive `IRepository<T>` instances; controllers receive service interfaces.
2. **Event system**: The `IEventPublisher` / `IConsumer<T>` pair allows decoupled cross-layer communication. Events flow upward: the repository (Data layer) publishes entity lifecycle events → consumers in Services and Plugins react.
3. **Service Locator**: `EngineContext.Current.Resolve<T>()` is used where constructor injection is impractical (e.g., static extension methods, middleware configuration). This is visible throughout `ApplicationBuilderExtensions.cs`.

---

## 2. How Does nopCommerce Handle Events Internally — What Is `IEventPublisher` and How Is It Used?

### 2.1 The Event Architecture

nopCommerce implements a **synchronous, in-process Publish/Subscribe (Observer) pattern** for internal events.

**Interface contract** (`Nop.Core/Events/IEventPublisher.cs`):
```csharp
public partial interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event);
}
```

**Consumer contract** (`Nop.Services/Events/IConsumer.cs`):
```csharp
public partial interface IConsumer<T>
{
    Task HandleEventAsync(T eventMessage);
}
```

The interface `IEventPublisher` lives in `Nop.Core.Events` (the innermost layer), making it available to all layers. The concrete `EventPublisher` class lives in `Nop.Services.Events` and is registered as a **singleton** (`NopStartup.cs:234`).

### 2.2 How Publishing Works (EventPublisher.cs — 55 lines)

When `PublishAsync<TEvent>()` is called, the `EventPublisher`:

1. **Resolves all consumers** dynamically from the DI container:
   ```csharp
   var consumers = EngineContext.Current.ResolveAll<IConsumer<TEvent>>().ToList();
   ```
2. **Iterates and invokes** each consumer's `HandleEventAsync()` **sequentially** (not in parallel).
3. **Handles errors gracefully**: if a consumer throws, the exception is logged via `ILogger` (with a nested try-catch to prevent logging loops). Other consumers continue executing.
4. **Supports chain interruption**: if the event implements `IStopProcessingEvent` and its `StopProcessing` property is `true`, the loop breaks early — no further consumers are called.

### 2.3 How Consumers Are Registered

All `IConsumer<T>` implementations across the entire solution (including plugins) are **automatically discovered and registered** during startup. In `NopStartup.cs:305-312`:

```csharp
var consumers = typeFinder.FindClassesOfType(typeof(IConsumer<>)).ToList();
foreach (var consumer in consumers)
    foreach (var findInterface in consumer.FindInterfaces(..., typeof(IConsumer<>)))
        services.AddScoped(findInterface, consumer);
```

This means a plugin can implement `IConsumer<EntityInsertedEvent<Product>>` and it will be automatically called whenever a product is inserted — with zero configuration and zero modification to the core code.

### 2.4 Where Events Are Published

Events are published at two key layers:

**1. Data Layer (EntityRepository)** — automatic entity lifecycle events:
- `InsertAsync()` → publishes `EntityInsertedEvent<TEntity>` (line 349)
- `UpdateAsync()` → publishes `EntityUpdatedEvent<TEntity>` (line 402)
- `DeleteAsync()` → publishes `EntityDeletedEvent<TEntity>` (line 452)

Each of these has a `bool publishEvent = true` parameter, allowing event suppression for bulk/internal operations.

**2. Service Layer** — business-level events:
- Services publish domain-specific events (e.g., `OrderPaidEvent`, `OrderPlacedEvent`, `CustomerLoggedInEvent`).
- Application lifecycle events like `AppStartedEvent` (`ApplicationBuilderExtensions.cs:66`).

### 2.5 Event Consumers in Practice

The most prominent use of the event system is **cache invalidation**. There are **75+ `CacheEventConsumer` classes** across `Nop.Services/`, each subscribing to entity lifecycle events to clear the relevant cache entries:

- `ProductCacheEventConsumer` — clears product caches on insert/update/delete
- `CategoryCacheEventConsumer` — clears category caches
- `CustomerCacheEventConsumer` — clears customer caches
- etc.

This pattern decouples cache management from business logic: the `ProductService` does not need to know about caching — the `CacheEventConsumer` handles it reactively.

### 2.6 Architectural Implications

- **Synchronous execution**: Events are `await`-ed sequentially. A slow consumer blocks the entire pipeline. There is no built-in support for asynchronous, fire-and-forget, or queued event processing.
- **No event filtering**: All registered consumers of a type are invoked. There is no subscription-level filtering or conditional routing.
- **Tight coupling to `EngineContext`**: The `EventPublisher` uses the Service Locator pattern (`EngineContext.Current.ResolveAll<>()`) rather than constructor injection to discover consumers. This makes it harder to test and harder to wrap with instrumentation.

---

## 3. Where Does the Code Make It Easy to Add Observability, and Where Does It Make It Hard?

### 3.1 Where Observability Is Easy

#### 3.1.1 The Event System as a Natural Hook

The `IEventPublisher` / `IConsumer<T>` system is the **single best insertion point for observability**. Because every entity write operation automatically publishes an event, an instrumentation plugin can:

- Implement `IConsumer<EntityInsertedEvent<Order>>` to trace order creation
- Implement `IConsumer<EntityUpdatedEvent<Product>>` to monitor product updates
- React to business events like `OrderPaidEvent` without modifying any existing service

This is architecturally significant because it allows observability to be added **as a plugin** — following the same extension model that the platform already uses for business features. No core code needs to change.

#### 3.1.2 DI-Based Service Registration

All service bindings in `NopStartup.cs` follow the pattern `services.AddScoped<IProductService, ProductService>()`. Because every service is behind an interface, it is straightforward to:

- **Register a decorator** that wraps the real service with timing/tracing logic. For example, replacing `ProductService` with a `TracingProductService` that delegates to the original and records spans.
- **Use DI interceptors** (via Autofac, when enabled) to add cross-cutting concerns like logging, metrics, and tracing around method invocations.

#### 3.1.3 The `partial class` Pattern

Every entity, service, and infrastructure class is declared as `partial class`. For example: `public partial class ProductService : IProductService` (`ProductService.cs:26`). This allows developers to extend any class with additional methods in a separate file without modifying the original source — useful for adding instrumentation methods or hooks.

#### 3.1.4 The `virtual` Keyword on Service Methods

Service methods are consistently marked as `virtual`, enabling subclass-based instrumentation. A plugin can inherit from `ProductService`, override key methods to add tracing, and re-register the binding in an `INopStartup`.

#### 3.1.5 The `INopStartup` Extension Point

The startup discovery mechanism (`ITypeFinder.FindClassesOfType<INopStartup>()`) means an observability module can create its own startup class that:

- Registers additional middleware (e.g., a request tracing middleware)
- Overrides service registrations with instrumented versions
- Configures exporters and telemetry pipelines

This can be done from a plugin, without touching `Program.cs`.

#### 3.1.6 The ASP.NET Core Middleware Pipeline

The HTTP request pipeline is built through `ApplicationBuilderExtensions.cs` with standard ASP.NET Core middleware. Adding a tracing or metrics middleware (e.g., OpenTelemetry ASP.NET Core instrumentation) is a matter of inserting `app.UseOpenTelemetryPrometheusExporter()` or similar calls at the appropriate point in the pipeline. The `INopStartup.Configure()` method provides the extension point to do this.

#### 3.1.7 Pre-Existing Observability Mechanisms

nopCommerce already has **two built-in observability systems**, though neither integrates with modern telemetry standards:

- **`ILogger` / `DefaultLogger`** (`Nop.Services/Logging/`): A database-backed error/warning/info logger. Records are stored in a `Log` table with columns for log level, short message, full message, IP address, customer reference, and timestamp. Accessible through the Admin panel under System > Log.
- **`ICustomerActivityService` / `CustomerActivityService`** (`Nop.Services/Logging/`): An audit-trail system that tracks user actions. Calls like `InsertActivityAsync("EditProduct", "Edited product: {Name}", product)` are scattered throughout admin controllers. Each `ActivityLog` record contains: a `SystemKeyword` (e.g., `"AddNewProduct"`, `"EditOrder"`), a comment string, the customer ID, IP address, entity name, entity ID, and creation timestamp. This is registered at `NopStartup.cs:218`.

These two systems provide a foundation: they demonstrate that the codebase already has "observability awareness" at the application level. However, they both write exclusively to the database, have no structured context or correlation IDs, and are invisible to external monitoring tools. An instrumentation effort could leverage these existing patterns — or at minimum, not duplicate them — by bridging their output to standard telemetry pipelines.

### 3.2 Where Observability Is Hard

#### 3.2.1 The Custom `ILogger` Is Not `Microsoft.Extensions.Logging.ILogger`

This is the **most significant observability obstacle**. nopCommerce defines its own `Nop.Services.Logging.ILogger` (`ILogger.cs`) — a database-backed logger with methods like `InsertLogAsync(LogLevel, shortMessage, fullMessage, customer)`. It is **not** the standard `Microsoft.Extensions.Logging.ILogger`.

This means:
- **Standard .NET logging integrations do not work**. Libraries like Serilog, Application Insights, OpenTelemetry LogExporter, etc., hook into `Microsoft.Extensions.Logging` — none of them will capture nopCommerce's application logs.
- **Structured logging is absent**. The custom logger writes to a `Log` database table. There are no structured fields, no correlation IDs, no trace context — just a short message string and an optional exception.
- All internal error logging (e.g., `EventPublisher` logging errors at line 44, `ApplicationBuilderExtensions` logging 404s at line 149) goes through this custom `ILogger`, bypassing the standard .NET pipeline entirely.

#### 3.2.2 The Service Locator Pattern (`EngineContext.Current.Resolve<T>()`)

Throughout the codebase, services are resolved via the static `EngineContext.Current.Resolve<T>()` call rather than constructor injection. This is particularly prevalent in:

- `EventPublisher.cs:23` — `EngineContext.Current.ResolveAll<IConsumer<TEvent>>()`
- `EventPublisher.cs:40` — `EngineContext.Current.Resolve<ILogger>()`
- `ApplicationBuilderExtensions.cs` — dozens of `EngineContext.Current.Resolve<>()` calls (lines 65, 75, 104, 107, 132, 142, 146, 213, etc.)

The Service Locator pattern makes instrumentation difficult because:
- You cannot easily wrap or intercept the resolution mechanism.
- Dependencies are hidden — there's no constructor that declares "I need these services", so an observability tool cannot know in advance which services a class uses.
- Testing with instrumented replacements requires modifying the global `EngineContext` state.

#### 3.2.3 Linq2DB Does Not Integrate with Standard Tracing

nopCommerce uses Linq2DB instead of Entity Framework Core. EF Core has built-in support for `System.Diagnostics.Activity` and emits tracing data that OpenTelemetry can capture automatically. Linq2DB, in contrast, **does not emit any diagnostic events by default**. This means:

- Database query durations are invisible to standard tracing tools.
- There is no automatic slow-query detection or query logging pipeline.
- Instrumenting the data layer requires wrapping `INopDataProvider` or `IRepository<T>` implementations manually.

#### 3.2.4 No Existing Telemetry Infrastructure

A search for `OpenTelemetry`, `Telemetry`, `System.Diagnostics.Activity`, `ActivitySource`, and `DiagnosticSource` across the entire codebase returned **zero results**. There is:

- No distributed tracing
- No metrics collection
- No span propagation
- No correlation IDs in logs or events
- No health check endpoints (beyond the `KeepAliveMiddleware` which simply returns HTTP 200)

Everything must be built from scratch.

#### 3.2.5 Event System Limitations for Observability

While the event system is a natural hook (see 3.1.1), it has specific limitations:

- **No timing data**: Events are published after the fact (`InsertAsync` → insert into DB → publish event). There is no "before" event to capture start time, making it impossible to measure operation duration through events alone.
- **No context propagation**: Events carry only the entity data. There is no concept of trace context, correlation ID, or parent span flowing through the event.
- **Synchronous execution**: An observability consumer that does anything slow (e.g., sends data to a remote collector) will block the entire request pipeline.

#### 3.2.6 Hard-Coded Singleton for ITypeFinder

`ITypeFinder` is accessed globally via `Singleton<ITypeFinder>.Instance` throughout the engine code. This makes it impossible to substitute or wrap with an instrumented version through standard DI — it's resolved before the DI container is built.

---

## 4. What Would You Need to Change Structurally to Instrument It Properly — and Is That Change Worth Making?

### 4.1 Change 1: Bridge the Custom Logger to `Microsoft.Extensions.Logging`

**What to change**: Create an adapter that implements `Nop.Services.Logging.ILogger` but forwards log entries to `Microsoft.Extensions.Logging.ILogger<T>` in addition to (or instead of) writing to the database.

**Why**: This unlocks the entire .NET logging ecosystem — Serilog, Application Insights, OpenTelemetry log exporters, structured logging with correlation IDs. It also enables log correlation with traces and metrics.

**How invasive**: **Low**. Replace the DI registration of `ILogger` → `DefaultLogger` with `ILogger` → `BridgeLogger` in a custom `INopStartup`. No core code needs modification.

**Is it worth it**: **Absolutely**. This is the highest-impact, lowest-effort change. Without it, all application logs are trapped in a database table with no structured context.

### 4.2 Change 2: Add OpenTelemetry Middleware via INopStartup

**What to change**: Create a new `INopStartup` implementation (ideally as a plugin) that:
- Calls `services.AddOpenTelemetry()` with tracing, metrics, and logging
- Adds `app.UseOpenTelemetryPrometheusScrapingEndpoint()` or configures OTLP export
- Registers ASP.NET Core auto-instrumentation for HTTP request tracing

**Why**: This adds request-level tracing (HTTP method, status code, duration, route) with zero modification to existing code. ASP.NET Core's built-in `DiagnosticSource` events are already being emitted by the framework; they just need a listener.

**How invasive**: **Very low**. A single `INopStartup` class, registered automatically by the engine. Can be packaged as a plugin.

**Is it worth it**: **Yes**. This provides immediate visibility into request latency, error rates, and throughput — the "golden signals" of observability — with minimal effort.

### 4.3 Change 3: Wrap `IRepository<T>` for Data Layer Tracing

**What to change**: Create a generic decorator `InstrumentedRepository<T> : IRepository<T>` that:
- Starts a `System.Diagnostics.Activity` span before each operation
- Delegates to the real `EntityRepository<T>`
- Records duration, entity type, operation type, and error status

Then replace the DI registration in an `INopStartup` to register `InstrumentedRepository<T>` as the implementation of `IRepository<T>`.

**Why**: Since Linq2DB does not emit tracing data, and every database interaction goes through `IRepository<T>`, this decorator captures all data access patterns in one place.

**How invasive**: **Low to medium**. The DI registration for `IRepository<T>` is an open generic binding in `Nop.Data/NopDbStartup.cs:63`: `services.AddScoped(typeof(IRepository<>), typeof(EntityRepository<>))`. The decorator must be inserted by overriding this registration in a custom `INopStartup` with a higher `Order` value. No existing code is modified.

**Is it worth it**: **Yes**. Database access is typically the most instrumented layer in any application. Without this, all SQL operations are invisible to the tracing pipeline. The generic nature of `IRepository<T>` makes a single decorator cover all entities.

### 4.4 Change 4: Add "Before" Events or Use AOP for Service-Level Timing

**What to change**: Either:
- (Option A) Extend the event system to publish "before" events (e.g., `EntityInsertingEvent<T>` before the insert, `EntityInsertedEvent<T>` after), enabling consumers to measure duration.
- (Option B) Use Autofac interceptors (when Autofac is enabled) or Castle DynamicProxy to add timing around service method calls.

**Why**: The current event system only publishes "after" events, making it impossible to measure how long an operation took through the event system alone.

**How invasive**:
- Option A: **Medium**. Requires modifying `EntityRepository.cs` to publish a new event type before each operation. This changes core code.
- Option B: **Low**. DI interceptors can be configured in a custom `INopStartup` without modifying existing classes.

**Is it worth it**: **Conditionally**. Option B (DI interceptors) is worth it if fine-grained service-level timing is needed. Option A (before events) is a larger change that might not justify the effort if the repository decorator (Change 3) already provides data-layer timing.

### 4.5 Change 5: Replace`EngineContext.Current.Resolve<>()` Usage with Constructor Injection

**What to change**: Refactor the cases where `EngineContext.Current.Resolve<T>()` is used (particularly in `EventPublisher` and `ApplicationBuilderExtensions`) to use constructor injection instead.

**Why**: Service Locator hides dependencies and makes interception impossible. Constructor injection makes the dependency graph explicit and allows standard DI-based instrumentation (decorators, interceptors).

**How invasive**: **High**. This is a significant refactoring that touches core infrastructure code. The `EventPublisher` would need to receive `IEnumerable<IConsumer<T>>` via constructor (which is complex for open generics). Application builder extensions would need restructuring.

**Is it worth it**: **Probably not** — at least not as a first step. The observability gains from Changes 1–3 are large and low-cost. Refactoring the Service Locator is a code-quality improvement that would benefit observability indirectly, but the cost/benefit ratio is unfavourable for instrumentability alone.

### 4.6 Summary: Recommended Instrumentation Strategy

| Priority | Change | Effort | Impact | Modifies Core? |
|----------|--------|--------|--------|----------------|
| **1** | OpenTelemetry middleware via `INopStartup` | Very low | High (HTTP tracing) | No |
| **2** | Logger bridge to `Microsoft.Extensions.Logging` | Low | High (structured logs) | No |
| **3** | `IRepository<T>` decorator for DB tracing | Low-Medium | High (data layer visibility) | No |
| **4** | DI interceptors for service-level timing | Low | Medium (service timing) | No |
| **5** | "Before" events in EntityRepository | Medium | Medium (event-level timing) | Yes |
| **6** | Refactor Service Locator usage | High | Low-Medium (indirect) | Yes |

The first four changes can be implemented entirely through the plugin/startup extension mechanism, requiring **zero modifications to the core nopCommerce source code**. This is a testament to the platform's extensible architecture — the same patterns that enable third-party payment gateways and shipping providers also enable clean observability instrumentation.
