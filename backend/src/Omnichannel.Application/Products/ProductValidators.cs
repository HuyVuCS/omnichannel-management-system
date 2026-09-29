using FluentValidation;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Products;

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public const int MaxSkuLength = 64;

    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(MaxSkuLength);
        RuleFor(x => x.Name).ProductName();
        RuleFor(x => x.Price).ProductPrice();
        RuleFor(x => x.LowStockThreshold).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.Name).ProductName();
        RuleFor(x => x.Price).ProductPrice();
        RuleFor(x => x.LowStockThreshold).GreaterThanOrEqualTo(0);
    }
}

public sealed class ProductListQueryValidator() : PageQueryValidator<ProductListQuery>(ProductListQuery.SortFields);

internal static class ProductRules
{
    public const int MaxNameLength = 300;

    // Matches the numeric(18,2) column.
    private const int PricePrecision = 18;
    private const int PriceScale = 2;

    public static IRuleBuilderOptions<T, string> ProductName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(MaxNameLength);

    public static IRuleBuilderOptions<T, decimal> ProductPrice<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThanOrEqualTo(0).PrecisionScale(PricePrecision, PriceScale, ignoreTrailingZeros: true);
}
