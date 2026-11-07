namespace redb.Route.Expressions;

/// <summary>
/// The file facts of an object in an object store (S3, Firebase Storage), for the file keys in the options of their
/// consumers. The name is the whole object key, as <c>${file:name}</c> has always meant for S3 (<c>in/a.csv</c> waits
/// for <c>in/a.csv.done</c>); the parent is the key prefix up to the last <c>/</c>. A key has no path of its own, so
/// the path keys write nothing; the size and the time are the object's when the store gives them, otherwise nothing.
/// </summary>
internal sealed class ObjectKeyFacts(string objectKey, long? length = null, DateTimeOffset? lastModified = null) : IFileFacts
{
    private int LastSlash => objectKey.LastIndexOf('/');

    private int ExtensionDot => objectKey.LastIndexOf('.') is var dot && dot > LastSlash ? dot : -1;

    public string? Name => objectKey;

    public string? NameOnly => ExtensionDot < 0 ? objectKey : objectKey[..ExtensionDot];

    public string? Extension => ExtensionDot < 0 ? "" : objectKey[(ExtensionDot + 1)..];

    public string? Parent => LastSlash < 0 ? "" : objectKey[..LastSlash];

    public string? AbsolutePath => null;

    public string? RelativePath => null;

    public long? Length => length;

    public DateTimeOffset? LastModified => lastModified;
}
