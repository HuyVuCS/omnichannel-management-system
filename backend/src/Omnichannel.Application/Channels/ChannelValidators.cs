using FluentValidation;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Channels;

public sealed class CreateChannelRequestValidator : AbstractValidator<CreateChannelRequest>
{
    public const int MaxNameLength = 200;
    public const int MaxSlugLength = 64;

    /// <summary>Lower-case words separated by single hyphens, e.g. <c>digital-platform</c>.</summary>
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    public CreateChannelRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MaxNameLength);

        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(MaxSlugLength)
            .Matches(SlugPattern)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.SlugFormat));

        RuleFor(x => x.Type).IsInEnum();
    }
}

public sealed class UpdateChannelRequestValidator : AbstractValidator<UpdateChannelRequest>
{
    public UpdateChannelRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CreateChannelRequestValidator.MaxNameLength);
        RuleFor(x => x.Status).IsInEnum();
    }
}
