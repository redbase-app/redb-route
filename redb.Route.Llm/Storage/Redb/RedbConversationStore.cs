using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IConversationStore"/>. Tree integrity is enforced by
/// <c>CreateChildAsync</c>; transcript reads use redb's tree primitives
/// (<c>TreeQuery&lt;T&gt;(root)</c> for descendants, <c>GetPathToRootAsync</c>
/// for breadcrumbs — root → leaf order) — server-side tree traversal, not
/// client-side rebuilding. Latest-leaf detection loads the conversation's
/// descendants once and selects the most recent message that is not a parent.
/// Per-row business identifiers live on indexed <c>_objects</c> columns —
/// <c>value_string</c> for the conversation/message id and <c>value_long</c>
/// on each message for the conversation FK (== root <c>_objects.id</c>).
/// Content blocks are stored as a typed nested array, no JSON marshalling
/// for our own schema.
/// </summary>
public sealed class RedbConversationStore : IConversationStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, long> _rootIds = new(StringComparer.Ordinal);
    private bool _schemesEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>Creates a new store. Schemes are synced lazily on first call.</summary>
    public RedbConversationStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task<string> AppendAsync(
        string conversationId,
        string? parentMessageId,
        LlmMessage message,
        ConversationMessageMeta meta,
        CancellationToken ct = default)
    {
        await EnsureSchemesAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var rootId = await GetOrCreateRootIdAsync(redb, conversationId, ct).ConfigureAwait(false);
        var rootObj = await redb.LoadAsync<ConversationProps>(rootId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation root {conversationId} disappeared between lookup and load.");

        IRedbObject parentObj = rootObj;
        if (parentMessageId is not null)
        {
            var parentMsg = await FindMessageByValueStringAsync(redb, parentMessageId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Parent message {parentMessageId} not found in conversation {conversationId}.");
            parentObj = parentMsg;
        }

        var newId = Guid.NewGuid().ToString("N");
        var child = new TreeRedbObject<MessageProps>
        {
            value_string = newId,
            value_long = rootId,
            Props = new MessageProps
            {
                Role = message.Role,
                Iteration = meta.Iteration,
                CreatedAtUtc = meta.CreatedAtUtc,
                ProviderId = meta.ProviderId,
                ModelId = meta.ModelId,
                StopReason = meta.StopReason?.ToString(),
                ToolUseId = meta.ToolUseId,
                InputTokens = meta.Usage.InputTokens,
                OutputTokens = meta.Usage.OutputTokens,
                Content = ToStoredBlocks(message.Content)
            }
        };

        await redb.CreateChildAsync(child, parentObj).ConfigureAwait(false);

        rootObj.Props.LastActivityAtUtc = meta.CreatedAtUtc;
        rootObj.Props.TotalInputTokens += meta.Usage.InputTokens;
        rootObj.Props.TotalOutputTokens += meta.Usage.OutputTokens;
        await redb.SaveAsync(rootObj).ConfigureAwait(false);

        return newId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationMessage>> LoadPathAsync(
        string conversationId, string? leafId = null, CancellationToken ct = default)
    {
        await EnsureSchemesAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var rootId = await GetOrCreateRootIdAsync(redb, conversationId, ct).ConfigureAwait(false);

        TreeRedbObject<MessageProps>? leafObj;
        if (leafId is not null)
        {
            leafObj = await FindMessageByValueStringAsync(redb, leafId).ConfigureAwait(false);
            if (leafObj is null) return [];
        }
        else
        {
            // Latest leaf in the conversation: load all descendants, find ids
            // that aren't parents of any other descendant, pick the most recent.
            var rootObj = await redb.LoadAsync<ConversationProps>(rootId).ConfigureAwait(false);
            if (rootObj is null) return [];

            var rows = await redb.TreeQuery<MessageProps>(rootObj)
                .ToFlatListAsync()
                .ConfigureAwait(false);
            if (rows.Count == 0) return [];

            var parentIds = new HashSet<long>(rows.Where(r => r.parent_id is not null).Select(r => r.parent_id!.Value));
            var leaves = rows.Where(r => !parentIds.Contains(r.id)).ToList();
            if (leaves.Count == 0) return [];

            leafObj = leaves.OrderByDescending(r => r.Props.CreatedAtUtc).First();
        }

        // Server-side ancestor walk; returns root → leaf order. The conversation
        // root is a ConversationProps node — GetPathToRootAsync<MessageProps>
        // still returns it (cast to MessageProps with default Props), so trim by id.
        var ancestors = (await redb.GetPathToRootAsync<MessageProps>(leafObj).ConfigureAwait(false)).ToList();

        var path = ancestors
            .Where(a => a.id != rootId)
            .Select(row => Materialize(row, conversationId, rootId, parentLookup: null))
            .ToList();

        return path;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationMessage>> LoadTreeAsync(
        string conversationId, CancellationToken ct = default)
        => await LoadAllMessagesAsync(conversationId).ConfigureAwait(false);

    private async Task<List<ConversationMessage>> LoadAllMessagesAsync(string conversationId)
    {
        await EnsureSchemesAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var rootId = await GetOrCreateRootIdAsync(redb, conversationId, CancellationToken.None).ConfigureAwait(false);
        var rootObj = await redb.LoadAsync<ConversationProps>(rootId).ConfigureAwait(false);
        if (rootObj is null) return [];

        // Server-side tree traversal: every MessageProps descendant of this
        // conversation root, regardless of nesting depth. ToFlatListAsync
        // skips Parent/Children pointer building — we rebuild parent linkage
        // manually below via the idToValueString lookup.
        var rows = await redb.TreeQuery<MessageProps>(rootObj)
            .ToFlatListAsync()
            .ConfigureAwait(false);

        // Map redb id → business id (value_string) so child rows can resolve
        // their parent's business id from the tree's native parent_id.
        var idToValueString = rows.ToDictionary(r => r.id, r => r.value_string);

        var list = new List<ConversationMessage>(rows.Count);
        foreach (var row in rows)
            list.Add(Materialize(row, conversationId, rootId, idToValueString));
        return list;
    }

    private static ConversationMessage Materialize(
        TreeRedbObject<MessageProps> row,
        string conversationId,
        long rootId,
        IReadOnlyDictionary<long, string?>? parentLookup)
    {
        string? parentValueString = null;
        if (row.parent_id is { } pid && pid != rootId && parentLookup is not null
            && parentLookup.TryGetValue(pid, out var pvs))
        {
            parentValueString = pvs;
        }

        return new ConversationMessage
        {
            Id = row.value_string ?? row.id.ToString(),
            ParentId = parentValueString,
            ConversationId = conversationId,
            Message = new LlmMessage
            {
                Role = row.Props.Role,
                Content = FromStoredBlocks(row.Props.Content)
            },
            Meta = new ConversationMessageMeta
            {
                CreatedAtUtc = row.Props.CreatedAtUtc.UtcDateTime,
                Iteration = row.Props.Iteration,
                ProviderId = row.Props.ProviderId,
                ModelId = row.Props.ModelId,
                StopReason = ParseStopReason(row.Props.StopReason),
                Usage = new LlmUsage(row.Props.InputTokens, row.Props.OutputTokens),
                ToolUseId = row.Props.ToolUseId
            }
        };
    }

    private async Task<long> GetOrCreateRootIdAsync(IRedbService redb, string conversationId, CancellationToken ct)
    {
        if (_rootIds.TryGetValue(conversationId, out var cached)) return cached;

        var hit = await redb.Query<ConversationProps>()
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (hit is not null)
        {
            _rootIds[conversationId] = hit.id;
            return hit.id;
        }

        var now = DateTimeOffset.UtcNow;
        var root = new RedbObject<ConversationProps>
        {
            value_string = conversationId,
            Props = new ConversationProps
            {
                TenantId = string.Empty,
                Status = "active",
                StartedAtUtc = now,
                LastActivityAtUtc = now
            }
        };
        var id = await redb.SaveAsync(root).ConfigureAwait(false);
        _rootIds[conversationId] = id;
        return id;
    }

    private static async Task<TreeRedbObject<MessageProps>?> FindMessageByValueStringAsync(IRedbService redb, string messageId)
    {
        var hit = await redb.Query<MessageProps>()
            .WhereRedb(x => x.ValueString == messageId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return hit is null ? null : await redb.LoadTreeAsync<MessageProps>(hit.id, maxDepth: 0).ConfigureAwait(false);
    }

    private async Task EnsureSchemesAsync()
    {
        if (_schemesEnsured) return;
        await _ensureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemesEnsured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<ConversationProps>().ConfigureAwait(false);
            await redb.SyncSchemeAsync<MessageProps>().ConfigureAwait(false);
            _schemesEnsured = true;
        }
        finally { _ensureLock.Release(); }
    }

    private static MessageContentBlock[] ToStoredBlocks(IReadOnlyList<LlmContentBlock> blocks)
    {
        if (blocks.Count == 0) return [];
        var arr = new MessageContentBlock[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            arr[i] = blocks[i] switch
            {
                LlmTextBlock t => new MessageContentBlock { Kind = "text", Text = t.Text },
                LlmToolUseBlock u => new MessageContentBlock
                {
                    Kind = "tool_use",
                    ToolUseId = u.ToolUseId,
                    ToolName = u.Name,
                    InputJson = u.InputJson
                },
                LlmToolResultBlock r => new MessageContentBlock
                {
                    Kind = "tool_result",
                    ToolUseId = r.ToolUseId,
                    OutputJson = r.OutputJson,
                    IsError = r.IsError
                },
                _ => new MessageContentBlock { Kind = "text", Text = blocks[i].ToString() ?? string.Empty }
            };
        }
        return arr;
    }

    private static IReadOnlyList<LlmContentBlock> FromStoredBlocks(MessageContentBlock[]? stored)
    {
        if (stored is null || stored.Length == 0) return [];
        var list = new List<LlmContentBlock>(stored.Length);
        foreach (var b in stored)
        {
            list.Add(b.Kind switch
            {
                "text" => new LlmTextBlock(b.Text ?? string.Empty),
                "tool_use" => new LlmToolUseBlock(b.ToolUseId ?? string.Empty, b.ToolName ?? string.Empty, b.InputJson ?? "{}"),
                "tool_result" => new LlmToolResultBlock(b.ToolUseId ?? string.Empty, b.OutputJson ?? string.Empty, b.IsError),
                _ => new LlmTextBlock(b.Text ?? string.Empty)
            });
        }
        return list;
    }

    private static LlmStopReason? ParseStopReason(string? raw) =>
        Enum.TryParse<LlmStopReason>(raw, out var parsed) ? parsed : null;
}
