using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Firebase;

/// <summary>
/// Fluent API entry point for Firebase Storage endpoints.
/// <example>
/// <code>
/// // Consumer: poll for new uploads
/// .From(FirebaseStorage.Bucket("my-app.appspot.com")
///         .Prefix("uploads/").Include("*.csv")
///         .DeleteAfterRead().Delay(10000).Build())
///
/// // Producer: upload
/// .To(FirebaseStorage.Bucket("my-app.appspot.com")
///         .Operation(FirebaseStorageOperationType.Upload)
///         .ObjectName("${header.fileName}")
///         .ContentType("application/json").Build())
///
/// // Producer: download
/// .To(FirebaseStorage.Bucket("my-app.appspot.com")
///         .Operation(FirebaseStorageOperationType.Download).Build())
/// </code>
/// </example>
/// </summary>
public static class FirebaseStorage
{
    /// <summary>Creates a Firebase Storage endpoint builder for the given bucket.</summary>
    /// <param name="bucket">Bucket name (e.g. <c>"my-app.appspot.com"</c>).</param>
    public static FirebaseStorageBuilder Bucket(string bucket) => new(bucket, null);

    /// <summary>Creates a Firebase Storage endpoint builder for a bucket with prefix.</summary>
    /// <param name="bucket">Bucket name.</param>
    /// <param name="prefix">Object prefix (e.g. <c>"uploads/"</c>).</param>
    public static FirebaseStorageBuilder Bucket(string bucket, string prefix) => new(bucket, prefix);
}

/// <summary>
/// Fluent builder for Firebase Storage endpoint URIs.
/// </summary>
public sealed class FirebaseStorageBuilder
{
    private readonly string _bucket;
    private readonly string? _prefix;
    private readonly Dictionary<string, string> _params = new();

    internal FirebaseStorageBuilder(string bucket, string? prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        _bucket = bucket;
        _prefix = prefix;
    }

    /// <summary>Producer operation type.</summary>
    public FirebaseStorageBuilder Operation(FirebaseStorageOperationType op) => Set("operation", op);

    /// <summary>Object name/key.</summary>
    public FirebaseStorageBuilder ObjectName(string name) => Set("objectName", name);

    /// <summary>Object name from expression.</summary>
    public FirebaseStorageBuilder ObjectName(IExpression name) => Set("objectName", name.ToTemplateString());

    /// <summary>MIME content type.</summary>
    public FirebaseStorageBuilder ContentType(string ct) => Set("contentType", ct);

    /// <summary>Cache-Control header value.</summary>
    public FirebaseStorageBuilder CacheControl(string value) => Set("cacheControl", value);

    /// <summary>Path to service-account JSON file.</summary>
    public FirebaseStorageBuilder CredentialPath(string v) => Set("credentialPath", v);

    /// <summary>Firebase project ID.</summary>
    public FirebaseStorageBuilder ProjectId(string v) => Set("projectId", v);

    /// <summary>Named connection factory reference.</summary>
    public FirebaseStorageBuilder ConnectionFactory(string v) => Set("connectionFactory", v);

    /// <summary>Download as Stream (true) or byte[] (false).</summary>
    public FirebaseStorageBuilder StreamBody(bool v = true) => Set("streamBody", v);

    /// <summary>Poll interval (ms).</summary>
    public FirebaseStorageBuilder Delay(int ms) => Set("delay", ms);

    /// <summary>Object name prefix for listing.</summary>
    public FirebaseStorageBuilder Prefix(string p) => Set("prefix", p);

    /// <summary>Max objects per poll cycle.</summary>
    public FirebaseStorageBuilder MaxMessagesPerPoll(int n) => Set("maxMessagesPerPoll", n);

    /// <summary>Delete object after consumer processing.</summary>
    public FirebaseStorageBuilder DeleteAfterRead(bool v = true) => Set("deleteAfterRead", v);

    /// <summary>Move objects to prefix after processing.</summary>
    public FirebaseStorageBuilder MoveAfterRead(string prefix) => Set("moveAfterRead", prefix);

    /// <summary>Skip previously processed objects.</summary>
    public FirebaseStorageBuilder Idempotent(bool v = true) => Set("idempotent", v);

    /// <summary>Include glob pattern.</summary>
    public FirebaseStorageBuilder Include(string glob) => Set("include", glob);

    /// <summary>Exclude glob pattern.</summary>
    public FirebaseStorageBuilder Exclude(string glob) => Set("exclude", glob);

    /// <summary>Include downloaded body in exchange.</summary>
    public FirebaseStorageBuilder IncludeBody(bool v = true) => Set("includeBody", v);

    /// <summary>Builds the Firebase Storage URI string.</summary>
    public string Build()
    {
        var path = _prefix is not null ? $"{_bucket}/{_prefix}" : _bucket;
        if (_params.Count == 0)
            return $"fbstorage://{path}";

        var sb = new StringBuilder($"fbstorage://{path}?");
        var first = true;
        foreach (var (key, value) in _params)
        {
            if (!first) sb.Append('&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(FirebaseStorageBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();

    private FirebaseStorageBuilder Set(string k, object v) { _params[k] = v.ToString()!; return this; }
}
