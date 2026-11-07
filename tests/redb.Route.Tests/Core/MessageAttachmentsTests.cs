using System.Text;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Core;

/// <summary>
/// The attachments of a message (Camel's <c>AttachmentMessage</c>): a list on the message, not a property of the
/// exchange, with re-readable content that follows the body's rules for copying, snapshots and disposal.
/// </summary>
public class MessageAttachmentsTests
{
    private static Attachment Bytes(string name, string text, string? fileName = null, string contentType = "text/plain") => new()
    {
        Name = name,
        FileName = fileName,
        ContentType = contentType,
        Content = AttachmentContent.FromBytes(Encoding.UTF8.GetBytes(text))
    };

    private static string Text(Attachment attachment) => Encoding.UTF8.GetString(attachment.Content.ReadAllBytes());

    private static StreamCache Cache(string text)
    {
        var cache = new StreamCache();
        cache.Write(Encoding.UTF8.GetBytes(text));
        cache.CompleteWriting();
        return cache;
    }

    // ── The list ──

    [Fact]
    public void Names_may_repeat_and_lookups_answer_the_first()
    {
        var message = new Message();
        message.Attachments.Add(Bytes("files", "one", "a.pdf"));
        message.Attachments.Add(Bytes("files", "two", "b.pdf"));
        message.Attachments.Add(Bytes("note", "three"));

        message.Attachments.Should().HaveCount(3, "a form posts several files under one field");
        message.Attachments.Find("files")!.FileName.Should().Be("a.pdf");
        message.Attachments.FindAll("files").Select(a => a.FileName).Should().Equal("a.pdf", "b.pdf");
        message.Attachments.Find("Files").Should().BeNull("names compare ordinally");
    }

    [Fact]
    public void Set_replaces_every_attachment_of_the_name_at_the_first_position()
    {
        var message = new Message();
        message.Attachments.Add(Bytes("a", "1"));
        message.Attachments.Add(Bytes("b", "2"));
        message.Attachments.Add(Bytes("a", "3"));

        message.Attachments.Set(Bytes("a", "new"));

        message.Attachments.Select(a => a.Name).Should().Equal("a", "b");
        Text(message.Attachments[0]).Should().Be("new");
        message.Attachments.Remove("b").Should().Be(1);
        message.Attachments.Should().ContainSingle();
    }

    [Fact]
    public void A_line_break_in_a_name_is_refused_before_it_can_reach_a_MIME_header()
    {
        var act = () => new Attachment { Name = "a\r\nBcc: x", Content = AttachmentContent.FromBytes([1]) };

        act.Should().Throw<ArgumentException>().WithMessage("*line break*");
    }

    [Fact]
    public void A_message_type_without_attachments_reads_none_and_refuses_additions()
    {
        IMessage foreign = new ForeignMessage();

        foreign.Attachments.Should().BeEmpty();
        var add = () => foreign.Attachments.Add(Bytes("a", "1"));
        add.Should().Throw<NotSupportedException>();
    }

    // ── Content ──

    [Fact]
    public async Task Content_is_read_from_the_start_on_every_open()
    {
        var content = AttachmentContent.FromStreamCache(Cache("payload"));

        (await content.ReadAllBytesAsync()).Should().Equal("payload"u8.ToArray());
        content.ReadAllBytes().Should().Equal("payload"u8.ToArray(), "a resend reads the same attachment again");
        content.Length.Should().Be(7);
    }

    [Fact]
    public void Readers_of_one_StreamCache_do_not_move_each_other_or_the_cache()
    {
        using var cache = Cache("abcdef");
        var first = cache.OpenReader();
        var second = cache.OpenReader();
        var buffer = new byte[3];

        first.ReadExactly(buffer);
        Encoding.ASCII.GetString(buffer).Should().Be("abc");
        second.ReadExactly(buffer);
        Encoding.ASCII.GetString(buffer).Should().Be("abc", "each reader has its own position");
        first.ReadExactly(buffer);
        Encoding.ASCII.GetString(buffer).Should().Be("def");
        cache.Position.Should().Be(0, "the readers leave the cache's own position alone");
    }

    [Fact]
    public async Task A_file_attachment_is_deleted_with_the_exchange_only_when_asked()
    {
        var kept = Path.GetTempFileName();
        var owned = Path.GetTempFileName();
        try
        {
            var message = new Message();
            message.Attachments.Add(new Attachment { Name = "kept", Content = AttachmentContent.FromFile(kept) });
            message.Attachments.Add(new Attachment { Name = "owned", Content = AttachmentContent.FromFile(owned, deleteWithExchange: true) });

            await new Exchange(message).DisposeAsync();

            File.Exists(kept).Should().BeTrue();
            File.Exists(owned).Should().BeFalse();
        }
        finally
        {
            File.Delete(kept);
            File.Delete(owned);
        }
    }

    // ── Copies, snapshots, disposal ──

    [Fact]
    public void Clone_gets_its_own_list_over_the_same_content()
    {
        var original = new Message();
        original.Attachments.Add(Bytes("a", "1"));

        var clone = original.Clone();
        clone.Attachments.Add(Bytes("b", "2"));

        original.Attachments.Should().ContainSingle("the clone's list is its own");
        clone.Attachments[0].Content.Should().BeSameAs(original.Attachments[0].Content, "content is shared like the body");
    }

    [Fact]
    public void Snapshot_freezes_the_content()
    {
        var bytes = Encoding.UTF8.GetBytes("frozen");
        var original = new Message();
        original.Attachments.Add(new Attachment { Name = "a", Content = AttachmentContent.FromBytes(bytes) });

        var snapshot = original.Snapshot();
        bytes[0] = (byte)'X';

        Text(snapshot.Attachments[0]).Should().Be("frozen");
    }

    [Fact]
    public async Task A_StreamCache_attachment_is_disposed_once_by_the_owner_and_not_by_a_copy()
    {
        var cache = Cache("x");
        var root = new Exchange(new Message("body"));
        root.In.Attachments.Add(new Attachment { Name = "a", Content = AttachmentContent.FromStreamCache(cache) });
        var copy = root.Clone();

        await copy.DisposeAsync();
        cache.CanRead.Should().BeTrue("the origin still carries it");

        await root.DisposeAsync();
        cache.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task A_checkpoint_is_not_captured_when_attachments_exceed_MaxReplayableBytes()
    {
        var options = new RouteEngineOptions { Attachments = { MaxReplayableBytes = 4 } };
        await using var context = new RouteContext($"att-replay-{Guid.NewGuid():N}", options: options);
        context.AddComponent(new DirectComponent());
        IExchange? seen = null;
        context.AddRoutes(r => r.From("direct://att-replay")
            .AddAttachment("big", new ConstantExpression("12345"))
            .Replayable("m")
            .Process(e => seen = e));
        await context.Start();

        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct://att-replay", "body");

        seen!.Properties.ContainsKey(RouteCheckpoint.PropertyKey).Should().BeFalse(
            "a snapshot keeps at most RedbRoute:Attachments:MaxReplayableBytes of attachments");
    }

    [Fact]
    public async Task A_checkpoint_keeps_attachments_within_MaxReplayableBytes()
    {
        await using var context = new RouteContext($"att-replay-ok-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        IExchange? seen = null;
        context.AddRoutes(r => r.From("direct://att-replay-ok")
            .AddAttachment("small", new ConstantExpression("12345"))
            .Replayable("m")
            .Process(e => seen = e));
        await context.Start();

        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct://att-replay-ok", "body");

        var checkpoint = (RouteCheckpoint)seen!.Properties[RouteCheckpoint.PropertyKey]!;
        Text(checkpoint.Snapshot.In.Attachments.Single()).Should().Be("12345");
    }

    // ── Through a route ──

    private static async Task<(RouteContext Context, ProducerTemplate Template)> Route(Action<InlineRouteBuilder> build)
    {
        var context = new RouteContext($"attachments-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        context.AddRoutes(build);
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();
        return (context, template);
    }

    [Fact]
    public async Task A_step_that_writes_Out_without_attachments_leaves_those_of_In()
    {
        IExchange? seen = null;
        var (context, template) = await Route(r => r.From("direct://att-out")
            .AddAttachment("kept", new ConstantExpression("k"))
            .Process(e => e.Out = new Message("reply"))
            .Process(e => seen = e));
        await using var _ = context;

        await template.SendAsync("direct://att-out", "in");

        seen!.In.Body.Should().Be("reply");
        seen.In.Attachments.Select(a => a.Name).Should().Equal("kept");
    }

    [Fact]
    public async Task A_step_that_writes_Out_with_attachments_replaces_those_of_In()
    {
        IExchange? seen = null;
        var (context, template) = await Route(r => r.From("direct://att-out2")
            .AddAttachment("old", new ConstantExpression("o"))
            .Process(e =>
            {
                var reply = new Message("reply");
                reply.Attachments.Add(Bytes("new", "n"));
                e.Out = reply;
            })
            .Process(e => seen = e));
        await using var _ = context;

        await template.SendAsync("direct://att-out2", "in");

        seen!.In.Attachments.Select(a => a.Name).Should().Equal("new");
    }

    [Fact]
    public async Task Every_split_part_carries_the_attachments()
    {
        var parts = new List<string>();
        var (context, template) = await Route(r => r.From("direct://att-split")
            .AddAttachment("doc", new ConstantExpression("d"))
            .Split(e => new object?[] { "a", "b" })
                .Process(e => parts.Add($"{e.In.Body}:{e.In.Attachments.Count}"))
            .End());
        await using var _ = context;

        await template.SendAsync("direct://att-split", "in");

        parts.Should().Equal("a:1", "b:1");
    }

    [Fact]
    public async Task The_attachment_steps_add_from_an_expression_the_body_and_a_file_and_remove()
    {
        var path = Path.Combine(Path.GetTempPath(), $"att-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "from file");
        try
        {
            IExchange? seen = null;
            var (context, template) = await Route(r => r.From("direct://att-steps")
                .SetHeader("who", "anna")
                .AddAttachment("note", new StringExpression("${header.who}"), new StringExpression("${header.who}.txt"))
                .AddAttachment("literal", new ConstantExpression("x"), fileName: "${not.a.template}")
                .AddAttachmentFromBody("body.bin", contentType: "application/pdf")
                .AddAttachmentFromFile(path)
                .AddAttachment("gone", new ConstantExpression("x"))
                .RemoveAttachment("gone")
                .Process(e => seen = e));
            await using var _ = context;

            await template.SendAsync("direct://att-steps", "the body");

            var attachments = seen!.In.Attachments;
            attachments.Select(a => a.Name).Should().Equal("note", "literal", "body.bin", Path.GetFileName(path));
            Text(attachments[0]).Should().Be("anna");
            attachments[0].FileName.Should().Be("anna.txt");
            attachments[0].ContentType.Should().Be("text/plain; charset=utf-8");
            attachments[1].FileName.Should().Be("${not.a.template}", "a literal stays literal, whatever its text");
            Text(attachments[2]).Should().Be("the body");
            attachments[2].ContentType.Should().Be("application/pdf");
            Text(attachments[3]).Should().Be("from file");
            seen.In.Body.Should().Be("the body", "attaching the body leaves it in place");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveAttachments_clears_the_list()
    {
        IExchange? seen = null;
        var (context, template) = await Route(r => r.From("direct://att-clear")
            .AddAttachment("a", new ConstantExpression("1"))
            .AddAttachment("b", new ConstantExpression("2"))
            .RemoveAttachments()
            .Process(e => seen = e));
        await using var _ = context;

        await template.SendAsync("direct://att-clear", "in");

        seen!.In.Attachments.Should().BeEmpty();
    }

    // ── In the expression language ──

    private static IExchange WithAttachments()
    {
        var message = new Message("body");
        message.Attachments.Add(new Attachment
        {
            Name = "note",
            FileName = "note.txt",
            ContentType = "text/plain; charset=windows-1251",
            Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=note.txt" },
            Content = AttachmentContent.FromBytes(Encoding.GetEncoding(1251).GetBytes("привет"))
        });
        message.Attachments.Add(Bytes("scan", "%PDF", "scan.pdf", "application/pdf"));
        return new Exchange(message);
    }

    static MessageAttachmentsTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData("${attachments.size}", "2")]
    [InlineData("${attachments[1].fileName}", "scan.pdf")]
    [InlineData("${attachments[0].length}", "6")]
    [InlineData("${attachmentContentAsText('note')}", "привет")]
    [InlineData("${attachmentContentType('scan')}", "application/pdf")]
    [InlineData("${attachmentHeader('note', 'content-disposition')}", "attachment; filename=note.txt")]
    [InlineData("${attachmentContentType('missing') ?? 'none'}", "none")]
    public void Templates_read_attachments_with_the_Camel_Simple_names(string template, string expected)
    {
        ExpressionResolver.ProcessTemplate(template, WithAttachments()).Should().Be(expected);
    }

    [Fact]
    public void A_condition_and_a_value_read_attachments_too()
    {
        var exchange = WithAttachments();

        new StringExpression("attachments.size > 1").Evaluate<bool>(exchange).Should().BeTrue();
        new StringExpression("${attachmentContent('scan')}").Evaluate<byte[]>(exchange).Should().Equal("%PDF"u8.ToArray());
        new StringExpression("${attachments}").Evaluate<IMessageAttachments>(exchange).Should().HaveCount(2);
    }

    [Fact]
    public void The_interpreter_and_the_compiler_agree()
    {
        var exchange = WithAttachments();
        foreach (var expression in new[] { "attachmentContentAsText('note')", "attachmentContentType('scan')", "attachmentHeader('note', 'Content-Disposition')" })
        {
            var interpreted = new redb.Route.Expressions.Ast.Parser(new redb.Route.Expressions.Ast.Tokenizer(expression).GetAllTokens()).Parse().Evaluate(exchange);
            var compiled = new StringExpression("${" + expression + "}").Evaluate<object?>(exchange);
            compiled.Should().Be(interpreted, expression);
        }
    }

    private sealed class ForeignMessage : IMessage
    {
        public object? Body { get; set; }
        public string? ContentType { get; set; }
        public IDictionary<string, object?> Headers { get; } = new Dictionary<string, object?>();
        public T? GetHeader<T>(string key) => default;
        public IMessage Clone() => this;
        public IMessage Snapshot() => this;
    }
}
