namespace redb.Route.Configuration;

/// <summary>Message attachment options, section <c>RedbRoute:Attachments</c>.</summary>
public sealed class AttachmentOptions
{
    /// <summary>Default <see cref="MaxReplayableBytes"/>: 10 MB, the default HTTP request body limit.</summary>
    public const long DefaultMaxReplayableBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The total attachment content of one exchange (<c>In</c> and <c>Out</c>) that a snapshot for replay keeps. A
    /// <c>.Replayable()</c> checkpoint of an exchange whose attachments are larger is not captured (logged, processing
    /// goes on), and so the exchange cannot be replayed from a checkpoint or from the Tsak DLQ, which stores that
    /// checkpoint. <c>0</c> means an exchange with any attachment is not replayable. Default: 10 MB.
    /// </summary>
    public long MaxReplayableBytes { get; set; } = DefaultMaxReplayableBytes;
}
