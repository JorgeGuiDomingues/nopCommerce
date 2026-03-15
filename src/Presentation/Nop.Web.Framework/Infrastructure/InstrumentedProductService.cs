using System.Diagnostics;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Discounts;
using Nop.Core.Domain.Localization;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Shipping;
using Nop.Core.Domain.Stores;
using Nop.Data;
using Nop.Services.Catalog;
using Nop.Services.Customers;
using Nop.Services.Localization;
using Nop.Services.Security;
using Nop.Services.Shipping.Date;
using Nop.Services.Stores;
using Nop.Services.Vendors;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// Instrumented subclass of ProductService that adds OpenTelemetry tracing spans
/// and custom metrics to the two key methods of the "search and view product" flow:
/// SearchProductsAsync and GetProductByIdAsync.
/// 
/// Uses the "virtual override" extensibility pattern — no core code is modified.
/// Registered in OpenTelemetryStartup to replace the default ProductService binding.
/// </summary>
public class InstrumentedProductService : ProductService
{
    public InstrumentedProductService(
        CatalogSettings catalogSettings,
        IAclService aclService,
        ICustomerService customerService,
        IDateRangeService dateRangeService,
        ILanguageService languageService,
        ILocalizationService localizationService,
        IProductAttributeParser productAttributeParser,
        IProductAttributeService productAttributeService,
        IRepository<Category> categoryRepository,
        IRepository<CrossSellProduct> crossSellProductRepository,
        IRepository<DiscountProductMapping> discountProductMappingRepository,
        IRepository<LocalizedProperty> localizedPropertyRepository,
        IRepository<Manufacturer> manufacturerRepository,
        IRepository<Product> productRepository,
        IRepository<ProductAttributeCombination> productAttributeCombinationRepository,
        IRepository<ProductAttributeMapping> productAttributeMappingRepository,
        IRepository<ProductCategory> productCategoryRepository,
        IRepository<ProductManufacturer> productManufacturerRepository,
        IRepository<ProductPicture> productPictureRepository,
        IRepository<ProductProductTagMapping> productTagMappingRepository,
        IRepository<ProductSpecificationAttribute> productSpecificationAttributeRepository,
        IRepository<ProductTag> productTagRepository,
        IRepository<ProductVideo> productVideoRepository,
        IRepository<ProductWarehouseInventory> productWarehouseInventoryRepository,
        IRepository<RelatedProduct> relatedProductRepository,
        IRepository<Shipment> shipmentRepository,
        IRepository<StockQuantityHistory> stockQuantityHistoryRepository,
        IRepository<TierPrice> tierPriceRepository,
        ISearchPluginManager searchPluginManager,
        IStaticCacheManager staticCacheManager,
        IVendorService vendorService,
        IStoreMappingService storeMappingService,
        IWorkContext workContext,
        LocalizationSettings localizationSettings)
        : base(catalogSettings, aclService, customerService, dateRangeService,
            languageService, localizationService, productAttributeParser, productAttributeService,
            categoryRepository, crossSellProductRepository, discountProductMappingRepository,
            localizedPropertyRepository, manufacturerRepository, productRepository,
            productAttributeCombinationRepository, productAttributeMappingRepository,
            productCategoryRepository, productManufacturerRepository, productPictureRepository,
            productTagMappingRepository, productSpecificationAttributeRepository,
            productTagRepository, productVideoRepository, productWarehouseInventoryRepository,
            relatedProductRepository, shipmentRepository, stockQuantityHistoryRepository,
            tierPriceRepository, searchPluginManager, staticCacheManager,
            vendorService, storeMappingService, workContext, localizationSettings)
    {
    }

    /// <summary>
    /// Instrumented wrapper around SearchProductsAsync.
    /// Creates a tracing span and records the search results count metric.
    /// Search keywords are sanitised before being set as span attributes.
    /// </summary>
    public override async Task<IPagedList<Product>> SearchProductsAsync(
        int pageIndex = 0,
        int pageSize = int.MaxValue,
        IList<int> categoryIds = null,
        IList<int> manufacturerIds = null,
        int storeId = 0,
        int vendorId = 0,
        int warehouseId = 0,
        ProductType? productType = null,
        bool visibleIndividuallyOnly = false,
        bool excludeFeaturedProducts = false,
        decimal? priceMin = null,
        decimal? priceMax = null,
        int productTagId = 0,
        string keywords = null,
        bool searchDescriptions = false,
        bool searchManufacturerPartNumber = true,
        bool searchSku = true,
        bool searchProductTags = false,
        int languageId = 0,
        IList<SpecificationAttributeOption> filteredSpecOptions = null,
        ProductSortingEnum orderBy = ProductSortingEnum.Position,
        bool showHidden = false,
        bool? overridePublished = null)
    {
        using var activity = CatalogInstrumentation.ActivitySource.StartActivity("ProductService.SearchProducts");

        // Record sanitised search metadata (no PII)
        activity?.SetTag("search.has_keywords", !string.IsNullOrWhiteSpace(keywords));
        activity?.SetTag("search.category_filtered", categoryIds != null && categoryIds.Any(id => id > 0));
        activity?.SetTag("search.page_index", pageIndex);
        activity?.SetTag("search.page_size", pageSize);

        var result = await base.SearchProductsAsync(
            pageIndex, pageSize, categoryIds, manufacturerIds, storeId, vendorId,
            warehouseId, productType, visibleIndividuallyOnly, excludeFeaturedProducts,
            priceMin, priceMax, productTagId, keywords, searchDescriptions,
            searchManufacturerPartNumber, searchSku, searchProductTags, languageId,
            filteredSpecOptions, orderBy, showHidden, overridePublished);

        var totalCount = result?.TotalCount ?? 0;
        activity?.SetTag("search.total_results", totalCount);

        // Record the custom metric
        CatalogInstrumentation.SearchResultsCount.Record(
            totalCount,
            new KeyValuePair<string, object>("search.has_keywords", !string.IsNullOrWhiteSpace(keywords)),
            new KeyValuePair<string, object>("search.category_filtered", categoryIds != null && categoryIds.Any(id => id > 0)));

        return result;
    }

    /// <summary>
    /// Instrumented wrapper around GetProductByIdAsync.
    /// Creates a tracing span and records the product view duration metric.
    /// </summary>
    public override async Task<Product> GetProductByIdAsync(int productId)
    {
        using var activity = CatalogInstrumentation.ActivitySource.StartActivity("ProductService.GetProductById");
        activity?.SetTag("product.id", productId);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var product = await base.GetProductByIdAsync(productId);

        sw.Stop();
        activity?.SetTag("product.found", product != null);

        // Record the custom metric
        CatalogInstrumentation.ProductViewDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object>("product.found", product != null));

        return product;
    }
}
