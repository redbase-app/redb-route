using System.Globalization;

namespace redb.Route.Firebase;

/// <summary>
/// Safely reads DateTimeOffset from a GCS object, falling back to manual parsing
/// when the SDK's typed accessor throws (e.g. non-RFC 3339 from emulators).
/// Always returns <see cref="DateTimeOffset"/>? — never a raw string.
/// </summary>
internal static class GcsDateTimeHelper
{
    /// <summary>
    /// Safely resolves a DateTimeOffset from a GCS object property.
    /// </summary>
    /// <param name="accessor">SDK typed accessor (e.g. obj.TimeCreatedDateTimeOffset).</param>
    /// <param name="raw">Raw JSON string (e.g. obj.TimeCreatedRaw).</param>
    internal static DateTimeOffset? SafeParse(Func<DateTimeOffset?> accessor, string? raw)
    {
        try
        {
            return accessor();
        }
        catch (FormatException)
        {
            // SDK couldn't parse — try manual parse of the raw string.
            if (raw is not null &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
            {
                return dto;
            }

            return null;
        }
    }
}
