using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using redb.Route.Abstractions;

namespace redb.Route.File.Repositories;

/// <summary>
/// File-system-backed implementation of <see cref="IClaimCheckRepository"/>.
/// Each claim is stored as a binary file with a companion metadata file for TTL tracking.
/// Suitable for very large payloads and shared filesystem (NFS/SMB) scenarios.
/// </summary>
public sealed class FileClaimCheckRepository : IClaimCheckRepository
{
    private const string DataExtension = ".claim";
    private const string MetaExtension = ".meta";

    private readonly string _directory;
    private readonly TimeSpan _defaultTtl;

    /// <summary>
    /// Creates a new file-based claim check repository.
    /// </summary>
    /// <param name="directory">Directory to store claim files in. Created if it does not exist.</param>
    /// <param name="defaultTtl">Default TTL for entries. Zero or null means no expiry.</param>
    public FileClaimCheckRepository(string directory, TimeSpan? defaultTtl = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _defaultTtl = defaultTtl ?? TimeSpan.Zero;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public async Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = Guid.NewGuid().ToString("N");
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> Retrieve(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var dataPath = GetDataPath(key);
        if (!System.IO.File.Exists(dataPath))
            return null;

        if (IsExpired(key))
        {
            DeleteFiles(key);
            return null;
        }

        return await System.IO.File.ReadAllBytesAsync(dataPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var dataPath = GetDataPath(key);
        if (!System.IO.File.Exists(dataPath))
            return null;

        if (IsExpired(key))
        {
            DeleteFiles(key);
            return null;
        }

        var data = await System.IO.File.ReadAllBytesAsync(dataPath, ct).ConfigureAwait(false);
        DeleteFiles(key);
        return data;
    }

    /// <inheritdoc />
    public Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        DeleteFiles(key);
        return Task.CompletedTask;
    }

    private async Task StoreInternal(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl, CancellationToken ct)
    {
        var dataPath = GetDataPath(key);
        var metaPath = GetMetaPath(key);

        await System.IO.File.WriteAllBytesAsync(dataPath, data.ToArray(), ct).ConfigureAwait(false);

        var effectiveTtl = ttl ?? _defaultTtl;
        var meta = new ClaimMetadata
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = effectiveTtl > TimeSpan.Zero
                ? DateTimeOffset.UtcNow + effectiveTtl
                : null
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(meta);
        await System.IO.File.WriteAllBytesAsync(metaPath, json, ct).ConfigureAwait(false);
    }

    private bool IsExpired(string key)
    {
        var metaPath = GetMetaPath(key);
        if (!System.IO.File.Exists(metaPath))
            return false;

        try
        {
            var json = System.IO.File.ReadAllBytes(metaPath);
            var meta = JsonSerializer.Deserialize<ClaimMetadata>(json);
            return meta?.ExpiresAt.HasValue == true && DateTimeOffset.UtcNow >= meta.ExpiresAt.Value;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteFiles(string key)
    {
        TryDelete(GetDataPath(key));
        TryDelete(GetMetaPath(key));
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); }
        catch (IOException) { /* best effort */ }
    }

    private string GetDataPath(string key) => GetSafePath(key, DataExtension);
    private string GetMetaPath(string key) => GetSafePath(key, MetaExtension);

    private string GetSafePath(string key, string extension)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_directory, key + extension));
        var normalizedDir = Path.GetFullPath(_directory);
        if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Key '{key}' resolves outside the claim check directory.", nameof(key));
        return fullPath;
    }

    private sealed class ClaimMetadata
    {
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
