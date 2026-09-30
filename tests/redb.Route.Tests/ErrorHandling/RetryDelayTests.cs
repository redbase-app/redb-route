using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.Processors;

namespace redb.Route.Tests.ErrorHandling;

/// <summary>
/// The retry waits the delay its policy computes — jitter only when the policy asks for it
/// (<see cref="RetryPolicy.CollisionAvoidanceFactor"/>, off by default as Camel's collision avoidance), capped by
/// <see cref="RetryPolicy.MaxDelay"/>. The processor used to add ±15% of its own on top: jitter twice when the policy
/// already had some, jitter nobody configured when it had none, and a wait past the cap.
/// </summary>
public class RetryDelayTests
{
    private sealed class DelayLog : ILogger
    {
        public List<double> Delays { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values
                && values.FirstOrDefault(v => v.Key == "Delay").Value is double delay)
                Delays.Add(delay);
        }
    }

    private static async Task<List<double>> WaitsOf(RetryPolicy policy)
    {
        var log = new DelayLog();
        var processor = new RetryProcessor(new DelegateProcessor(_ => throw new InvalidOperationException("down")), policy, log);
        var act = () => processor.Process(new Exchange(new Message("x")));
        await act.Should().ThrowAsync<InvalidOperationException>();
        return log.Delays;
    }

    [Fact]
    public async Task Without_jitter_in_the_policy_the_retry_waits_exactly_the_policy_delay()
    {
        var policy = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.FromMilliseconds(10), BackoffMultiplier = 2 };

        var waits = await WaitsOf(policy);

        waits.Should().Equal(policy.GetDelay(0).TotalMilliseconds, policy.GetDelay(1).TotalMilliseconds,
            policy.GetDelay(2).TotalMilliseconds);
    }

    [Fact]
    public async Task The_wait_never_passes_the_cap()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 5, InitialDelay = TimeSpan.FromMilliseconds(20), BackoffMultiplier = 2,
            MaxDelay = TimeSpan.FromMilliseconds(20), CollisionAvoidanceFactor = 0.15,
        };

        var waits = await WaitsOf(policy);

        waits.Should().HaveCount(5).And.OnlyContain(w => w <= 20);
    }
}
