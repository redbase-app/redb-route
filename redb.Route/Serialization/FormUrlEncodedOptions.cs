using System.Text;

namespace redb.Route.Serialization;

/// <summary>
/// Options of <see cref="FormUrlEncodedMessageSerializer"/>. The limits apply wherever the form comes from (HTTP, a file,
/// a queue), not only behind a web server; the defaults are those of ASP.NET Core's <c>FormOptions</c>.
/// </summary>
public sealed class FormUrlEncodedOptions
{
    /// <summary>
    /// Character encoding of the decoded bytes. Default UTF-8, what browsers send from a UTF-8 page; the
    /// <c>charset</c> parameter is not part of this content type, so a sender with another encoding needs its own
    /// format instance.
    /// </summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    /// <summary>Most fields one form may have. Default 1024.</summary>
    public int MaxFields { get; set; } = 1024;

    /// <summary>Longest field name, in characters after decoding. Default 2048.</summary>
    public int MaxNameLength { get; set; } = 2048;

    /// <summary>Longest field value, in characters after decoding. Default 4 194 304 (4 M).</summary>
    public int MaxValueLength { get; set; } = 4 * 1024 * 1024;
}
