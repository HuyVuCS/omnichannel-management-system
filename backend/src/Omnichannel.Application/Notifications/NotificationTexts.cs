using System.Globalization;
using System.Resources;
using System.Text;

namespace Omnichannel.Application.Notifications;

/// <summary>
/// Every text an e-mail to a customer carries. Texts live in <c>Resources/NotificationTexts.resx</c> (English, the
/// default) and <c>NotificationTexts.vi.resx</c>. An e-mail is written in the language of the <c>Notifications</c>
/// settings, not in the culture of the thread that sends it - the worker has none - so the culture is always passed in.
/// </summary>
public static class NotificationTexts
{
    private static readonly ResourceManager Resources = new(
        "Omnichannel.Application.Resources.NotificationTexts",
        typeof(NotificationTexts).Assembly);

    /// <summary>Resource names. A unit test checks that each one has an English and a Vietnamese text.</summary>
    public static class Keys
    {
        public const string PaymentSubject = nameof(PaymentSubject);
        public const string PaymentBody = nameof(PaymentBody);
        public const string PaymentLine = nameof(PaymentLine);
        public const string DateTimeFormat = nameof(DateTimeFormat);
    }

    /// <summary>The text of <paramref name="key"/> in <paramref name="culture"/>, with <c>{Name}</c> placeholders replaced.</summary>
    public static string Get(string key, CultureInfo culture, params (string Name, string Value)[] arguments)
    {
        // A text of several lines has the line ends of its resource file; an e-mail always gets "\n".
        var text = Resources.GetString(key, culture)?.ReplaceLineEndings("\n")
            ?? throw new InvalidOperationException($"Notification text '{key}' is not defined in NotificationTexts.resx.");

        // One pass over the template: a value that itself contains "{Name}" (a product called that) is never expanded.
        var values = arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);
        var unused = new HashSet<string>(values.Keys, StringComparer.Ordinal);
        var result = new StringBuilder(text.Length + 64);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('{', position);
            var close = open < 0 ? -1 : text.IndexOf('}', open);
            if (close < 0)
            {
                break;
            }

            result.Append(text, position, open - position);
            var name = text[(open + 1)..close];
            if (!values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Notification text '{key}' has a placeholder {{{name}}} without a value.");
            }

            unused.Remove(name);
            result.Append(value);
            position = close + 1;
        }

        if (unused.Count > 0)
        {
            throw new InvalidOperationException($"Notification text '{key}' has no placeholder {{{unused.First()}}}.");
        }

        return result.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>The text stored for exactly <paramref name="culture"/>, without fallback; null when that file lacks the key.</summary>
    public static string? FindExact(string key, CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);
}
