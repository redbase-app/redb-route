using redb.Route.Expressions;

namespace redb.Route.GenericFile;

/// <summary>
/// The file facts of a file a consumer is handling, for the file keys in its own options (<c>moveTo</c>,
/// <c>preMove</c>, <c>moveFailed</c>, <c>doneFileName</c>, <c>idempotentKey</c>). The values are the ones the file
/// consumer writes into the <c>redbFile.*</c> headers, so <c>${file:ext}</c> in <c>moveTo</c> and in a
/// <c>&lt;log&gt;</c> of the same route agree.
/// </summary>
public sealed class GenericFileFacts : IFileFacts
{
    private readonly IFileOperations _ops;
    private readonly string? _basePath;

    /// <summary>The facts of <paramref name="file"/>, which is now at <paramref name="workPath"/>.</summary>
    /// <param name="file">The polled file.</param>
    /// <param name="workPath">Where the file is now: its own path, or where a pre-move or a read lock put it.</param>
    /// <param name="ops">The transport's path helpers.</param>
    public GenericFileFacts(GenericFileInfo file, string workPath, IFileOperations ops)
        : this(file.Name, workPath, file.BasePath, ops)
    {
        Length = file.Length;
        LastModified = file.LastModified;
    }

    /// <summary>
    /// The facts known from a path alone, for the compatibility form of the consumer hooks: the size and the last
    /// write time are not known here and are <c>null</c>.
    /// </summary>
    public GenericFileFacts(string fileName, string workPath, string? basePath, IFileOperations ops)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(ops);
        _ops = ops;
        _basePath = basePath;
        Name = fileName;
        AbsolutePath = workPath;
    }

    /// <inheritdoc />
    public string? Name { get; }

    /// <inheritdoc />
    public string? NameOnly => _ops.GetFileNameWithoutExtension(Name!);

    /// <inheritdoc />
    public string? Extension => FileLanguage.WithoutDot(_ops.GetExtension(Name!).ToLowerInvariant());

    /// <inheritdoc />
    public string? Parent => string.IsNullOrEmpty(AbsolutePath) ? null : _ops.GetParentPath(AbsolutePath);

    /// <inheritdoc />
    public string? AbsolutePath { get; }

    /// <inheritdoc />
    public string? RelativePath => string.IsNullOrEmpty(AbsolutePath) || _basePath is null
        ? null
        : _ops.GetRelativePath(_basePath, AbsolutePath);

    /// <inheritdoc />
    public long? Length { get; }

    /// <inheritdoc />
    public DateTimeOffset? LastModified { get; }
}
