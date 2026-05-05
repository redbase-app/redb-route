using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Unit tests for SignalRHeaders: constants, prefix, IsRedbHeader.
/// </summary>
public class SignalRHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        SignalRHeaders.Prefix.Should().Be("redbSignalR.");
    }

    [Theory]
    [InlineData(nameof(SignalRHeaders.Method), "redbSignalR.Method")]
    [InlineData(nameof(SignalRHeaders.ConnectionId), "redbSignalR.ConnectionId")]
    [InlineData(nameof(SignalRHeaders.UserId), "redbSignalR.UserId")]
    [InlineData(nameof(SignalRHeaders.Event), "redbSignalR.Event")]
    [InlineData(nameof(SignalRHeaders.HubPath), "redbSignalR.HubPath")]
    [InlineData(nameof(SignalRHeaders.Protocol), "redbSignalR.Protocol")]
    [InlineData(nameof(SignalRHeaders.Ssl), "redbSignalR.Ssl")]
    [InlineData(nameof(SignalRHeaders.Target), "redbSignalR.Target")]
    [InlineData(nameof(SignalRHeaders.Group), "redbSignalR.Group")]
    [InlineData(nameof(SignalRHeaders.TargetConnection), "redbSignalR.TargetConnection")]
    [InlineData(nameof(SignalRHeaders.TargetUser), "redbSignalR.TargetUser")]
    [InlineData(nameof(SignalRHeaders.AddToGroup), "redbSignalR.AddToGroup")]
    [InlineData(nameof(SignalRHeaders.RemoveFromGroup), "redbSignalR.RemoveFromGroup")]
    public void AllHeaders_HaveCorrectPrefix(string fieldName, string expected)
    {
        var value = typeof(SignalRHeaders).GetField(fieldName)!.GetValue(null) as string;
        value.Should().Be(expected);
        value.Should().StartWith(SignalRHeaders.Prefix);
    }

    [Theory]
    [InlineData("redbSignalR.Method", true)]
    [InlineData("redbSignalR.ConnectionId", true)]
    [InlineData("X-Custom-Header", false)]
    [InlineData("Content-Type", false)]
    [InlineData("redbSignalR.", true)]
    public void IsRedbHeader_WorksCorrectly(string header, bool expected)
    {
        SignalRHeaders.IsRedbHeader(header).Should().Be(expected);
    }
}
