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
/// client-side rebuilding.
/// Per-row business identifiers live on indexed <c>_objects</c> columns —
/// <c>value_string</c> for the conversation/message id and <c>value_long</c>
/// on each message for the conversation FK (== root <c>_objects.id</c>).
/// <para>
/// <b>Conversation isolation is enforced by the <c>value_long</c> FK, not by tree
/// shape.</b> Head detection and message lookup are scoped by it explicitly, so a
/// read can never surface — and a write can never attach under — another
/// conversation's node, independent of how a provider evaluates tree filters.
/// </para>
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

    /// <summary>
    /// Resolves the redb instance for this call and returns the name it was
    /// resolved under. The name is part of the root-id cache key: the instance is
    /// chosen per exchange (<c>?redb=</c> → <c>LlmKeys.RedbName</c>), so a cache
    /// keyed by conversation id alone would hand back an id resolved in database A
    /// while the call runs against database B.
    /// </summary>
    private (IRedbService Redb, string Name) Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
        {
            name = s;
        }
        var effective = name ?? string.Empty;
        return (_context.GetRedbService(effective, exchange), effective);
    }

    // NUL separator: instance name and conversation id are both opaque caller
    // strings, so any printable separator could be forged into a collision
    // ("a" + "b:c" vs "a:b" + "c").
    private const char CacheKeySeparator = (char)0;

    private static string CacheKey(string redbName, string conversationId)
        => string.Concat(redbName, CacheKeySeparator, conversationId);

    /// <inheritdoc />
    public async Task<string> AppendAsync(
        string conversationId,
        string? parentMessageId,
        LlmMessage message,
        ConversationMessageMeta meta,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        var (redb, redbName) = Resolve(exchange);

        var rootId = await GetOrCreateRootIdAsync(redb, redbName, conversationId, ct).ConfigureAwait(false);
        var rootObj = await redb.LoadAsync<ConversationProps>(rootId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation root {conversationId} disappeared between lookup and load.");

        IRedbObject parentObj = rootObj;
        if (parentMessageId is not null)
        {
            // Scoped by rootId on purpose: the lookup is by an opaque caller-supplied
            // id over the whole schema, so an unscoped resolve would happily attach
            // this message under another conversation's node — a cross-conversation
            // write that no later read can undo.
            var parentMsg = await FindMessageByValueStringAsync(redb, parentMessageId, rootId).ConfigureAwait(false)
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
                FactoryName = meta.FactoryName,
                BaseUrl = meta.BaseUrl,
                ProviderResponseId = meta.ProviderResponseId,
                LatencyMs = meta.LatencyMs,
                ApiKeyFingerprint = meta.ApiKeyFingerprint,
                RetryCount = meta.RetryCount,
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
        var (redb, redbName) = Resolve(exchange);

        var rootId = await GetOrCreateRootIdAsync(redb, redbName, conversationId, ct).ConfigureAwait(false);

        TreeRedbObject<MessageProps>? leafObj;
        if (leafId is not null)
        {
            leafObj = await FindMessageByValueStringAsync(redb, leafId, rootId).ConfigureAwait(false);
            if (leafObj is null) return [];
        }
        else
        {
            // Head detection scoped by the conversation FK, not by tree traversal.
            //
            // The newest message of a conversation is always a leaf — anything that
            // became a parent has a child created after it — so "newest by
            // date_create within this conversation" is exactly "the freshest leaf",
            // without depending on how a provider implements WhereLeaves(). That
            // matters: a rooted TreeQuery(root).WhereLeaves() used to be evaluated
            // as "freshest leaf of the whole schema" (fixed for Pro in d88ff9fe,
            // still open for the Free PVT routing), which returned another
            // conversation's transcript and then attached the next message under it.
            //
            // value_long carries the root's _objects.id and lives on the indexed
            // _objects row, so this stays a single-row server-side lookup.
            var head = await redb.Query<MessageProps>()
                .WhereRedb(o => o.ValueLong == rootId)
                .OrderByDescendingRedb(o => o.DateCreate)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            if (head is null) return [];

            // The path walker needs a TreeRedbObject; reload the leaf as a
            // single-node tree to satisfy GetPathToRootAsync.
            leafObj = await redb.LoadTreeAsync<MessageProps>(head.id, maxDepth: 0).ConfigureAwait(false);
            if (leafObj is null) return [];
        }

        // Server-side ancestor walk; returns root → leaf order. The conversation
        // root is a ConversationProps node — GetPathToRootAsync<MessageProps>
        // still returns it (cast to MessageProps with default Props), so trim by id.
        // The Role guard is belt-and-braces for that cast: a role-less row is not a
        // message, and letting one through means posting an empty role to the
        // provider (a 400 that looks like a model problem, not a storage one).
        var ancestors = (await redb.GetPathToRootAsync<MessageProps>(leafObj).ConfigureAwait(false)).ToList();

        var path = ancestors
            .Where(a => a.id != rootId && !string.IsNullOrEmpty(a.Props.Role))
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
        var (redb, redbName) = Resolve(exchange);

        var rootId = await GetOrCreateRootIdAsync(redb, redbName, conversationId, CancellationToken.None).ConfigureAwait(false);
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
                    : null,
                FactoryName = row.Props.FactoryName,
                BaseUrl = row.Props.BaseUrl,
                ProviderResponseId = row.Props.ProviderResponseId,
                LatencyMs = row.Props.LatencyMs,
                ApiKeyFingerprint = row.Props.ApiKeyFingerprint,
                RetryCount = row.Props.RetryCount
            }
        };
    }

    private async Task<long> GetOrCreateRootIdAsync(
        IRedbService redb, string redbName, string conversationId, CancellationToken ct)
    {
        // TTL-bound cache: stale entries (e.g. someone purged the conversation
        // out-of-band) self-heal after RootCacheTtl instead of pinning a dead
        // id for the whole process lifetime. Keyed by (instance, conversation) —
        // the same conversation id in two named databases resolves to two
        // different root ids, and a conversation-only key would cross them.
        var key = CacheKey(redbName, conversationId);
        var now = DateTimeOffset.UtcNow;
        if (_rootIds.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Id;

        var hit = await redb.Query<ConversationProps>()
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (hit is not null)
        {
            _rootIds[key] = new CachedRoot(hit.id, now + RootCacheTtl);
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
        _rootIds[key] = new CachedRoot(id, now + RootCacheTtl);
        return id;
    }

    /// <summary>
    /// Resolves a message by its business id <b>within one conversation</b>.
    /// <paramref name="rootId"/> is not an optimisation — the id is opaque and
    /// caller-supplied, so an unscoped lookup lets a caller reach (and, through
    /// <c>AppendAsync</c>, write under) another conversation's node. Returns null
    /// when the message does not exist or belongs to a different conversation;
    /// the two cases are deliberately indistinguishable to the caller.
    /// </summary>
    private static async Task<TreeRedbObject<MessageProps>?> FindMessageByValueStringAsync(
        IRedbService redb, string messageId, long rootId)
    {
        var hit = await redb.Query<MessageProps>()
            .WhereRedb(x => x.ValueString == messageId && x.ValueLong == rootId)
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
