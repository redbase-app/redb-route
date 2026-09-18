using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using redb.Route.Llm.Providers;
using redb.Route.Tests.Llm.TestHelpers;
using Xunit.Abstractions;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Phase 16.0a — the live half of the provider matrix: what the wire actually carries for the model's
/// thinking block, and what happens when that block is missing from a tool turn.
/// <para>
/// The probes speak <b>raw HTTP</b> instead of going through <see cref="OpenAiProvider"/> on purpose:
/// until 16.1 the connector neither returns a thinking block nor can be asked to send one back, so a
/// connector test could not report the defect — it would only report the connector. The provider is the
/// evidence here, and the probes are the smallest shape that asks it the questions 16.0 exists for:
/// what comes back, what must be sent back, what happens when the block is dropped, and whether a
/// conversation whose tool turns never carried one can still be continued.
/// </para>
/// <para>
/// Nothing here prints a secret or the thinking text. The artifact records field names, lengths, the
/// first 16 hex digits of the block's SHA-256 (two runs can be compared without reading thoughts) and
/// the provider's error text. Findings land in <c>16-0a-probe.json</c> next to the test binary and are
/// transcribed into <c>docs/V4/llm-THINKING/PROVIDER-MATRIX.md</c> by hand.
/// </para>
/// <para>
/// A probe whose history already carries the tool result expects an answer; only the opening turns
/// expect a call. The verdicts are read that way, and the artifact records both the shapes sent and
/// what came back, so a reader can disagree with the reading.
/// </para>
/// <para>
/// <c>tool_choice</c> is deliberately absent from the tool probes: the first live run of this wave
/// (2026-09-14) showed thinking mode answering <c>400 invalid_request_error: Thinking mode does not
/// support this tool_choice</c> for <c>"required"</c>, which would have masked every other finding. The
/// system prompt asks for the tool instead, and one probe keeps the pinned form on purpose so the
/// constraint stays proven rather than remembered.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class ThinkingProbeLiveTests
{
    private const string EnvVar = "REDB_LLM_DEEPSEEK";
    private const string Provider = "deepseek";
    private const string ToolName = "order_lookup";
    private const int MaxTokens = 512;

    /// <summary>
    /// Aliases DeepSeek documents but no longer lists. Probed on their own, because "documented" and
    /// "still routed" stopped being the same thing: on 2026-09-14 <c>GET /models</c> offers
    /// <c>deepseek-flash</c> and <c>deepseek-v4-pro</c> and nothing else.
    /// </summary>
    private static readonly string[] LegacyAliases = ["deepseek-reasoner", "deepseek-chat"];

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the probe with xUnit's output sink.</summary>
    public ThinkingProbeLiveTests(ITestOutputHelper output) => _output = output;

    [EnvFact(EnvVar)]
    public async Task DeepSeek_Probes_RecordWhatTheThinkingPathDoes()
    {
        var key = Environment.GetEnvironmentVariable(EnvVar)!;
        var baseUrl = OpenAiProvider.ResolveDefaultBaseUrl(Provider);
        var report = new ProbeReport
        {
            Provider = Provider,
            Endpoint = new Uri(baseUrl, "chat/completions").ToString(),
            StartedUtc = DateTimeOffset.UtcNow
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            var available = await ListModelsAsync(http, baseUrl, report);

            // Whatever the endpoint lists is what gets probed: "ask /models first" is step zero of 16.0,
            // and the reason this wave discovered that the models its plan named no longer exist.
            foreach (var model in available.Where(IsChatModel))
            {
                var withBlock = await ProbeWithBlockAsync(http, baseUrl, model, report);
                await ProbeWithBlockReplayAsync(http, baseUrl, model, withBlock, report);
                await ProbeWithoutBlockAsync(http, baseUrl, model, withBlock, report, withTools: true);
                await ProbeWithoutBlockWithContentAsync(http, baseUrl, model, withBlock, report);
                await ProbeWithoutBlockAsync(http, baseUrl, model, withBlock, report, withTools: false);
                await ProbeOldHistoryAsync(http, baseUrl, model, report);
                await ProbeToolChoiceAsync(http, baseUrl, model, report);
            }

            // Aliases the endpoint no longer lists get one question: does the name still route at all?
            foreach (var alias in LegacyAliases.Where(a => !available.Any(id => id.Equals(a, StringComparison.OrdinalIgnoreCase))))
                await SendAsync(http, baseUrl, ProbeId.AliasWithBlock,
                    ChatBody(alias, new JsonArray(SystemPrompt(), UserQuestion()), withTools: true),
                    expectToolCall: true, report);
        }
        finally
        {
            report.FinishedUtc = DateTimeOffset.UtcNow;
            WriteArtifact(report);
        }

        _output.WriteLine($"16.0a artifact: {ArtifactPath}");

        report.ModelListStatus.Should().Be(200, "GET /models is the cheapest proof that the key is live");
        report.ModelsAvailable.Should().NotBeEmpty("the endpoint must list at least one model to probe");
        report.Probes.Should().NotBeEmpty();
        report.Probes.Where(p => p.Verdict != ProbeVerdict.NotApplicable)
            .Should().OnlyContain(p => p.Status > 0 && p.Status < 500, "a 5xx is infrastructure, not a provider verdict");
        report.Probes.Should().OnlyContain(p => p.Verdict != ProbeVerdict.Unknown,
            "every probe must end in a recorded verdict — that record is the deliverable of 16.0a");
        report.Probes.Count(p => p.Probe == ProbeId.WithBlock && p.Status == 200).Should().BePositive(
            "at least one candidate model must answer a tool turn, or nothing was learned");
        File.Exists(ArtifactPath).Should().BeTrue("the artifact is what the matrix is filled from");
    }

    /// <summary>Asks the endpoint which models exist, so the probes run on names that are real.</summary>
    private static async Task<List<string>> ListModelsAsync(HttpClient http, Uri baseUrl, ProbeReport report)
    {
        var ids = new List<string>();

        using var response = await http.GetAsync(new Uri(baseUrl, "models"));
        var body = await response.Content.ReadAsStringAsync();
        report.ModelListStatus = (int)response.StatusCode;

        if (response.IsSuccessStatusCode && TryParse(body) is JsonObject root && root["data"] is JsonArray data)
            foreach (var entry in data)
                if (entry?["id"]?.GetValue<string>() is { Length: > 0 } id && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                    ids.Add(id);

        report.ModelsAvailable = ids.ToArray();
        return ids;
    }

    /// <summary>Embedding and other non-chat entries are listed too; probing them would only add noise.</summary>
    private static bool IsChatModel(string id) => !id.Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>First question: a tool turn with nothing replayed — what does the answer carry?</summary>
    private static async Task<ProbeOutcome?> ProbeWithBlockAsync(
        HttpClient http, Uri baseUrl, string model, ProbeReport report)
    {
        var body = ChatBody(model, new JsonArray(SystemPrompt(), UserQuestion()), withTools: true);
        return await SendAsync(http, baseUrl, ProbeId.WithBlock, body, expectToolCall: true, report);
    }

    /// <summary>
    /// Second question: replay the block we just received — the shape 16.2 must be able to reproduce.
    /// Skipped, with a reason, when the model returned nothing to replay.
    /// </summary>
    private static async Task ProbeWithBlockReplayAsync(
        HttpClient http, Uri baseUrl, string model, ProbeOutcome? withBlock, ProbeReport report)
    {
        if (withBlock?.ThinkingText is not { Length: > 0 } thinking || withBlock.ToolCalls.Count == 0)
        {
            report.Probes.Add(new ProbeOutcome
            {
                Probe = ProbeId.WithBlockReplay,
                Model = model,
                Verdict = ProbeVerdict.NotApplicable,
                VerdictDetail = string.IsNullOrEmpty(withBlock?.ThinkingText)
                    ? "the model returned no thinking block, so there is nothing to replay"
                    : "the model returned no tool call to attach the block to"
            });
            return;
        }

        var call = withBlock.ToolCalls[0];
        var messages = new JsonArray(
            SystemPrompt(), UserQuestion(),
            AssistantTurn(call, thinking, withBlock.ContentText),
            ToolResult(call, "shipped"));

        await SendAsync(http, baseUrl, ProbeId.WithBlockReplay,
            ChatBody(model, messages, withTools: true), expectToolCall: false, report);
    }

    /// <summary>
    /// Third question, the K4 test: the same tool turn with the block dropped — exactly what the connector
    /// sends today. Three shapes, because the answer turned out to depend on the turn rather than on
    /// <c>tools</c>: as the model returned it minus the block, with a non-empty <c>content</c>, and with no
    /// tool surface declared at all (the control for the documented "the 400 needs <c>tools</c>" rule).
    /// </summary>
    private static async Task ProbeWithoutBlockAsync(
        HttpClient http, Uri baseUrl, string model, ProbeOutcome? withBlock, ProbeReport report, bool withTools)
    {
        var call = withBlock?.ToolCalls.FirstOrDefault() ?? SyntheticCall();
        var messages = new JsonArray(
            SystemPrompt(), UserQuestion(),
            AssistantTurn(call, thinking: null, withBlock?.ContentText),
            ToolResult(call, "shipped"));

        await SendAsync(http, baseUrl,
            withTools ? ProbeId.WithoutBlockTools : ProbeId.WithoutBlockNoTools,
            ChatBody(model, messages, withTools), expectToolCall: false, report);
    }

    /// <summary>
    /// The same dropped-block turn, but with the visible <c>content</c> the model produced kept in place:
    /// if the provider validates the block only when the assistant turn has text, this is where it says so.
    /// </summary>
    private static async Task ProbeWithoutBlockWithContentAsync(
        HttpClient http, Uri baseUrl, string model, ProbeOutcome? withBlock, ProbeReport report)
    {
        var call = withBlock?.ToolCalls.FirstOrDefault() ?? SyntheticCall();
        var messages = new JsonArray(
            SystemPrompt(), UserQuestion(),
            AssistantTurn(call, thinking: null, withBlock?.ContentText ?? "Let me look that up."),
            ToolResult(call, "shipped"));

        await SendAsync(http, baseUrl, ProbeId.WithoutBlockContent,
            ChatBody(model, messages, withTools: true), expectToolCall: false, report);
    }

    /// <summary>
    /// Fourth question: a conversation created before the phase — its tool turns never carried a block.
    /// Continuing it is the class of the 2026-09-07 incident.
    /// </summary>
    private static async Task ProbeOldHistoryAsync(HttpClient http, Uri baseUrl, string model, ProbeReport report)
    {
        var call = SyntheticCall();
        var messages = new JsonArray(
            SystemPrompt(), UserQuestion(),
            AssistantTurn(call, thinking: null),
            ToolResult(call, "shipped"),
            AssistantText("shipped"),
            new JsonObject { ["role"] = "user", ["content"] = "And what about order 43?" });

        await SendAsync(http, baseUrl, ProbeId.OldHistory,
            ChatBody(model, messages, withTools: true), expectToolCall: true, report);
    }

    /// <summary>
    /// Fifth question, learned the hard way on the first run: thinking mode refuses a pinned tool_choice.
    /// Kept as a probe so the constraint stays proven — the connector must never send
    /// <c>tool_choice: "required"</c> to a thinking model.
    /// </summary>
    private static async Task ProbeToolChoiceAsync(HttpClient http, Uri baseUrl, string model, ProbeReport report)
    {
        var body = ChatBody(model, new JsonArray(SystemPrompt(), UserQuestion()), withTools: true);
        body["tool_choice"] = "required";

        await SendAsync(http, baseUrl, ProbeId.ToolChoiceRequired, body, expectToolCall: true, report);
    }

    /// <summary>Sends one probe and records everything the matrix needs — and nothing it must not carry.</summary>
    private static async Task<ProbeOutcome> SendAsync(
        HttpClient http, Uri baseUrl, string probeId, JsonObject body, bool expectToolCall, ProbeReport report)
    {
        var outcome = new ProbeOutcome
        {
            Probe = probeId,
            Model = body["model"]!.GetValue<string>(),
            Request = Describe(body)
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(baseUrl, "chat/completions"), content);
        var text = await response.Content.ReadAsStringAsync();

        outcome.Status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) Capture(outcome, TryParse(text), expectToolCall);
        else Reject(outcome, text);

        report.Probes.Add(outcome);
        return outcome;
    }

    /// <summary>Builds the request body in the shape the connector sends, minus everything it does not send.</summary>
    private static JsonObject ChatBody(string model, JsonArray messages, bool withTools)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = MaxTokens,
            ["stream"] = false
        };

        if (!withTools) return body;

        // No tool_choice: thinking mode refuses the pinned form (see the class remarks), and the prompt
        // already asks for the tool by name.
        body["tools"] = ToolDefinitions();
        return body;
    }

    /// <summary>Roles of the request plus whether the tool surface was declared — the "what we sent" column.</summary>
    private static string Describe(JsonObject body)
    {
        var messages = (JsonArray)body["messages"]!;
        var roles = messages.Select(m =>
        {
            var role = m?["role"]?.GetValue<string>() ?? "?";
            if (m?["tool_calls"] is not null)
                role += m?["reasoning_content"] is not null ? "(tool_calls+thinking)" : "(tool_calls)";
            return role;
        });

        return $"{string.Join(",", roles)} | tools={(body["tools"] is null ? "no" : "yes")}";
    }

    private static void Capture(ProbeOutcome outcome, JsonNode? json, bool expectToolCall)
    {
        if (json?["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["message"]?.AsObject() is not { } message)
        {
            outcome.Verdict = ProbeVerdict.Unknown;
            outcome.VerdictDetail = "200 without a message to read";
            return;
        }

        var first = choices[0]!.AsObject();
        outcome.ResponseModel = json["model"]?.GetValue<string>();
        outcome.SystemFingerprint = json["system_fingerprint"]?.GetValue<string>();
        outcome.FinishReason = first["finish_reason"]?.GetValue<string>();
        outcome.MessageKeys = message.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        var thinking = message["reasoning_content"]?.GetValue<string>();
        if (thinking is not null) outcome.ThinkingField = "reasoning_content";
        else if (message["reasoning"] is JsonValue other) { outcome.ThinkingField = "reasoning"; thinking = other.GetValue<string>(); }

        outcome.ThinkingPresent = !string.IsNullOrEmpty(thinking);
        outcome.ThinkingLength = thinking?.Length ?? 0;
        outcome.ThinkingSha256 = thinking is { Length: > 0 } ? Sha256(thinking) : null;
        outcome.ThinkingText = thinking;

        outcome.ContentLength = message["content"]?.GetValue<string>()?.Length ?? 0;
        outcome.ContentText = message["content"]?.GetValue<string>();
        if (message["tool_calls"] is JsonArray calls)
        {
            outcome.ToolCallCount = calls.Count;
            foreach (var call in calls)
            {
                if (call?["function"] is not JsonObject fn) continue;
                outcome.ToolCalls.Add(new ToolCallShape
                {
                    Id = call["id"]?.GetValue<string>() ?? string.Empty,
                    Name = fn["name"]?.GetValue<string>() ?? string.Empty,
                    Arguments = fn["arguments"]?.GetValue<string>() ?? string.Empty
                });
            }
        }

        if (json["usage"] is JsonObject usage)
        {
            outcome.PromptTokens = usage["prompt_tokens"]?.GetValue<int>();
            outcome.CompletionTokens = usage["completion_tokens"]?.GetValue<int>();
            outcome.ReasoningTokens = usage["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<int>();
        }

        var degraded = expectToolCall && outcome.ToolCallCount == 0;
        outcome.Verdict = degraded ? ProbeVerdict.AcceptedDegraded : ProbeVerdict.Accepted;
        outcome.VerdictDetail = degraded
            ? "answered without calling the tool the prompt asked for"
            : "answered";
    }

    private static void Reject(ProbeOutcome outcome, string body)
    {
        var error = TryParse(body)?["error"] as JsonObject;
        outcome.ErrorType = Truncate(error?["type"]?.GetValue<string>(), 120);
        outcome.ErrorMessage = Truncate(error?["message"]?.GetValue<string>() ?? body, 400);
        outcome.Verdict = ProbeVerdict.Rejected;
        outcome.VerdictDetail = $"HTTP {outcome.Status}: {outcome.ErrorType ?? outcome.ErrorMessage}";
    }

    private static JsonObject SystemPrompt() => new()
    {
        ["role"] = "system",
        ["content"] = $"Use the {ToolName} tool to look up the order, then reply with only the status word."
    };

    private static JsonObject UserQuestion() => new()
    {
        ["role"] = "user",
        ["content"] = "What is the status of order 42?"
    };

    /// <summary>An assistant tool turn — with the thinking block only when the probe means to send one.</summary>
    private static JsonObject AssistantTurn(ToolCallShape call, string? thinking, string? content = null)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
            })
        };

        if (thinking is { Length: > 0 })
            message["reasoning_content"] = thinking;

        return message;
    }

    private static JsonObject ToolResult(ToolCallShape call, string status) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = call.Id,
        ["content"] = $"{{\"status\":\"{status}\"}}"
    };

    private static JsonObject AssistantText(string text) => new()
    {
        ["role"] = "assistant",
        ["content"] = text
    };

    /// <summary>A tool call the answer did not supply, so every probe still runs when the first one fails.</summary>
    private static ToolCallShape SyntheticCall() => new()
    {
        Id = "call_probe_1",
        Name = ToolName,
        Arguments = "{\"orderId\":\"42\"}"
    };

    private static JsonArray ToolDefinitions() => new(
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "Look up an order by id and return its status.",
                ["parameters"] = JsonNode.Parse(
                    """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")
            }
        });

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    /// <summary>First 16 hex digits of SHA-256 — enough to compare two runs without carrying the thought.</summary>
    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];

    private static string? Truncate(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : text[..max] + "...";

    /// <summary>Where the findings land: next to the test binary, so <c>bin</c> keeps them out of the repo.</summary>
    private static string ArtifactPath => Path.Combine(AppContext.BaseDirectory, "16-0a-probe.json");

    private static void WriteArtifact(ProbeReport report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(ArtifactPath, json);
    }
}

/// <summary>Ids of the probes; the matrix cites these, so they are stable strings, not enum numbers.</summary>
internal static class ProbeId
{
    /// <summary>A tool turn with nothing replayed.</summary>
    public const string WithBlock = "with-block";

    /// <summary>The block we received, sent back on the next tool turn.</summary>
    public const string WithBlockReplay = "with-block-replay";

    /// <summary>The K4 probe: tool turn without its block, tool surface declared.</summary>
    public const string WithoutBlockTools = "without-block-tools";

    /// <summary>Control for K4: same turn, no tool surface declared.</summary>
    public const string WithoutBlockNoTools = "without-block-no-tools";

    /// <summary>Same turn with its visible <c>content</c> kept, block still dropped.</summary>
    public const string WithoutBlockContent = "without-block-content";

    /// <summary>A conversation created before the phase, continued.</summary>
    public const string OldHistory = "old-history";

    /// <summary>An alias the endpoint no longer lists, asked the first question only.</summary>
    public const string AliasWithBlock = "alias-with-block";

    /// <summary>A tool turn with <c>tool_choice: "required"</c> — the form thinking mode refuses.</summary>
    public const string ToolChoiceRequired = "tool-choice-required";
}

/// <summary>Verdict vocabulary shared by the artifact and the matrix.</summary>
internal static class ProbeVerdict
{
    /// <summary>Nothing was learned — the probe must not end here.</summary>
    public const string Unknown = "unknown";

    /// <summary>2xx and the expected shape came back.</summary>
    public const string Accepted = "accepted";

    /// <summary>2xx but thinner than the request pinned — the "degraded answer" outcome.</summary>
    public const string AcceptedDegraded = "accepted-degraded";

    /// <summary>Provider refused the request; the error text is in the outcome.</summary>
    public const string Rejected = "rejected";

    /// <summary>The probe could not be asked (nothing to replay) — a recorded answer, not a gap.</summary>
    public const string NotApplicable = "not-applicable";
}

/// <summary>The tool call of an assistant turn, in the shape it travels on the wire.</summary>
internal sealed class ToolCallShape
{
    /// <summary>Provider-assigned call id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Function name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Function arguments as a JSON string.</summary>
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>One probe: what was sent, what came back, what it means.</summary>
internal sealed class ProbeOutcome
{
    /// <summary>Probe id — see <see cref="ProbeId"/>.</summary>
    public string Probe { get; set; } = string.Empty;

    /// <summary>Model the probe ran on.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Model id the provider says it served — how an alias resolves.</summary>
    public string? ResponseModel { get; set; }

    /// <summary>Backend fingerprint, when the provider exposes one.</summary>
    public string? SystemFingerprint { get; set; }

    /// <summary>HTTP status of the response.</summary>
    public int Status { get; set; }

    /// <summary>Verdict — see <see cref="ProbeVerdict"/>.</summary>
    public string Verdict { get; set; } = ProbeVerdict.Unknown;

    /// <summary>One line explaining the verdict.</summary>
    public string? VerdictDetail { get; set; }

    /// <summary>Roles and tool surface of the request we sent.</summary>
    public string Request { get; set; } = string.Empty;

    /// <summary>Provider's <c>finish_reason</c>.</summary>
    public string? FinishReason { get; set; }

    /// <summary>Keys present on the assistant message — how the provider names its fields.</summary>
    public string[] MessageKeys { get; set; } = [];

    /// <summary>Name of the field carrying the thinking block, when one came.</summary>
    public string? ThinkingField { get; set; }

    /// <summary>Whether a non-empty thinking block came back.</summary>
    public bool ThinkingPresent { get; set; }

    /// <summary>Length of the thinking block in characters.</summary>
    public int ThinkingLength { get; set; }

    /// <summary>Short hash of the thinking block, for run-to-run comparison.</summary>
    public string? ThinkingSha256 { get; set; }

    /// <summary>Length of the visible answer.</summary>
    public int ContentLength { get; set; }

    /// <summary>Number of tool calls in the answer.</summary>
    public int ToolCallCount { get; set; }

    /// <summary>The tool calls themselves, reused when a later probe replays this turn.</summary>
    public List<ToolCallShape> ToolCalls { get; set; } = [];

    /// <summary>Prompt tokens reported by the provider.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion tokens reported by the provider.</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>Reasoning tokens, when the provider splits them out.</summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>Provider error type, when the request was refused.</summary>
    public string? ErrorType { get; set; }

    /// <summary>Provider error text, truncated.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The block itself, kept in memory so the replay probe can send it back. Never written to the
    /// artifact: thoughts are the model's, and this file is meant to be committed as evidence.
    /// </summary>
    [JsonIgnore]
    public string? ThinkingText { get; set; }

    /// <summary>The visible answer of the turn, kept in memory so the dropped-block probe can keep it.</summary>
    [JsonIgnore]
    public string? ContentText { get; set; }
}

/// <summary>The whole 16.0a run: which models answered, and every probe's outcome.</summary>
internal sealed class ProbeReport
{
    /// <summary>Provider alias probed.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Chat Completions endpoint the probes used.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>When the run finished.</summary>
    public DateTimeOffset FinishedUtc { get; set; }

    /// <summary>HTTP status of <c>GET /models</c>.</summary>
    public int ModelListStatus { get; set; }

    /// <summary>Model ids the endpoint offers.</summary>
    public string[] ModelsAvailable { get; set; } = [];

    /// <summary>Every probe the run performed, in order.</summary>
    public List<ProbeOutcome> Probes { get; set; } = [];
}
