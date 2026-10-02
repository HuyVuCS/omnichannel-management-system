using FluentValidation;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Alerts;

public sealed class AlertListQueryValidator : PageQueryValidator<AlertListQuery>
{
    public AlertListQueryValidator()
        : base(AlertListQuery.SortFields)
    {
        RuleForEach(x => x.Status).IsInEnum();
        RuleForEach(x => x.Type).IsInEnum();

        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeInverted,
                ("From", FieldNames.ForMember(nameof(AlertListQuery.From)))));

        RuleFor(x => x.To)
            .Must((query, to) => to!.Value - query.From!.Value <= TimeSpan.FromDays(AlertListQuery.MaxRangeDays))
            .When(x => x.From.HasValue && x.To.HasValue && x.To >= x.From)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeTooLong,
                ("From", FieldNames.ForMember(nameof(AlertListQuery.From))),
                ("MaxDays", AlertListQuery.MaxRangeDays)));
    }
}
