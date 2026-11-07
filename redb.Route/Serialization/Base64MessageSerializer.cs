using System.Text;

namespace redb.Route.Serialization;

/// <summary>Base64 as a data format: <c>Marshal("application/base64")</c> encodes the body, <c>Unmarshal&lt;byte[]&gt;("application/base64")</c> decodes it.</summary>
public sealed class Base64MessageSerializer : BinaryWrapperSerializer
{
    /// <summary>Content type the format is registered under by default.</summary>
    public const string DefaultContentType = "application/base64";

    /// <inheritdoc />
    public override string ContentType => DefaultContentType;

    /// <inheritdoc />
    protected override string FormatName => "Base64";

    /// <inheritdoc />
    protected override byte[] Encode(byte[] payload) => Encoding.ASCII.GetBytes(Convert.ToBase64String(payload));

    /// <inheritdoc />
    protected override byte[] Decode(byte[] data) => Convert.FromBase64String(Encoding.ASCII.GetString(data).Trim());
}
