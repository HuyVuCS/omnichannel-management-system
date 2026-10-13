using Microsoft.Extensions.Options;
using Omnichannel.Application.Products;
using Omnichannel.Workers.ImageCleanupWorker;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 026c Part B2: the settings of the image clean-up and the margin its safety rests on.</summary>
public sealed class ImageCleanupOptionsTests
{
    [Fact]
    public void The_defaults_are_valid_every_six_hours_objects_older_than_a_day()
    {
        var options = new ImageCleanupOptions();

        Assert.Equal(
            (true, TimeSpan.FromHours(6), TimeSpan.FromHours(24), 500),
            (options.Enabled, options.Interval, options.MinAge, options.MaxPerRun));
        Assert.Empty(ImageCleanupOptionsRules.Validate(options));
    }

    [Fact]
    public void The_shortest_age_is_four_times_the_deadline_of_a_confirmation()
    {
        // The confirmation locks the product within the deadline after its copy, then saves and commits (each at most
        // the command timeout of 30 s): 30 + 60 s is less than the two minutes below.
        Assert.Equal(TimeSpan.FromSeconds(30), ProductImageRules.ConfirmationDeadline);
        Assert.Equal(TimeSpan.FromMinutes(2), ImageCleanupOptionsRules.MinMinAge);
        Assert.Equal(ProductImageRules.ConfirmationDeadline * 4, ImageCleanupOptionsRules.MinMinAge);
    }

    [Theory]
    [InlineData("00:02:00", true)]
    [InlineData("00:01:59", false)]
    [InlineData("00:00:00", false)]
    [InlineData("-00:00:01", false)]
    [InlineData("365.00:00:00", true)]
    [InlineData("365.00:00:01", false)]
    public void An_age_out_of_range_is_one_failure_that_names_the_setting_also_when_the_clean_up_is_off(string minAge, bool valid)
    {
        foreach (var enabled in new[] { true, false })
        {
            var failures = ImageCleanupOptionsRules.Validate(new ImageCleanupOptions { Enabled = enabled, MinAge = TimeSpan.Parse(minAge) });

            if (valid)
            {
                Assert.Empty(failures);
            }
            else
            {
                Assert.StartsWith("ImageCleanup:MinAge", Assert.Single(failures), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("00:00:01", 1, 0)]
    [InlineData("7.00:00:00", 10_000, 0)]
    [InlineData("00:00:00.999", 1, 1)]
    [InlineData("7.00:00:01", 1, 1)]
    [InlineData("06:00:00", 0, 1)]
    [InlineData("06:00:00", 10_001, 1)]
    [InlineData("00:00:00", -1, 2)]
    public void Interval_and_limit_have_ranges(string interval, int maxPerRun, int failures)
    {
        var options = new ImageCleanupOptions { Interval = TimeSpan.Parse(interval), MaxPerRun = maxPerRun };

        Assert.Equal(failures, ImageCleanupOptionsRules.Validate(options).Count);
    }

    [Fact]
    public void The_host_s_validator_reports_the_rules()
    {
        var validator = new ImageCleanupOptionsValidator();

        Assert.True(validator.Validate(Options.DefaultName, new ImageCleanupOptions()).Succeeded);
        var failed = validator.Validate(Options.DefaultName, new ImageCleanupOptions { MinAge = TimeSpan.FromSeconds(5), MaxPerRun = 0 });
        Assert.True(failed.Failed);
        Assert.Equal(2, failed.Failures.Count());
    }
}
