using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Tests.Llm.TestHelpers;
using Xunit.Abstractions;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Phase 16.0b, on the interface that is actually reachable: DeepSeek serves the Anthropic Messages API
/// at <c>https://api.deepseek.com/anthropic</c> (<c>x-api-key</c> supported, <c>type="thinking"</c>
/// supported, <c>redacted_thinking</c> not), so <see cref="AnthropicProvider"/> can be exercised against
/// a live endpoint without an Anthropic key — which is the whole question 16.0b asks: does a thinking
/// block that came off the wire go back on it without a 400.
/// <para>
/// The run is recorded, not assumed. Both request bodies are captured as sent, and the artifact says
/// what came back (a block with a signature, a block without one, or none) — so a green run with no
/// returnable block is read as "the path works", never as "the replay was proven".
/// </para>
/// <para>
/// Nothing here prints the key or the thinking text: the artifact carries counts, lengths, field names
/// and the first 16 hex digits of the block's SHA-256.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class AnthropicCompatLiveTests
{
    private const string EnvVar = "REDB_LLM_DEEPSEEK";
    private const string BaseUrl = "https://api.deepseek.com/anthropic/";
    private const string Model = "deepseek-flash";
    private const string Artifact = "16-0b-anthropic-compat.json";

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the probe with xUnit's output sink.</summary>
    public AnthropicCompatLiveTests(ITestOutputHelper output) => _output = output;

    /// <summary>Forwards a request untouched and keeps the body and address the provider sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpClient _forward = new() { Timeout = TimeSpan.FromSeconds(120) };

        /// <summary>Serialised request bodies, in the order the provider sent them.</summary>
        public List<string> SentBodies { get; } = new();

        /// <summary>Request addresses, so the run proves which endpoint was spoken to.</summary>
        public List<string> SentUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            SentBodies.Add(body);
            SentUris.Add(request.RequestUri!.ToString());

            using var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return await _forward.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _forward.Dispose();
            base.Dispose(disposing);
        }
    }

    private static string Fingerprint(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    /// <summary>What came back on the thinking path — enough to compare two runs without reading thoughts.</summary>
    private sealed record ThinkingShape(bool Present, bool HasSignature, bool IsRedacted, int TextLength, string TextFingerprint)
    {
        public static ThinkingShape Of(LlmThinkingBlock? block) => block is null
            ? new ThinkingShape(false, false, false, 0, string.Empty)
            : new ThinkingShape(true, block.Signature is { Length: > 0 }, block.IsRedacted,
                block.Text.Length, Fingerprint(block.Text));
    }

    private const string Question = "Where is order 4711 right now?";

    private static readonly LlmToolCapability LookupTool = new()
    {
        Name = "order_lookup",
        Description = "Looks up one order by id and returns its shipping status.",
        InputSchema = """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}"""
    };

    private static JsonObject ParseBody(string body) => JsonNode.Parse(body)!.AsObject();

    /// <summary>A block the Anthropic wire can carry back: signed thinking, or a redacted payload.</summary>
    private static bool HasAnthropicForm(LlmThinkingBlock? block) =>
        block is not null && (block.IsRedacted || !string.IsNullOrEmpty(block.Signature));

    private static IEnumerable<JsonObject> BlocksOf(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) yield break;
        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
                if (block is JsonObject obj) yield return obj;
        }
    }

    private static int ThinkingBlocksIn(JsonObject body) => BlocksOf(body)
        .Count(b => b["type"]?.GetValue<string>() is "thinking" or "redacted_thinking");

    /// <summary>The signature (or redacted payload) of the first thinking block carried in a request.</summary>
    private static string? SignaturePlayed(JsonObject body) => BlocksOf(body)
        .FirstOrDefault(b => b["type"]?.GetValue<string>() is "thinking" or "redacted_thinking") is { } first
            ? first["signature"]?.GetValue<string>() ?? first["data"]?.GetValue<string>()
            : null;

    /// <summary>
    /// The 16.0b question on an endpoint that is reachable today: <see cref="AnthropicProvider"/> against
    /// DeepSeek's Anthropic-compatible interface, asked twice — once for an answer that may carry a thinking
    /// block, then with that block and the tool turn it belongs to handed back.
    /// </summary>
    [EnvFact(EnvVar)]
    public async Task DeepSeek_AnthropicPath_TakesTheThinkingBlockBack()
    {
        var started = DateTimeOffset.UtcNow;
        var factory = new LlmConnectionFactory
        {
            Name = "deepseek-anthropic-compat",
            Provider = "anthropic",
            ModelId = Model,
            ApiKey = Environment.GetEnvironmentVariable(EnvVar)!,
            BaseUrl = new Uri(BaseUrl),
            MaxTokens = 512
        };

        using var recorder = new RecordingHandler();
        var provider = new AnthropicProvider(factory, new HttpClient(recorder));

        var first = await provider.CompleteAsync(new LlmRequest
        {
            SystemPrompt = "Call the order_lookup tool before answering the question.",
            Messages = [LlmMessage.User(Question)],
            Tools = [LookupTool]
        });

        var thinking = first.Content.OfType<LlmThinkingBlock>().FirstOrDefault();
        var shape = ThinkingShape.Of(thinking);
        var toolUses = first.Content.OfType<LlmToolUseBlock>().ToList();

        // The transcript is rebuilt the way a run would: the block first, what the assistant said next,
        // then the tool result the tool_use asked for — the shape that 400s if either half is lost.
        var assistant = new List<LlmContentBlock>();
        if (HasAnthropicForm(thinking)) assistant.Add(thinking!);
        assistant.AddRange(first.Content.Where(b => b is not LlmThinkingBlock));

        var transcript = new List<LlmMessage> { LlmMessage.User(Question) };
        if (assistant.Count > 0)
            transcript.Add(new LlmMessage { Role = "assistant", Content = assistant });
        if (toolUses.Count > 0)
            transcript.Add(new LlmMessage
            {
                Role = "user",
                Content = toolUses
                    .Select(u => (LlmContentBlock)new LlmToolResultBlock(u.ToolUseId, """{"status":"in_transit"}"""))
                    .ToList()
            });

        var replay = await provider.CompleteAsync(new LlmRequest
        {
            Messages = transcript,
            Tools = [LookupTool]
        });

        var sent = recorder.SentBodies.Select(ParseBody).ToList();
        var carriedBack = ThinkingBlocksIn(sent[1]);
        var verdict = shape switch
        {
            { Present: false } => "no thinking block came back — the path holds, the replay is not proven",
            { IsRedacted: true } => "redacted block came back and was handed back verbatim",
            { HasSignature: true } => "signed block came back and was handed back verbatim",
            _ => "unsigned block came back — this connector does not replay it (it would be rejected)"
        };

        recorder.SentUris.Should().HaveCount(2);
        recorder.SentUris[0].Should().Be(BaseUrl + "v1/messages");
        first.Content.Should().NotBeEmpty();
        replay.Content.Should().NotBeEmpty();

        // Decision 15 of the plan: this connector never asks for thinking, so a block that comes back is
        // the model thinking on its own — asserted, because "we do not send it" is a promise a request
        // body can keep.
        sent[0].Should().NotContainKey("thinking", "the anthropic path must not send a top-level thinking field.");
        sent[1].Should().NotContainKey("thinking");

        if (HasAnthropicForm(thinking))
        {
            carriedBack.Should().Be(1);
            SignaturePlayed(sent[1]).Should().Be(thinking!.IsRedacted ? thinking.RedactedData : thinking.Signature);
        }
        else
        {
            // Nothing returnable came back, so nothing could be replayed — said out loud rather than
            // left for a reader to infer from a green run.
            carriedBack.Should().Be(0);
        }

        var artifact = new
        {
            Phase = "16.0b — the anthropic path against DeepSeek's Anthropic-compatible interface",
            Endpoint = recorder.SentUris[0],
            Model,
            StartedUtc = started,
            FinishedUtc = DateTimeOffset.UtcNow,
            FirstRequestHadThinkingField = sent[0].ContainsKey("thinking"),
            ThinkingOnTheWire = ThinkingBlocksIn(sent[0]),
            ThinkingParsed = shape,
            ToolCalls = toolUses.Count,
            BlocksHandedBack = carriedBack,
            ReplayedSignatureFingerprint = SignaturePlayed(sent[1]) is { Length: > 0 } sig ? Fingerprint(sig) : null,
            Verdict = verdict
        };

        var path = Path.Combine(AppContext.BaseDirectory, Artifact);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine($"artifact: {path}");
        _output.WriteLine($"thinking on the wire={artifact.ThinkingOnTheWire} parsed={shape.Present} " +
                          $"signed={shape.HasSignature} redacted={shape.IsRedacted} " +
                          $"textLen={shape.TextLength} textSha256={shape.TextFingerprint}");
        _output.WriteLine($"tool calls={toolUses.Count} blocks handed back={carriedBack}");
        _output.WriteLine(verdict);
    }
}
