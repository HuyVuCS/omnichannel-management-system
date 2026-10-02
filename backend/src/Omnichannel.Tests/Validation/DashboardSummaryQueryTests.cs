using System.Globalization;
using FluentValidation;
using Omnichannel.Application.Dashboard;

namespace Omnichannel.Tests.Validation;

/// <summary>Task 015 Part C: the range of the dashboard summary (defaults, order, limit, error keys).</summary>
public sealed class DashboardSummaryQueryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);
    private readonly DashboardSummaryQueryValidator _validator = new();
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    // The messages are compared in English, whatever the language of the machine.
    public DashboardSummaryQueryTests() => CultureInfo.CurrentUICulture = new CultureInfo("en");

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;

    [Fact]
    public void Without_a_range_the_last_24_hours_are_meant()
    {
        Assert.True(_validator.Validate(new DashboardSummaryQuery()).IsValid);

        Assert.Equal((Now.AddHours(-24), Now), DashboardRange.Resolve(new DashboardSummaryQuery(), Now));
    }

    [Fact]
    public void A_single_end_is_completed_with_the_default()
    {
        var to = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(7));
        Assert.Equal((to.AddHours(-24), to), DashboardRange.Resolve(new DashboardSummaryQuery { To = to }, Now));

        var from = Now.AddDays(-10);
        Assert.Equal((from, Now), DashboardRange.Resolve(new DashboardSummaryQuery { From = from }, Now));

        // "to" alone may lie in the future or far in the past: the range is always 24 hours.
        Assert.Equal(
            TimeSpan.FromHours(24),
            DashboardRange.Resolve(new DashboardSummaryQuery { To = Now.AddYears(5) }, Now).To - DashboardRange.Resolve(new DashboardSummaryQuery { To = Now.AddYears(5) }, Now).From);
    }

    [Fact]
    public void Both_ends_given_are_used_as_they_are()
    {
        var query = new DashboardSummaryQuery { From = Now.AddDays(-92), To = Now };

        Assert.True(_validator.Validate(query).IsValid);
        Assert.Equal((query.From.Value, query.To.Value), DashboardRange.Resolve(query, Now.AddYears(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void To_must_be_later_than_from(int secondsAfterFrom)
    {
        var failure = Assert.Single(_validator.Validate(new DashboardSummaryQuery { From = Now, To = Now.AddSeconds(secondsAfterFrom) }).Errors);

        Assert.Equal(("to", "'To date' must be later than 'From date'."), (failure.PropertyName, failure.ErrorMessage));
    }

    [Fact]
    public void The_range_is_at_most_92_days()
    {
        var failure = Assert.Single(_validator.Validate(new DashboardSummaryQuery { From = Now.AddDays(-92).AddSeconds(-1), To = Now }).Errors);

        Assert.Equal(
            ("to", "The range from 'From date' to 'To date' must not be longer than 92 days."),
            (failure.PropertyName, failure.ErrorMessage));
        Assert.Equal(92, DashboardSummaryQuery.MaxRangeDays);
    }

    [Fact]
    public void A_from_that_makes_the_default_range_empty_or_too_long_is_reported_on_from()
    {
        var future = Assert.Throws<ValidationException>(() => DashboardRange.Resolve(new DashboardSummaryQuery { From = Now }, Now));
        Assert.Equal(("from", "'To date' must be later than 'From date'."), (future.Errors.Single().PropertyName, future.Errors.Single().ErrorMessage));

        var old = Assert.Throws<ValidationException>(() => DashboardRange.Resolve(new DashboardSummaryQuery { From = Now.AddDays(-93) }, Now));
        Assert.Equal(
            ("from", "The range from 'From date' to 'To date' must not be longer than 92 days."),
            (old.Errors.Single().PropertyName, old.Errors.Single().ErrorMessage));

        // Exactly 92 days back is still allowed.
        Assert.Equal(Now.AddDays(-92), DashboardRange.Resolve(new DashboardSummaryQuery { From = Now.AddDays(-92) }, Now).From);
    }
}
