using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Wave 16.2 (debt from 16.1): a thinking block survives the replay fixture. The snapshot has a slot
/// for the thought, its signature and a redacted payload on <b>both</b> sides — a recorded block
/// without them came back as <c>Type = "unknown"</c> and vanished, so an eval replay of a tool loop
/// silently lost the reasoning the provider is supposed to get back.
/// </summary>
public sealed class ReplayProviderThinkingTests
{
    [Fact]
    public async Task ThinkingBlock_SurvivesReplaySnapshot()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"redb-replay-{Guid.NewGuid():N}.json");
        try
        {
            // The request side: the shape a second iteration of a tool loop sends — the assistant turn
            // that carries the reasoning, then the tool result.
            var request = new LlmRequest
            {
                Messages =
                [
                    LlmMessage.User("status of order 42?"),
                    new LlmMessage
                    {
                        Role = "assistant",
                        Content =
                        [
                            new LlmThinkingBlock("previous reasoning", "sig-prev"),
                            new LlmToolUseBlock("tu_1", "order_lookup", """{"orderId":"42"}""")
                        ]
                    },
                    new LlmMessage { Role = "user", Content = [new LlmToolResultBlock("tu_1", """{"status":"shipped"}""")] }
                ]
            };

            var inner = new FakeProvider().Enqueue(new LlmResponse
            {
                Content = [new LlmThinkingBlock("recorded reasoning", "sig-9"), new LlmTextBlock("shipped")],
                StopReason = LlmStopReason.EndTurn,
                Usage = new LlmUsage(3, 4)
            });

            var recorder = new ReplayProvider(fixture, ReplayMode.Record, inner);
            var recorded = await recorder.CompleteAsync(request);
            recorded.Content.OfType<LlmThinkingBlock>().Should().ContainSingle();
            recorder.EntryCount.Should().Be(1);

            var snapshot = await File.ReadAllTextAsync(fixture);
            snapshot.Should().Contain("sig-prev", "the request snapshot keeps the signature that makes the block replayable");
            snapshot.Should().Contain("sig-9", "the response snapshot keeps it too");
            snapshot.Should().Contain("thinking", "the block is recorded under its own kind, not as \"unknown\"");

            var replayed = await new ReplayProvider(fixture, ReplayMode.Replay).CompleteAsync(request);

            var block = replayed.Content.OfType<LlmThinkingBlock>().Should().ContainSingle(
                "a block recorded without its fields comes back as \"unknown\" and is dropped").Subject;
            block.Text.Should().Be("recorded reasoning");
            block.Signature.Should().Be("sig-9");
        }
        finally
        {
            if (File.Exists(fixture)) File.Delete(fixture);
        }
    }
}
