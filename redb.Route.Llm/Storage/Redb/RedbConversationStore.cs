using System.Collections.Concurrent;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

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
/// <para>
/// The <see cref="IRedbService"/> is resolved per call through
/// <see cref="RedbRouteExtensions.GetRedbService(IRouteContext, string, IExchange?)"/>:
/// when an <see cref="IExchange"/> is supplied (the engine forwards the route
/// pipeline's exchange) the route framework hands out a per-exchange scoped
/// instance and disposes it with the exchange. The named instance —
/// <c>?redb=my-llm-db</c> on the URI — is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c>; missing or null falls back to
/// the default <c>IRedbService</c> registered in the context (the host's
/// instance from <c>services.AddRedb()</c>).
/// </para>
/// </summary>
public sealed class RedbConversationStore : IConversationStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;
    private readonly ConcurrentDictionary<string, CachedRoot> _rootIds = new(StringComparer.Ordinal);
    private static readonly TimeSpan RootCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates a new store backed by the route context's redb instance(s).
    /// </summary>
    /// <param name="context">Route context that knows how to resolve named/default <see cref="IRedbService"/>.</param>
    /// <param name="defaultRedbName">
    /// Optional default name. When the runtime exchange does not carry
    /// <see cref="LlmKeys.RedbName"/> in its properties this name is used; when
    /// it is null/empty the default unnamed instance is used.
    /// </param>
    public RedbConversationStore(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    private readonly record struct CachedRoot(long Id, DateTimeOffset ExpiresAt);

    private IRedbService Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
        {
            name = s;
        }
        return _context.GetRedbService(name ?? string.Empty, exchange);
    }

    /// <inheritdoc />
    public async Task<string> AppendAsync(
        string conversationId,
        string? parentMessageId,
        LlmMessage message,
        ConversationMessageMeta meta,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

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
                Temperature = meta.Temperature,
                MaxTokens = meta.MaxTokens,
                TopP = meta.TopP,
                PromptTemplateName = meta.PromptTemplateName,
                PromptTemplateVersion = meta.PromptTemplateVersion,
                ToolSetHash = meta.ToolSetHash,
                ProviderSystemFingerprint = meta.ProviderSystemFingerprint,
                UserId = meta.UserId,
                AuditTags = meta.AuditTags is { Count: > 0 }
                    ? new Dictionary<string, string>(meta.AuditTags, StringComparer.Ordinal)
                    : null,
                Content = ToStoredBlocks(message.Content)
            }
        };

        await redb.CreateChildAsync(child, parentObj).ConfigureAwait(false);

        rootObj.Props.LastActivityAtUtc = meta.CreatedAtUtc;
        rootObj.Props.TotalInputTokens += meta.Usage.InputTokens;
        rootObj.Props.TotalOutputTokens += meta.Usage.OutputTokens;
        // Framework does not auto-stamp date_modify; mirror LastActivityAtUtc.
        rootObj.date_modify = new DateTimeOffset(
            DateTime.SpecifyKind(meta.CreatedAtUtc, DateTimeKind.Utc));
        await redb.SaveAsync(rootObj).ConfigureAwait(false);

        return newId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationMessage>> LoadPathAsync(
        string conversationId, string? leafId = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

        var rootId = await GetOrCreateRootIdAsync(redb, conversationId, ct).ConfigureAwait(false);

        TreeRedbObject<MessageProps>? leafObj;
        if (leafId is not null)
        {
            leafObj = await FindMessageByValueStringAsync(redb, leafId).ConfigureAwait(false);
            if (leafObj is null) return [];
        }
        else
        {
            // Server-side latest-leaf detection: WhereLeaves filters the
            // descendant subtree to nodes without children at the SQL level,
            // then ORDER BY _objects.date_create DESC LIMIT 1 picks the
            // freshest. Avoids hauling the whole conversation transcript just
            // to compute a parent-id set in C#.
            var rootObj = await redb.LoadAsync<ConversationProps>(rootId).ConfigureAwait(false);
            if (rootObj is null) return [];

            var latestLeaf = await redb.TreeQuery<MessageProps>(rootObj)
                .WhereLeaves()
                .OrderByDescendingRedb(o => o.DateCreate)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            if (latestLeaf is null) return [];

            // The path walker needs a TreeRedbObject; reload the leaf as a
            // single-node tree to satisfy GetPathToRootAsync.
            leafObj = await redb.LoadTreeAsync<MessageProps>(latestLeaf.id, maxDepth: 0).ConfigureAwait(false);
            if (leafObj is null) return [];
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
        string conversationId, IExchange? exchange = null, CancellationToken ct = default)
        => await LoadAllMessagesAsync(conversationId, exchange).ConfigureAwait(false);

    private async Task<List<ConversationMessage>> LoadAllMessagesAsync(string conversationId, IExchange? exchange)
    {
        var redb = Resolve(exchange);

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
                ToolUseId = row.Props.ToolUseId,
                Temperature = row.Props.Temperature,
                MaxTokens = row.Props.MaxTokens,
                TopP = row.Props.TopP,
                PromptTemplateName = row.Props.PromptTemplateName,
                PromptTemplateVersion = row.Props.PromptTemplateVersion,
                ToolSetHash = row.Props.ToolSetHash,
                ProviderSystemFingerprint = row.Props.ProviderSystemFingerprint,
                UserId = row.Props.UserId,
                AuditTags = row.Props.AuditTags is { Count: > 0 }
                    ? new Dictionary<string, string>(row.Props.AuditTags, StringComparer.Ordinal)
                    : null
            }
        };
    }

    private async Task<long> GetOrCreateRootIdAsync(IRedbService redb, string conversationId, CancellationToken ct)
    {
        // TTL-bound cache: stale entries (e.g. someone purged the conversation
        // out-of-band) self-heal after RootCacheTtl instead of pinning a dead
        // id for the whole process lifetime.
        var now = DateTimeOffset.UtcNow;
        if (_rootIds.TryGetValue(conversationId, out var cached) && cached.ExpiresAt > now)
            return cached.Id;

        var hit = await redb.Query<ConversationProps>()
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (hit is not null)
        {
            _rootIds[conversationId] = new CachedRoot(hit.id, now + RootCacheTtl);
            return hit.id;
        }

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
        _rootIds[conversationId] = new CachedRoot(id, now + RootCacheTtl);
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
