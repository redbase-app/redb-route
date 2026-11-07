namespace redb.Route.Abstractions;

/// <summary>
/// The attachments of one message, in order. Unlike Camel's map, names may repeat: a form posts several files under one
/// field and two files may share a name, and a map would lose all but one. Lookups by name return the first match.
/// Names compare ordinally (file names and <c>Content-ID</c>s are case-sensitive).
/// </summary>
/// <remarks>Not thread-safe, like <see cref="IMessage.Headers"/>.</remarks>
public interface IMessageAttachments : IReadOnlyList<Attachment>
{
    /// <summary>The first attachment named <paramref name="name"/>, or <c>null</c>.</summary>
    Attachment? Find(string name);

    /// <summary>Every attachment named <paramref name="name"/>, in order; empty when there is none.</summary>
    IReadOnlyList<Attachment> FindAll(string name);

    /// <summary>Appends an attachment; an attachment with the same name may already be there.</summary>
    void Add(Attachment attachment);

    /// <summary>
    /// Puts <paramref name="attachment"/> in place of every attachment with its name: at the position of the first one,
    /// or at the end when there is none.
    /// </summary>
    void Set(Attachment attachment);

    /// <summary>Removes every attachment named <paramref name="name"/>.</summary>
    /// <returns>How many were removed.</returns>
    int Remove(string name);

    /// <summary>Removes all attachments.</summary>
    void Clear();
}
