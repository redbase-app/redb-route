using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Mode for <see cref="ReplayProvider"/>.
/// </summary>
public enum ReplayMode
{
    /// <summary>Strictly replay from fixture; missing entries throw.</summary>
    Replay,

    /// <summary>Record every request/response from the inner provider into the fixture.</summary>
    Record,

    /// <summary>
    /// Replay when a fixture entry exists; otherwise call the inner provider
    /// and append the response. Useful for filling out a fixture incrementally.
    /// </summary>
    Auto
}

/// <summary>
/// Deterministic <see cref="ILlmProvider"/> backed by a JSON fixture on disk.
/// In <see cref="ReplayMode.Replay"/> the provider replays previously recorded
/// responses keyed by a stable hash of the request — no network IO. In
/// <see cref="ReplayMode.Record"/> it calls an inner provider and persists
/// every response. <see cref="ReplayMode.Auto"/> mixes the two, falling
/// through to the inner provider for cache misses and appending the result.
/// <para>
/// Designed for integration tests, eval-harness fixtures and offline demos —
/// keeps reference runs deterministic and free of API spend while still being
/// upgradable by re-recording against a real provider.
/// </para>
/// </summary>
public sealed class ReplayProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ReplayEntry> _entries;
    private readonly string _fixturePath;
    private readonly ReplayMode _mode;
    private readonly ILlmProvider? _inner;

    /// <summary>Creates a replay provider bound to a fixture file.</summary>
    /// <param name="fixturePath">Path to the JSON fixture (created if missing in Record/Auto).</param>
    /// <param name="mode">Replay/record behaviour.</param>
    /// <param name="inner">Inner provider — required for Record / Auto, ignored in Replay.</param>
    /// <param name="providerId">Identifier surfaced as <see cref="ProviderId"/> (defaults to "replay").</param>
    /// <param name="modelId">Identifier surfaced as <see cref="ModelId"/> (defaults to "replay-model").</param>
    public ReplayProvider(string fixturePath, ReplayMode mode, ILlmProvider? inner = null,
        string providerId = "replay", string modelId = "replay-model")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        if (mode is ReplayMode.Record or ReplayMode.Auto && inner is null)
            throw new ArgumentNullException(nameof(inner),
                "Inner provider is required when ReplayProvider is in Record or Auto mode.");

        _fixturePath = fixturePath;
        _mode = mode;
        _inner = inner;
        ProviderId = providerId;
        ModelId = modelId;
        _entries = LoadFixture(fixturePath);
    }

    /// <inheritdoc />
    public string ProviderId { get; }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <summary>Number of entries currently held in the in-memory fixture.</summary>
    public int EntryCount { get { lock (_gate) return _entries.Count; } }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = ComputeKey(request);

        if (_mode != ReplayMode.Record)
        {
            ReplayEntry? entry;
            lock (_gate) _entries.TryGetValue(key, out entry);
            if (entry is not null)
                return MaterialiseResponse(entry);
            if (_mode == ReplayMode.Replay)
                throw new InvalidOperationException(
                    $"ReplayProvider: no fixture entry for request key '{key}' in '{_fixturePath}'. " +
                    $"Re-record with ReplayMode.Auto / Record or update the fixture.");
        }

        // Record / Auto-miss path
        var response = await _inner!.CompleteAsync(request, ct).ConfigureAwait(false);
        AppendEntry(key, request, response);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For determinism the recorded shape is always the final non-streaming response.
        // Streaming consumers receive it as a single terminal chunk — matches the
        // default fallback in ILlmProvider.StreamAsync.
        var response = await CompleteAsync(request, ct).ConfigureAwait(false);
        yield return new LlmStreamChunk(response.Content, response.StopReason, response.Usage);
    }

    /// <summary>Manually persists the in-memory fixture to disk. Called automatically on append.</summary>
    public void Flush()
    {
        lock (_gate) SaveFixtureLocked();
    }

    private void AppendEntry(string key, LlmRequest request, LlmResponse response)
    {
        var entry = new ReplayEntry
        {
            Key = key,
            Request = SnapshotRequest(request),
            Response = SnapshotResponse(response)
        };
        lock (_gate)
        {
            _entries[key] = entry;
            SaveFixtureLocked();
        }
    }

    private void SaveFixtureLocked()
    {
        var dir = Path.GetDirectoryName(_fixturePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var doc = new FixtureDocument
        {
            ProviderId = ProviderId,
            ModelId = ModelId,
            Entries = _entries.Values.ToList()
        };
        File.WriteAllText(_fixturePath, JsonSerializer.Serialize(doc, JsonOpts));
    }

    private static Dictionary<string, ReplayEntry> LoadFixture(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, ReplayEntry>();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, ReplayEntry>();

        var doc = JsonSerializer.Deserialize<FixtureDocument>(json, JsonOpts);
        var result = new Dictionary<string, ReplayEntry>();
        if (doc?.Entries is null) return result;
        foreach (var e in doc.Entries)
            if (!string.IsNullOrEmpty(e.Key)) result[e.Key] = e;
        return result;
    }

    private static string ComputeKey(LlmRequest request)
    {
        var snapshot = SnapshotRequest(request);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static RequestSnapshot SnapshotRequest(LlmRequest r) => new()
    {
        ModelId = r.ModelId,
        SystemPrompt = r.SystemPrompt,
        Temperature = r.Temperature,
        MaxTokens = r.MaxTokens,
        TopP = r.TopP,
        StopSequences = r.StopSequences,
        Tools = r.Tools.Select(c => new ToolSnapshot
        {
            Name = c.Name,
            Description = c.Description,
            InputSchema = c.InputSchema
        }).ToList(),
        Messages = r.Messages.Select(SnapshotMessage).ToList()
    };

    private static MessageSnapshot SnapshotMessage(LlmMessage m) => new()
    {
        Role = m.Role,
        Blocks = m.Content.Select(b => b switch
        {
            LlmTextBlock t => new BlockSnapshot { Type = "text", Text = t.Text },
            LlmToolUseBlock u => new BlockSnapshot
            {
                Type = "tool_use",
                ToolUseId = u.ToolUseId,
                Name = u.Name,
                InputJson = u.InputJson
            },
            LlmToolResultBlock r => new BlockSnapshot
            {
                Type = "tool_result",
                ToolUseId = r.ToolUseId,
                OutputJson = r.OutputJson,
                IsError = r.IsError
            },
            _ => new BlockSnapshot { Type = "unknown" }
        }).ToList()
    };

    private static ResponseSnapshot SnapshotResponse(LlmResponse r) => new()
    {
        StopReason = r.StopReason,
        RawStopReason = r.RawStopReason,
        Usage = new UsageSnapshot { InputTokens = r.Usage.InputTokens, OutputTokens = r.Usage.OutputTokens },
        Blocks = r.Content.Select(b => b switch
        {
            LlmTextBlock t => new BlockSnapshot { Type = "text", Text = t.Text },
            LlmToolUseBlock u => new BlockSnapshot
            {
                Type = "tool_use",
                ToolUseId = u.ToolUseId,
                Name = u.Name,
                InputJson = u.InputJson
            },
            LlmToolResultBlock tr => new BlockSnapshot
            {
                Type = "tool_result",
                ToolUseId = tr.ToolUseId,
                OutputJson = tr.OutputJson,
                IsError = tr.IsError
            },
            _ => new BlockSnapshot { Type = "unknown" }
        }).ToList()
    };

    private static LlmResponse MaterialiseResponse(ReplayEntry entry)
    {
        var snap = entry.Response ?? throw new InvalidOperationException(
            $"ReplayProvider: fixture entry '{entry.Key}' has no response snapshot.");

        var blocks = new List<LlmContentBlock>();
        foreach (var b in snap.Blocks ?? new List<BlockSnapshot>())
        {
            switch (b.Type)
            {
                case "text": blocks.Add(new LlmTextBlock(b.Text ?? string.Empty)); break;
                case "tool_use": blocks.Add(new LlmToolUseBlock(
                    b.ToolUseId ?? Guid.NewGuid().ToString("N"),
                    b.Name ?? string.Empty,
                    b.InputJson ?? "{}")); break;
                case "tool_result": blocks.Add(new LlmToolResultBlock(
                    b.ToolUseId ?? string.Empty,
                    b.OutputJson ?? "{}",
                    b.IsError)); break;
            }
        }
        if (blocks.Count == 0) blocks.Add(new LlmTextBlock(string.Empty));

        return new LlmResponse
        {
            Content = blocks,
            StopReason = snap.StopReason,
            Usage = new LlmUsage(snap.Usage?.InputTokens ?? 0, snap.Usage?.OutputTokens ?? 0),
            RawStopReason = snap.RawStopReason
        };
    }

    private sealed class FixtureDocument
    {
        public string? ProviderId { get; set; }
        public string? ModelId { get; set; }
        public List<ReplayEntry> Entries { get; set; } = new();
    }

    private sealed class ReplayEntry
    {
        public string Key { get; set; } = string.Empty;
        public RequestSnapshot? Request { get; set; }
        public ResponseSnapshot? Response { get; set; }
    }

    private sealed class RequestSnapshot
    {
        public string? ModelId { get; set; }
        public string? SystemPrompt { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public double? TopP { get; set; }
        public IReadOnlyList<string>? StopSequences { get; set; }
        public List<ToolSnapshot>? Tools { get; set; }
        public List<MessageSnapshot>? Messages { get; set; }
    }

    private sealed class ToolSnapshot
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? InputSchema { get; set; }
    }

    private sealed class MessageSnapshot
    {
        public string? Role { get; set; }
        public List<BlockSnapshot>? Blocks { get; set; }
    }

    private sealed class BlockSnapshot
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? ToolUseId { get; set; }
        public string? Name { get; set; }
        public string? InputJson { get; set; }
        public string? OutputJson { get; set; }
        public bool IsError { get; set; }
    }

    private sealed class ResponseSnapshot
    {
        public LlmStopReason StopReason { get; set; } = LlmStopReason.EndTurn;
        public string? RawStopReason { get; set; }
        public UsageSnapshot? Usage { get; set; }
        public List<BlockSnapshot>? Blocks { get; set; }
    }

    private sealed class UsageSnapshot
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
