using FluentValidation;
using FluentValidation.Results;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Dashboard;

/// <summary>
/// Rules for a range whose two ends were both given. A range that is completed with the defaults (only one end given) is
/// checked by <see cref="DashboardRange.Resolve"/> with the same rules and messages.
/// </summary>
public sealed class DashboardSummaryQueryValidator : AbstractValidator<DashboardSummaryQuery>
{
    public DashboardSummaryQueryValidator()
    {
        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeEmpty,
                ("From", FieldNames.ForMember(nameof(DashboardSummaryQuery.From)))));

        RuleFor(x => x.To)
            .Must((query, to) => to!.Value - query.From!.Value <= TimeSpan.FromDays(DashboardSummaryQuery.MaxRangeDays))
            .When(x => x.From.HasValue && x.To.HasValue && x.To > x.From)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeTooLong,
                ("From", FieldNames.ForMember(nameof(DashboardSummaryQuery.From))),
                ("MaxDays", DashboardSummaryQuery.MaxRangeDays)));
    }
}

public static class DashboardRange
{
    /// <summary>
    /// The range a query means at <paramref name="now"/>: <c>to</c> defaults to now, <c>from</c> to <c>to</c> − 24 h.
    /// </summary>
    /// <exception cref="ValidationException">
    /// Only <c>from</c> was given and it is not before now, or lies more than the allowed number of days back (key <c>from</c>).
    /// </exception>
    public static (DateTimeOffset From, DateTimeOffset To) Resolve(DashboardSummaryQuery query, DateTimeOffset now)
    {
        var to = query.To ?? now;
        var from = query.From ?? to - DashboardSummaryQuery.DefaultRange;

        if (query.From.HasValue && !query.To.HasValue)
        {
            var key = ValidationConventions.JsonName(nameof(DashboardSummaryQuery.From));
            var fromName = FieldNames.ForMember(nameof(DashboardSummaryQuery.From));
            var toName = FieldNames.ForMember(nameof(DashboardSummaryQuery.To));

            if (from >= to)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(key, ValidationMessages.Get(
                        ValidationMessages.Keys.DateRangeEmpty, ("PropertyName", toName), ("From", fromName)))
                ]);
            }

            if (to - from > TimeSpan.FromDays(DashboardSummaryQuery.MaxRangeDays))
            {
                throw new ValidationException(
                [
                    new ValidationFailure(key, ValidationMessages.Get(
                        ValidationMessages.Keys.DateRangeTooLong,
                        ("PropertyName", toName), ("From", fromName), ("MaxDays", DashboardSummaryQuery.MaxRangeDays)))
                ]);
            }
        }

        return (from, to);
    }
}
