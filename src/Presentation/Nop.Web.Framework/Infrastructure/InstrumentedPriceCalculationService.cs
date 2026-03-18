using System.Diagnostics;
using Nop.Core.Caching;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Discounts;
using Nop.Core.Domain.Stores;
using Nop.Services.Catalog;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Discounts;
using Nop.Core.Domain.Directory;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// Instrumented subclass of PriceCalculationService that adds an OpenTelemetry tracing span
/// for the "Pricing" step required by the assignment.
/// </summary>
public class InstrumentedPriceCalculationService : PriceCalculationService
{
    public InstrumentedPriceCalculationService(CatalogSettings catalogSettings,
        CurrencySettings currencySettings,
        ICategoryService categoryService,
        ICurrencyService currencyService,
        ICustomerService customerService,
        IDiscountService discountService,
        IManufacturerService manufacturerService,
        IProductAttributeParser productAttributeParser,
        IProductService productService,
        IStaticCacheManager staticCacheManager)
        : base(catalogSettings, currencySettings, categoryService, currencyService,
            customerService, discountService, manufacturerService, productAttributeParser,
            productService, staticCacheManager)
    {
    }

    public override async Task<(decimal priceWithoutDiscounts, decimal finalPrice, decimal appliedDiscountAmount, List<Discount> appliedDiscounts)> GetFinalPriceAsync(
        Product product,
        Customer customer,
        Store store,
        decimal? overriddenProductPrice,
        decimal additionalCharge,
        bool includeDiscounts,
        int quantity,
        DateTime? rentalStartDate,
        DateTime? rentalEndDate)
    {
        using var activity = CatalogInstrumentation.ActivitySource.StartActivity("Pricing");
        activity?.SetTag("product.id", product?.Id);

        return await base.GetFinalPriceAsync(product, customer, store, overriddenProductPrice,
            additionalCharge, includeDiscounts, quantity, rentalStartDate, rentalEndDate);
    }
}
