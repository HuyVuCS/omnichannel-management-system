using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Omnichannel.Api.Auth;

/// <summary>
/// How a channel proves that a webhook is its own (doc §6 "verify signature/secret"): it sends
/// <c>X-Webhook-Timestamp: {unix seconds}</c> and <c>X-Webhook-Signature: v1={hex}</c>, where the hex value is
/// HMAC-SHA256 with the channel's secret over <c>"{timestamp}.{body}"</c> - the timestamp exactly as sent, a dot, and the
/// <b>bytes of the body exactly as sent</b>. Nothing of the body is parsed, normalised or re-serialised for it.
/// <para>
/// The timestamp is part of what is signed, and must be close to the clock of the API: a recorded webhook cannot be sent
/// again later. Sent again within the window it is the same order, which the order intake answers as a duplicate.
/// </para>
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";

    /// <summary>The scheme of a signature value: <c>v1=…</c>. Values of other schemes are ignored.</summary>
    public const string Version = "v1";

    private const int HexLength = SHA256.HashSizeInBytes * 2;

    /// <summary>Whole seconds since 1970-01-01 UTC, digits only: no sign, no spaces, no fraction.</summary>
    public static bool TryParseTimestamp(string? header, out long unixSeconds) =>
        long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out unixSeconds);

    /// <summary>At most <paramref name="tolerance"/> before or after <paramref name="now"/>, compared in whole seconds.</summary>
    public static bool IsWithinTolerance(long unixSeconds, DateTimeOffset now, TimeSpan tolerance) =>
        Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) <= (long)tolerance.TotalSeconds;

    /// <summary>
    /// The <c>v1</c> signatures of the header: one or more comma-separated <c>v1={64 hex digits}</c> values (a sender that
    /// is changing its secret may send one per secret). Everything else - other schemes, values that are not a SHA-256 in
    /// hex - is left out.
    /// </summary>
    public static IReadOnlyList<byte[]> ParseSignatures(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return [];
        }

        var signatures = new List<byte[]>();
        foreach (var value in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = value.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 || !value.AsSpan(0, separator).Equals(Version, StringComparison.Ordinal))
            {
                continue;
            }

            var hex = value.AsSpan(separator + 1);
            if (hex.Length == HexLength && IsHex(hex))
            {
                signatures.Add(Convert.FromHexString(hex));
            }
        }

        return signatures;
    }

    /// <summary>HMAC-SHA256 with <paramref name="secret"/> (its UTF-8 bytes) over the timestamp as sent, a dot, and the body bytes.</summary>
    public static byte[] Compute(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp));
        hmac.AppendData("."u8);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    /// <summary>
    /// True when one of <paramref name="signatures"/> is the signature of the request with one of <paramref name="secrets"/>.
    /// Every pair is compared in constant time, and all of them are compared: how long the check takes says nothing about
    /// which one matched, or how much of it.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> secrets, string timestamp, ReadOnlySpan<byte> body, IReadOnlyList<byte[]> signatures)
    {
        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Compute(secret, timestamp, body);
            foreach (var signature in signatures)
            {
                matched |= CryptographicOperations.FixedTimeEquals(expected, signature);
            }
        }

        return matched;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
