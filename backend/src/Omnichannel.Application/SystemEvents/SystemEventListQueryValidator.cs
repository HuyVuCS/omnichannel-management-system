using FluentValidation;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.SystemEvents;

public sealed class SystemEventListQueryValidator : PageQueryValidator<SystemEventListQuery>
{
    // Column lengths of SystemEventLogs.EventType / CorrelationId.
    public const int MaxEventTypeLength = 100;
    public const int MaxCorrelationIdLength = 100;

    public SystemEventListQueryValidator()
        : base(SystemEventListQuery.SortFields)
    {
        RuleForEach(x => x.Level).IsInEnum();
        RuleForEach(x => x.EventType).NotEmpty().MaximumLength(MaxEventTypeLength);
        RuleFor(x => x.CorrelationId).MaximumLength(MaxCorrelationIdLength);

        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeInverted,
                ("From", FieldNames.ForMember(nameof(SystemEventListQuery.From)))));

        RuleFor(x => x.To)
            .Must((query, to) => to!.Value - query.From!.Value <= TimeSpan.FromDays(SystemEventListQuery.MaxRangeDays))
            .When(x => x.From.HasValue && x.To.HasValue && x.To >= x.From)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeTooLong,
                ("From", FieldNames.ForMember(nameof(SystemEventListQuery.From))),
                ("MaxDays", SystemEventListQuery.MaxRangeDays)));
    }
}
