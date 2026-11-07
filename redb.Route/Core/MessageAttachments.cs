using System.Collections;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>The attachment list of a <see cref="Message"/>.</summary>
internal sealed class MessageAttachments : IMessageAttachments
{
    /// <summary>
    /// What a message that does not implement attachments answers with: it carries none, and adding one fails instead of
    /// being dropped.
    /// </summary>
    internal static readonly IMessageAttachments Unsupported = new UnsupportedAttachments();

    private readonly List<Attachment> _items;

    public MessageAttachments() => _items = [];

    private MessageAttachments(List<Attachment> items) => _items = items;

    public int Count => _items.Count;

    public Attachment this[int index] => _items[index];

    public Attachment? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var item in _items)
            if (string.Equals(item.Name, name, StringComparison.Ordinal))
                return item;
        return null;
    }

    public IReadOnlyList<Attachment> FindAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _items.Where(i => string.Equals(i.Name, name, StringComparison.Ordinal)).ToArray();
    }

    public void Add(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _items.Add(attachment);
    }

    public void Set(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var first = _items.FindIndex(i => string.Equals(i.Name, attachment.Name, StringComparison.Ordinal));
        if (first < 0)
        {
            _items.Add(attachment);
            return;
        }
        _items[first] = attachment;
        for (var i = _items.Count - 1; i > first; i--)
            if (string.Equals(_items[i].Name, attachment.Name, StringComparison.Ordinal))
                _items.RemoveAt(i);
    }

    public int Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _items.RemoveAll(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    public void Clear() => _items.Clear();

    /// <summary>Same attachments in a new list (the content sources are shared), as <see cref="Message.Clone"/> shares the body.</summary>
    public MessageAttachments Copy() => new([.. _items]);

    /// <summary>Same attachments with isolated content, as <see cref="Message.Snapshot"/> deep-copies the body.</summary>
    public MessageAttachments DeepCopy() => new(_items.Select(i => i.WithContent(i.Content.DeepCopy())).ToList());

    /// <summary>Appends the attachments of <paramref name="source"/> to <paramref name="target"/>, sharing their content.</summary>
    internal static void CopyInto(IMessage source, IMessage target)
    {
        if (source is not Message { AttachmentsOrNull: { } items })
            return;
        var into = target.Attachments;
        foreach (var item in items)
            into.Add(item);
    }

    /// <summary>
    /// The pipeline's Out-to-In rule: when <paramref name="source"/> carries attachments they replace those of
    /// <paramref name="target"/>; a step that set no attachments on its Out leaves the In ones in place.
    /// </summary>
    internal static void ReplaceWhenPresent(IMessage source, IMessage target)
    {
        if (source is not Message { AttachmentsOrNull: { } items })
            return;
        var into = target.Attachments;
        into.Clear();
        foreach (var item in items)
            into.Add(item);
    }

    /// <summary>Total length of the attachment content of <paramref name="message"/>; 0 without attachments.</summary>
    internal static long TotalLength(IMessage message) =>
        message is Message { AttachmentsOrNull: { } items } ? items.TotalLength() : 0;

    /// <summary>Total length of the content of all attachments.</summary>
    public long TotalLength() => _items.Sum(i => i.Content.Length);

    public IEnumerator<Attachment> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class UnsupportedAttachments : IMessageAttachments
    {
        public int Count => 0;
        public Attachment this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public Attachment? Find(string name) => null;
        public IReadOnlyList<Attachment> FindAll(string name) => [];
        public void Add(Attachment attachment) => throw NotSupported();
        public void Set(Attachment attachment) => throw NotSupported();
        public int Remove(string name) => 0;
        public void Clear() { }
        public IEnumerator<Attachment> GetEnumerator() => Enumerable.Empty<Attachment>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static NotSupportedException NotSupported() =>
            new("This message type does not carry attachments; use redb.Route.Core.Message.");
    }
}
