namespace SerialNumbers.Core.Services;

/// <summary>Names of files in the archive and in partner folders.</summary>
public static class ArchivePaths
{
    /// <summary>
    /// <c>{partner}/{yyyy}/{MM}/{dd}/{HHmmssfff}_{file}</c>. The time prefix keeps every delivery,
    /// including a partner re-sending a file under the same name: nothing is ever overwritten.
    /// </summary>
    public static string For(string partnerCode, string fileName, DateTimeOffset receivedAt) =>
        $"{Safe(partnerCode)}/{receivedAt:yyyy}/{receivedAt:MM}/{receivedAt:dd}/{receivedAt:HHmmssfff}_{Safe(fileName)}";

    /// <summary>A file name for a message that arrived over AS2 and has none: its Message-ID.</summary>
    public static string FromAs2MessageId(string? messageId) =>
        Safe(string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId.Trim('<', '>')) + ".xml";

    /// <summary>
    /// <c>SNRESP_{requestId}_{responseId}.xml</c>. The request id alone is not enough: a partner that
    /// sends a request id twice gets two responses, and the rejection of the repeat must not overwrite
    /// the original acceptance. A delivery retried from the outbox keeps its name.
    /// </summary>
    public static string ResponseFileName(string requestId, long responseObjectId) =>
        $"SNRESP_{Safe(requestId)}_{responseObjectId}.xml";

    private static string Safe(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}
