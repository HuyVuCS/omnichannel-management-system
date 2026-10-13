namespace Omnichannel.Application.Products;

/// <summary>
/// The clean-up of image objects nothing refers to, run by the Workers host. Bound from the <c>ImageCleanup</c>
/// configuration section.
/// <para>
/// Two kinds of objects are left behind by design: uploads that were never confirmed (the staging keys), and final
/// objects no product refers to - a replaced or removed image whose delete failed, or the copy of a confirmation that
/// did not finish. None of them is ever served; the sweep removes them so the bucket does not grow for good.
/// </para>
/// </summary>
public sealed class ImageCleanupOptions
{
    public const string SectionName = "ImageCleanup";

    /// <summary>False = this host never deletes an image object by itself.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pause between two runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Only an object the storage wrote at least this long ago is deleted. The margin is what makes the sweep safe
    /// next to a confirmation that is under way (<see cref="ImageCleanupOptionsRules.MinMinAge"/>).
    /// </summary>
    public TimeSpan MinAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Most objects one run deletes; the rest waits for the next run.</summary>
    public int MaxPerRun { get; set; } = 500;
}

/// <summary>Returns one message per setting that is out of range; none when the options are valid.</summary>
public static class ImageCleanupOptionsRules
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>Not a business limit: a longer pause is a typing error.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// The shortest <see cref="ImageCleanupOptions.MinAge"/>: four times <see cref="ProductImageRules.ConfirmationDeadline"/>
    /// (two minutes). A confirmation locks the product within that deadline after its copy or gives up, and then has
    /// two statements left (save and commit, each bounded by the command timeout of 30 s) - so a copy older than this
    /// that no product refers to never will be referred to. A drill uses the minimum; production keeps the default.
    /// </summary>
    public static readonly TimeSpan MinMinAge = ProductImageRules.ConfirmationDeadline * 4;

    /// <summary>Not a business limit: a longer age is a typing error.</summary>
    public static readonly TimeSpan MaxMinAge = TimeSpan.FromDays(365);

    public const int MaxPerRunLimit = 10_000;

    public static IReadOnlyList<string> Validate(ImageCleanupOptions options)
    {
        var failures = new List<string>();

        if (options.Interval < MinInterval || options.Interval > MaxInterval)
        {
            failures.Add($"ImageCleanup:Interval must be between {MinInterval:c} and {MaxInterval:c}.");
        }

        // Checked for a sweep that is switched off as well: switching it on later must not be the moment it turns out wrong.
        if (options.MinAge < MinMinAge || options.MinAge > MaxMinAge)
        {
            failures.Add(
                $"ImageCleanup:MinAge ({options.MinAge:c}) must be between {MinMinAge:c} and {MaxMinAge:c}: an object younger than " +
                "that may be the copy of a confirmation that has not referred to it yet.");
        }

        if (options.MaxPerRun is < 1 or > MaxPerRunLimit)
        {
            failures.Add($"ImageCleanup:MaxPerRun must be between 1 and {MaxPerRunLimit}.");
        }

        return failures;
    }
}

/// <summary>SystemEventLog event types of product images.</summary>
public static class ProductImageEvents
{
    /// <summary>
    /// A run of the image clean-up that deleted at least one object (Information, the run's correlation id). Payload:
    /// <c>{runId, minAgeSeconds, uploads, images, keys, notListed}</c> - <c>uploads</c> counts the deleted staging
    /// objects, <c>images</c> the deleted final objects, <c>keys</c> holds at most <see cref="OrphansDeletedMaxListed"/>
    /// of their keys (ids only, no personal data), <c>notListed</c> counts the rest.
    /// </summary>
    public const string OrphansDeleted = "product_image.orphans_deleted";

    public const int OrphansDeletedMaxListed = 50;
}

/// <summary>How a clean-up run is found again: its event and log lines carry this correlation id.</summary>
public static class ImageCleanupIds
{
    public const string CorrelationIdPrefix = "image-cleanup-";

    public static string CorrelationIdOf(Guid runId) => $"{CorrelationIdPrefix}{runId:N}";
}

/// <param name="Ran">False when another instance was running a clean-up: nothing was looked at.</param>
/// <param name="Listed">Objects under the two image prefixes the run looked at.</param>
/// <param name="DeletedUploads">Keys of the staging objects it deleted (a dry run: would have deleted).</param>
/// <param name="DeletedImages">Keys of the final objects it deleted (a dry run: would have deleted).</param>
/// <param name="Referenced">Final objects old enough that a product refers to: the images in use, left alone.</param>
/// <param name="Unknown">Objects under an image prefix whose key is not one the application generates: left alone (logged).</param>
/// <param name="Errors">Objects that could not be deleted (logged); the next run finds them again.</param>
/// <param name="MoreExpected">True when the run stopped at <see cref="ImageCleanupOptions.MaxPerRun"/>.</param>
public sealed record ImageCleanupResult(
    Guid RunId,
    bool Ran,
    int Listed,
    IReadOnlyList<string> DeletedUploads,
    IReadOnlyList<string> DeletedImages,
    int Referenced,
    int Unknown,
    int Errors,
    bool MoreExpected)
{
    public static ImageCleanupResult NotRun(Guid runId) => new(runId, false, 0, [], [], 0, 0, 0, false);
}

/// <summary>
/// One clean-up run (review 019): deletes every staging object (<see cref="ProductImageKey.StagingPrefix"/>) and every
/// final object (<see cref="ProductImageKey.FinalPrefix"/>) no product refers to, when the storage wrote it more than
/// <see cref="ImageCleanupOptions.MinAge"/> ago. Export files are not touched. One run at a time across all Workers
/// instances.
/// </summary>
public interface IProductImageCleanup
{
    /// <param name="runId">Id of the run; its correlation id is <see cref="ImageCleanupIds.CorrelationIdOf"/>.</param>
    /// <param name="workerName">Worker name of the event the run writes.</param>
    /// <param name="dryRun">
    /// True = look only: the run lists, asks the database and reports what it would delete, but deletes nothing and
    /// writes nothing (no event). For an operator who wants to know what the next run takes before it runs.
    /// </param>
    Task<ImageCleanupResult> RunAsync(Guid runId, string workerName, bool dryRun, CancellationToken cancellationToken);
}
