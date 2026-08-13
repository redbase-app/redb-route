using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Firebase;

/// <summary>
/// Fluent API entry point for Firestore endpoints.
/// <example>
/// <code>
/// // Realtime consumer:
/// .From(Firestore.Collection("orders")
///         .Where("status==pending")
///         .CredentialPath("/secrets/firebase-sa.json").Build())
///
/// // CRUD producer:
/// .To(Firestore.Collection("orders")
///         .Operation(FirestoreOperationType.Update)
///         .DocumentId("${header['redbFirestore.DocumentId']}")
///         .CredentialPath("/secrets/firebase-sa.json").Build())
///
/// // Query producer:
/// .To(Firestore.Collection("users")
///         .Operation(FirestoreOperationType.Query)
///         .Where("age&gt;18").OrderBy("name").Limit(50).Build())
/// </code>
/// </example>
/// </summary>
public static class Firestore
{
    /// <summary>Creates a Firestore endpoint builder for the given collection path.</summary>
    /// <param name="path">Collection path (e.g. <c>"users"</c>, <c>"users/uid/orders"</c>).</param>
    public static FirestoreBuilder Collection(string path) => new(path);
}

/// <summary>
/// Fluent builder for Firestore endpoint URIs.
/// </summary>
public sealed class FirestoreBuilder
{
    private readonly string _collectionPath;
    private readonly Dictionary<string, string> _params = new();

    internal FirestoreBuilder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _collectionPath = path;
    }

    /// <summary>Producer operation type.</summary>
    public FirestoreBuilder Operation(FirestoreOperationType op) => Set("operation", op);

    /// <summary>Document ID (static value).</summary>
    public FirestoreBuilder DocumentId(string id) => Set("documentId", id);

    /// <summary>Document ID from expression.</summary>
    public FirestoreBuilder DocumentId(IExpression id) => Set("documentId", id.ToTemplateString());

    /// <summary>Where filter (e.g. <c>"status==pending"</c>).</summary>
    public FirestoreBuilder Where(string filter) => Set("where", filter);

    /// <summary>Order by field (e.g. <c>"createdAt"</c> or <c>"createdAt desc"</c>).</summary>
    public FirestoreBuilder OrderBy(string field) => Set("orderBy", field);

    /// <summary>Max documents to return.</summary>
    public FirestoreBuilder Limit(int n) => Set("limit", n);

    /// <summary>Pagination offset.</summary>
    public FirestoreBuilder Offset(int n) => Set("offset", n);

    /// <summary>Merge fields on Set instead of overwriting.</summary>
    public FirestoreBuilder Merge(bool v = true) => Set("merge", v);

    /// <summary>Use realtime snapshot listener (default: true).</summary>
    public FirestoreBuilder Realtime(bool v = true) => Set("realtime", v);

    /// <summary>Poll interval (ms) for non-realtime mode.</summary>
    public FirestoreBuilder Delay(int ms) => Set("delay", ms);

    /// <summary>Body as raw JSON string.</summary>
    public FirestoreBuilder RawJson(bool v = true) => Set("rawJson", v);

    /// <summary>Path to service-account JSON file.</summary>
    public FirestoreBuilder CredentialPath(string v) => Set("credentialPath", v);

    /// <summary>Firebase project ID.</summary>
    public FirestoreBuilder ProjectId(string v) => Set("projectId", v);

    /// <summary>Named connection factory reference.</summary>
    public FirestoreBuilder ConnectionFactory(string v) => Set("connectionFactory", v);

    /// <summary>Builds the Firestore URI string.</summary>
    public string Build()
    {
        if (_params.Count == 0)
            return $"fstore://{_collectionPath}";

        var sb = new StringBuilder($"fstore://{_collectionPath}?");
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
    public static implicit operator string(FirestoreBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();

    private FirestoreBuilder Set(string k, object v) { _params[k] = v.ToString()!; return this; }
}
