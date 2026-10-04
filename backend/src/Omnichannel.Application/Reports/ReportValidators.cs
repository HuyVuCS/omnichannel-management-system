using FluentValidation;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Reports;

/// <summary>
/// The filters are checked like those of the order list. The longest range is configuration
/// (<c>Reports:MaxRangeDays</c>) and is checked by the service, with the message of the other date ranges.
/// </summary>
public sealed class ExportOrdersRequestValidator : AbstractValidator<ExportOrdersRequest>
{
    public ExportOrdersRequestValidator()
    {
        RuleFor(x => x.Channel).MaximumLength(CreateChannelRequestValidator.MaxSlugLength);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Search).MaximumLength(PageQuery.MaxSearchLength);
        RuleFor(x => x.From).NotNull();
        RuleFor(x => x.To).NotNull();
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeInverted,
                ("From", FieldNames.ForMember(nameof(ExportOrdersRequest.From)))));
        RuleFor(x => x.TimeZone).KnownTimeZone();
    }
}

public sealed class ExportInventoryRequestValidator : AbstractValidator<ExportInventoryRequest>
{
    public ExportInventoryRequestValidator()
    {
        RuleFor(x => x.Search).MaximumLength(PageQuery.MaxSearchLength);
        RuleFor(x => x.TimeZone).KnownTimeZone();
    }
}

public sealed class ReportExportListQueryValidator : PageQueryValidator<ReportExportListQuery>
{
    public ReportExportListQueryValidator()
        : base(ReportExportListQuery.SortFields)
    {
        RuleFor(x => x.ReportType).IsInEnum();
    }
}

internal static class ReportValidationRules
{
    /// <summary>Required, and a time zone this host knows by that id.</summary>
    public static IRuleBuilderOptions<T, string?> KnownTimeZone<T>(this IRuleBuilderInitial<T, string?> rule) =>
        rule
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(ReportTimeZones.IsKnown)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.TimeZoneUnknown));
}
