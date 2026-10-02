using FluentValidation;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Application.Channels;

public sealed class CreateChannelProductRequestValidator : AbstractValidator<CreateChannelProductRequest>
{
    public CreateChannelProductRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.ExternalProductId).ExternalId(x => x.ExternalSku, nameof(CreateChannelProductRequest.ExternalSku));
        RuleFor(x => x.ExternalSku).MaximumLength(ChannelProduct.MaxExternalIdLength);
    }
}

public sealed class UpdateChannelProductRequestValidator : AbstractValidator<UpdateChannelProductRequest>
{
    public UpdateChannelProductRequestValidator()
    {
        RuleFor(x => x.ExternalProductId).ExternalId(x => x.ExternalSku, nameof(UpdateChannelProductRequest.ExternalSku));
        RuleFor(x => x.ExternalSku).MaximumLength(ChannelProduct.MaxExternalIdLength);
        RuleFor(x => x.Status).IsInEnum();
    }
}

public sealed class ChannelProductListQueryValidator : PageQueryValidator<ChannelProductListQuery>
{
    public ChannelProductListQueryValidator()
        : base(ChannelProductListQuery.SortFields)
    {
    }
}

internal static class ChannelProductRules
{
    /// <summary>
    /// The external product id: at most <see cref="ChannelProduct.MaxExternalIdLength"/> characters, and required unless the
    /// external SKU is given (a mapping needs one of the two). The error of a mapping without either is reported on the
    /// external product id, naming both fields.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> ExternalId<T>(
        this IRuleBuilder<T, string?> rule,
        Func<T, string?> externalSku,
        string externalSkuMember) =>
        rule
            .MaximumLength(ChannelProduct.MaxExternalIdLength)
            .Must((request, externalProductId) =>
                !string.IsNullOrWhiteSpace(externalProductId) || !string.IsNullOrWhiteSpace(externalSku(request)))
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.ExternalIdRequired,
                ("ExternalSku", FieldNames.ForMember(externalSkuMember))));
}
