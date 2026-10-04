using FluentValidation;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Products;

/// <summary>
/// The upper bound of <c>sizeBytes</c> is configuration (<c>ObjectStorage:MaxImageBytes</c>) and is checked by
/// <see cref="IProductImageService.PresignAsync"/>; the declared values are a courtesy check either way - the confirmation
/// looks at the stored object.
/// </summary>
public sealed class PresignProductImageRequestValidator : AbstractValidator<PresignProductImageRequest>
{
    public PresignProductImageRequestValidator()
    {
        RuleFor(x => x.ContentType)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(ProductImageRules.IsAllowed)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ImageTypeNotAllowed));
        RuleFor(x => x.SizeBytes).GreaterThanOrEqualTo(1);
    }
}

public sealed class ConfirmProductImageRequestValidator : AbstractValidator<ConfirmProductImageRequest>
{
    public ConfirmProductImageRequestValidator()
    {
        RuleFor(x => x.ObjectKey).NotEmpty();
    }
}
