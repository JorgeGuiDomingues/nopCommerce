# Instrumentação OpenTelemetry — "Customer Searches and Views a Product"

## 1. Resumo do que foi implementado

Instrumentámos o fluxo **"Customer searches and views a product"** do nopCommerce com OpenTelemetry, cobrindo os três requisitos do enunciado:

1. **Distributed Tracing** — spans desde o ponto de entrada HTTP até à base de dados
2. **Três métricas custom** com justificação operacional real
3. **Exclusão de dados sensíveis** — sem emails, dados de pagamento ou PII nos traces

### Fluxo instrumentado

```
[Browser] → HTTP Request
    └─ CatalogController.Search() / ProductController.ProductDetails()    ← span automático (ASP.NET Core)
        └─ InstrumentedProductService.SearchProductsAsync()                ← span custom
            └─ InstrumentedRepository<Product>.GetAllPaged()               ← span custom (DB)
        └─ InstrumentedProductService.GetProductByIdAsync()                ← span custom
            └─ InstrumentedRepository<Product>.GetById()                   ← span custom (DB)
        └─ InstrumentedStaticCacheManager.GetAsync()                       ← métrica cache hit/miss
```

---

## 2. Ficheiros criados e modificados

### Ficheiros novos (sem tocar no código core)

| Ficheiro | Camada | Propósito |
|----------|--------|-----------|
| `Nop.Services/Catalog/CatalogInstrumentation.cs` | Services | Define o `ActivitySource` e `Meter` partilhados + as 4 métricas |
| `Nop.Data/InstrumentedRepository.cs` | Data | Decorador genérico `InstrumentedRepository<T>` que envolve o `EntityRepository<T>` com spans de tracing para cada operação CRUD |
| `Nop.Web.Framework/Infrastructure/InstrumentedProductService.cs` | Web.Framework | Subclasse de `ProductService` que faz override de `SearchProductsAsync()` e `GetProductByIdAsync()` com spans e métricas |
| `Nop.Web.Framework/Infrastructure/InstrumentedStaticCacheManager.cs` | Web.Framework | Decorador de `IStaticCacheManager` que regista métricas de cache hit/miss |
| `Nop.Web.Framework/Infrastructure/PiiSanitizingProcessor.cs` | Web.Framework | `BaseProcessor<Activity>` que remove/redacta PII dos spans antes da exportação |
| `Nop.Web.Framework/Infrastructure/OpenTelemetryStartup.cs` | Web.Framework | `INopStartup` (Order=2001) que configura o SDK OpenTelemetry, regista os serviços instrumentados e o endpoint Prometheus |
| `docker-compose.observability.yml` | Raiz | Docker Compose para Jaeger + Prometheus + Grafana |
| `observability/prometheus.yml` | Raiz | Configuração do Prometheus para fazer scraping do endpoint `/metrics` |
| `observability/grafana/provisioning/` | Raiz | Ficheiros de provisioning do Grafana (datasources para Prometheus e Jaeger) |

### Ficheiros modificados (apenas 2)

| Ficheiro | Alteração |
|----------|-----------|
| `Nop.Web.Framework.csproj` | Adicionados 4 pacotes NuGet do OpenTelemetry |
| `Nop.Data/NopDbStartup.cs` | Alterada 1 linha: registo de `IRepository<>` agora usa `InstrumentedRepository<>` como decorador |

---

## 3. Métricas custom — justificação

### Métrica 1: `nopcommerce.search.results_count` (Histograma)

**O que mede:** Número de produtos devolvidos por cada pesquisa.

**Justificação operacional:**
> Se a mediana dos resultados de pesquisa cair para zero, significa que o catálogo de produtos está vazio, o índice de pesquisa está corrompido ou uma migração de dados falhou. Um operador pode correlacionar esta queda com o último deployment ou migração e agir imediatamente. Também ajuda a detectar quando os filtros de pesquisa são demasiado agressivos — se utilizadores pesquisam mas não encontram resultados, é um sinal de que a configuração de busca precisa de ajuste.

**Tags:** `search.has_keywords` (bool), `search.category_filtered` (bool)

**Registado em:** `InstrumentedProductService.SearchProductsAsync()` — após cada chamada bem-sucedida.

---

### Métrica 2: `nopcommerce.catalog.product_view_duration_ms` (Histograma)

**O que mede:** Duração (em milissegundos) da obtenção dos dados de um produto.

**Justificação operacional:**
> Se o p95 da duração de carregamento da página de produto disparar de 50ms para 500ms, indica uma regressão de performance no motor de preços, na resolução de tier prices ou nas queries à base de dados — tudo isto corre durante o `PrepareProductDetailsModelAsync()`. Um operador pode agir sobre este sinal antes dos clientes começarem a ver páginas lentas ou timeouts. Esta métrica funciona como um "canário" — avisa sobre degradação antes de os utilizadores reportarem problemas.

**Tags:** `product.found` (bool)

**Registado em:** `InstrumentedProductService.GetProductByIdAsync()` — mede o tempo total de obtenção do produto.

---

### Métrica 3: `nopcommerce.cache.hits` e `nopcommerce.cache.misses` (Contadores)

**O que mede:** Número de cache hits e cache misses no `IStaticCacheManager`.

**Justificação operacional:**
> Uma queda repentina no rácio de cache hits indica que a cache está a ser invalidada demasiado agressivamente (por exemplo, após um clear manual ou um bug na configuração de TTL). Quando a cache falha, todas as queries vão directamente à base de dados, aumentando a carga e a latência. Um operador pode usar o rácio hit/miss para:
> - Detectar "cache storms" após deployments
> - Identificar chaves de cache que nunca são reutilizadas (desperdício de memória)
> - Correlacionar degradação de performance com o estado da cache

**Tags:** `cache.key_prefix` (string) — prefixo da chave para agrupar por domínio (ex: "Nop", "product", etc.)

**Registado em:** `InstrumentedStaticCacheManager.GetAsync()` — usando o padrão de callback: se o `acquire` é invocado, é um miss; se não, é um hit.

---

### Métrica 4: `nopcommerce.catalog.errors` (Counter)

**O que mede:** Número de erros e exceções lançados durante as operações do catálogo (Pesquisa e Detalhes do Produto).

**Justificação operacional:**
> Permite criar alertas críticos na camada de Observabilidade. Se o rácio de erros subir (ex: timeouts na base de dados ou falha de componentes web), dispara um alerta que avisa preventivamente a equipa de suporte de que a funcionalidade principal da loja está instável para os clientes.

**Tags:** `operation_name` (string), `error_type` (string)

**Registado em:** `InstrumentedProductService.SearchProductsAsync()` e `GetProductByIdAsync()` — acionado num bloco `catch` sempre que uma exceção quebra o fluxo.

---

### Métrica 5: `nopcommerce.database.query_duration_ms` (Histograma)

**O que mede:** Duração exata (em milissegundos) de resposta de todas as queries enviadas à Base de Dados pelo catálogo.

**Justificação operacional:**
> Se a página de um produto ou a pesquisa ficar lenta, uma métrica explícita de latência da DB diz-nos *imediatamente* se a culpa da degradação está na Base de Dados (ex: falhas de hardware, locks no SQL Server) ou no processamento CPU do servidor web (ASP.NET Core). Isto reduz substancialmente o Mean Time to Resolution (MTTR).

**Tags:** `db.operation` (string), `db.entity` (string)

**Registado em:** `InstrumentedRepository<T>` — mede o tempo que cada chamada CRUD do Linq2DB demora a processar e devolve no formato de um Snapshot de Latência.

---

## 4. Exclusão de dados sensíveis (PII)

O `PiiSanitizingProcessor` é um `BaseProcessor<Activity>` que corre **centralmente** no pipeline de exportação do OpenTelemetry — antes de qualquer span ser enviado para o Jaeger. Isto é melhor do que tentar proteger cada ponto de instrumentação individualmente.

**O que é filtrado:**
- **Atributos bloqueados por nome exacto:** `customer.email`, `customer.name`, `payment.card_number`, `payment.cvv`, `user.email`, `http.request.header.cookie`, `http.request.header.authorization`, etc.
- **Atributos com padrões sensíveis:** qualquer atributo cujo nome contenha `password`, `token`, `secret`, `creditcard`, `ssn`
- **Emails em valores de texto:** regex que detecta padrões `user@domain.com` em qualquer valor de atributo e substitui por `[EMAIL_REDACTED]`

**Exemplo:** Se alguém pesquisar por "test@example.com", o span mostrará `search.keywords = "[EMAIL_REDACTED]"` em vez do email real.

---

## 5. Como testar a aplicação

### Pré-requisitos

- Docker e Docker Compose instalados
- .NET 9 SDK instalado (para correr localmente fora de Docker)

### Opção A: Correr tudo localmente (recomendado para desenvolvimento)

#### Passo 1: Iniciar o stack de observabilidade

```bash
cd /Users/jorgedomingues/Mestrado/1Ano/2Semestre/AS/P/nopCommerce
docker compose -f docker-compose.observability.yml up -d
```

Isto inicia:
- **Jaeger** em `http://localhost:16686` (UI de traces)
- **Prometheus** em `http://localhost:9090` (métricas)
- **Grafana** em `http://localhost:3000` (dashboards, login: admin/admin)

#### Passo 2: Restaurar e correr a aplicação

```bash
cd src
dotnet restore Nop.sln
dotnet run --project Presentation/Nop.Web/Nop.Web.csproj
```

A aplicação vai arrancar em `http://localhost:5000` (ou a porta configurada).

#### Passo 3: Gerar tráfego

1. Abrir o browser em `http://localhost:5000`
2. **Pesquisar:** Ir à barra de pesquisa, escrever "laptop" e submeter
3. **Ver produto:** Clicar num dos produtos dos resultados da pesquisa

#### Passo 4: Verificar traces no Jaeger

1. Abrir `http://localhost:16686`
2. No campo "Service", seleccionar `nopcommerce-web`
3. Clicar "Find Traces"
4. Deves ver traces com spans hierárquicos:
   - `GET /search` (HTTP, automático)
     - `ProductService.SearchProducts` (custom)
       - `Repository.GetAllPaged` (DB)
   - `GET /product-slug` (HTTP, automático)
     - `ProductService.GetProductById` (custom)
       - `Repository.GetById` (DB)

#### Passo 5: Verificar métricas no Prometheus

1. Abrir `http://localhost:9090`
2. No campo de query, escrever e executar:
   - `nopcommerce_search_results_count_bucket` — histograma de resultados de pesquisa
   - `nopcommerce_catalog_product_view_duration_ms_milliseconds_bucket` — histograma de duração
   - `nopcommerce_cache_hits_total` — total de cache hits
   - `nopcommerce_cache_misses_total` — total de cache misses
   - `nopcommerce_catalog_errors_total` — total de erros registados no catálogo
   - `nopcommerce_database_query_duration_ms_milliseconds_bucket` — histograma de latência das queries de base de dados
   - `rate(nopcommerce_cache_hits_total[5m])` — taxa de cache hits por segundo

#### Passo 6: Verificar o dashboard no Grafana

1. Abrir `http://localhost:3000` (login: admin/admin)
2. Ir a "Explore" → seleccionar "Jaeger" → procurar por `nopcommerce-web`
3. Ir a "Explore" → seleccionar "Prometheus" → executar queries acima

#### Passo 7: Verificar exclusão de PII

1. Na pesquisa do nopCommerce, pesquisar por `test@example.com`
2. No Jaeger, encontrar o trace correspondente
3. Verificar que o atributo **não** contém o email — deve mostrar `[EMAIL_REDACTED]`

### Opção B: Correr tudo com Docker

```bash
cd /Users/jorgedomingues/Mestrado/1Ano/2Semestre/AS/P/nopCommerce
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
```

Nota: O `docker-compose.yml` original usa SQL Server e expõe a aplicação na porta 80. Os passos de verificação são os mesmos, mas o nopCommerce estará em `http://localhost:80`.

### Opção C: Verificar rapidamente sem Docker (apenas métricas)

Se não tiveres Docker, podes verificar que o endpoint de métricas funciona localmente:

```bash
cd src
dotnet run --project Presentation/Nop.Web/Nop.Web.csproj
# Noutra terminal:
curl http://localhost:5000/metrics
```

Deves ver output no formato Prometheus com as métricas `nopcommerce_*`.

---

## 6. Arquitectura da instrumentação

```
┌───────────────────────────────────────────────────────────────────┐
│  Nop.Web (Presentation)                                         │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │ ASP.NET Core Auto-Instrumentation (HTTP spans)             │ │
│  │ CatalogController.Search() → ProductController.Details()   │ │
│  └─────────────────────────┬───────────────────────────────────┘ │
│                            │                                     │
│  Nop.Web.Framework                                              │
│  ┌─────────────────────────┴───────────────────────────────────┐ │
│  │ OpenTelemetryStartup (INopStartup, Order=2001)             │ │
│  │ InstrumentedProductService (subclass override)              │ │
│  │ InstrumentedStaticCacheManager (decorator)                  │ │
│  │ PiiSanitizingProcessor (BaseProcessor<Activity>)            │ │
│  └─────────────────────────┬───────────────────────────────────┘ │
│                            │                                     │
│  Nop.Services                                                   │
│  ┌─────────────────────────┴───────────────────────────────────┐ │
│  │ CatalogInstrumentation (ActivitySource + Meter + Métricas) │ │
│  │ ProductService.SearchProductsAsync() ← override            │ │
│  │ ProductService.GetProductByIdAsync()  ← override           │ │
│  └─────────────────────────┬───────────────────────────────────┘ │
│                            │                                     │
│  Nop.Data                                                       │
│  ┌─────────────────────────┴───────────────────────────────────┐ │
│  │ InstrumentedRepository<T> (decorator IRepository<T>)       │ │
│  │ EntityRepository<T> (implementação real)                    │ │
│  │   → Linq2DB → SQL Server                                   │ │
│  └─────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────┘

            Exportação
               │
     ┌─────────┴──────────┐
     │                    │
  ┌──▼──┐           ┌────▼─────┐
  │OTLP │           │Prometheus│
  │gRPC │           │ /metrics │
  └──┬──┘           └────┬─────┘
     │                    │
  ┌──▼──┐           ┌────▼─────┐
  │Jaeger│           │Prometheus│
  │ UI   │           │ Server   │
  └──┬──┘           └────┬─────┘
     │                    │
     └────────┬───────────┘
              │
         ┌────▼────┐
         │ Grafana │
         │Dashboard│
         └─────────┘
```

---

## 7. Decisões técnicas

### Porquê subclasse em vez de partial class?
O nopCommerce usa `partial class` extensivamente, mas `partial class` **não permite fazer override de métodos**. Como `SearchProductsAsync()` e `GetProductByIdAsync()` são `virtual`, a forma correcta de os instrumentar sem tocar no código core é criando uma subclasse (`InstrumentedProductService`) e registando-a no container DI.

### Porquê decorador para o repositório?
O Linq2DB (ORM usado pelo nopCommerce) **não emite eventos de diagnóstico** como o Entity Framework Core faz. Sem um decorador, não haveria spans ao nível da base de dados. O `InstrumentedRepository<T>` é registado como open generic `IRepository<>` e envolve o `EntityRepository<T>` real.

### Porquê Order=2001 no OpenTelemetryStartup?
O `NopStartup` principal tem `Order = 2000`. O nosso `OpenTelemetryStartup` precisa de correr **depois** para poder substituir os registos DI (`IProductService` e `IStaticCacheManager`) que o `NopStartup` já registou.

### Porquê processador central de PII?
Em vez de garantir em cada ponto de instrumentação que não há leak de PII, o `PiiSanitizingProcessor` funciona como uma rede de segurança centralizada. Qualquer atributo sensível que escape é apanhado antes da exportação.



email: admin@yourStore.com
password: admin