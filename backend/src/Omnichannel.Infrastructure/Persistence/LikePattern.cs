namespace Omnichannel.Infrastructure.Persistence;

/// <summary>Builds (I)LIKE patterns from user input so that <c>%</c>, <c>_</c> and <c>\</c> match literally.</summary>
public static class LikePattern
{
    /// <summary>Escape character passed to <c>EF.Functions.ILike</c> (PostgreSQL's default).</summary>
    public const string EscapeCharacter = @"\";

    /// <summary>Pattern matching values that contain <paramref name="text"/>.</summary>
    public static string Contains(string text) => $"%{Escape(text)}%";

    public static string Escape(string text) => text
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
