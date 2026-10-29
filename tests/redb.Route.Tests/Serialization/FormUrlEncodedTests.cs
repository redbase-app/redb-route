using System.Text;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// <c>application/x-www-form-urlencoded</c> as a format registered by default: what a browser posts from an HTML form,
/// parsed and written by the WHATWG rules browsers and ASP.NET follow.
/// </summary>
public class FormUrlEncodedTests
{
    private static readonly FormUrlEncodedMessageSerializer Format = new();

    private static FormBody Parse(string wire) => Format.Deserialize<FormBody>(Encoding.ASCII.GetBytes(wire))!;

    [Fact]
    public void A_browser_form_reads_with_repeated_names_in_order()
    {
        var form = Parse("claim=12345&theme=%D0%BF%D1%80%D0%B5%D1%82%D0%B5%D0%BD%D0%B7%D0%B8%D1%8F&tag=a&tag=b&note=two+words");

        form.Get("claim").Should().Be("12345");
        form.Get("theme").Should().Be("претензия");
        form.GetAll("tag").Should().Equal("a", "b");
        form.Get("note").Should().Be("two words", "'+' is a space in form data");
        form.Select(f => f.Key).Should().Equal("claim", "theme", "tag", "tag", "note");
    }

    [Fact]
    public void Edge_forms_follow_the_WHATWG_parser()
    {
        var form = Parse("&a=&b&c=1=2&d=%zz&e=%2");

        form.Get("a").Should().Be("");
        form.Get("b").Should().Be("", "a name without '=' has an empty value");
        form.Get("c").Should().Be("1=2", "only the first '=' splits");
        form.Get("d").Should().Be("%zz", "a '%' without two hex digits stays as it is");
        form.Get("e").Should().Be("%2");
        form.Count.Should().Be(5, "an empty pair is skipped");
    }

    [Fact]
    public void Writing_encodes_as_a_browser_does_and_reads_back()
    {
        var form = new FormBody().Add("q", "a b&c=d").Add("name", "Анна").Add("safe", "*-._");

        var wire = Encoding.ASCII.GetString(Format.Serialize(form));

        wire.Should().Be("q=a+b%26c%3Dd&name=%D0%90%D0%BD%D0%BD%D0%B0&safe=*-._");
        Parse(wire).Should().Equal(form);
    }

    [Fact]
    public void A_dictionary_reads_the_first_value_and_writes_like_a_form()
    {
        Format.Deserialize<Dictionary<string, string>>("a=1&a=2&b=3"u8.ToArray())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1", ["b"] = "3" });

        Encoding.ASCII.GetString(Format.Serialize(new Dictionary<string, string> { ["x"] = "1 2" })).Should().Be("x=1+2");
    }

    [Fact]
    public void Another_target_or_source_type_is_refused_with_the_format_named()
    {
        var read = () => Format.Deserialize<int>("a=1"u8.ToArray());
        var write = () => Format.Serialize(42);

        read.Should().Throw<NotSupportedException>().WithMessage("Form data cannot be read as*");
        write.Should().Throw<NotSupportedException>().WithMessage("Form data cannot be written*");
    }

    [Fact]
    public void Limits_hold_wherever_the_form_came_from()
    {
        var small = new FormUrlEncodedMessageSerializer(o => { o.MaxFields = 2; o.MaxValueLength = 3; });

        var tooMany = () => small.Deserialize<FormBody>("a=1&b=2&c=3"u8.ToArray());
        var tooLong = () => small.Deserialize<FormBody>("a=1234"u8.ToArray());

        tooMany.Should().Throw<InvalidDataException>().WithMessage("*more than 2 fields*");
        tooLong.Should().Throw<InvalidDataException>().WithMessage("*longer than 3*");
    }

    [Fact]
    public void Another_encoding_is_an_option_of_the_instance()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = new FormUrlEncodedMessageSerializer(o => o.Encoding = Encoding.GetEncoding(1251));

        cp1251.Deserialize<FormBody>("n=%C0%ED%ED%E0"u8.ToArray())!.Get("n").Should().Be("Анна");
    }

    [Fact]
    public async Task ConvertBody_reads_a_posted_form_through_the_default_registration()
    {
        await using var context = new RouteContext($"form-{Guid.NewGuid():N}");
        context.AddComponent(new redb.Route.Components.DirectComponent());
        FormBody? seen = null;
        context.AddRoutes(r => r.From("direct://form-in")
            .ConvertBody<FormBody>()
            .Process(e => seen = (FormBody)e.In.Body!));
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();

        var message = new Message("claim=7&tag=a&tag=b"u8.ToArray()) { ContentType = "application/x-www-form-urlencoded" };
        await template.SendAsync("direct://form-in", message);

        seen!.Get("claim").Should().Be("7");
        seen.GetAll("tag").Should().Equal("a", "b");
    }

    [Fact]
    public void FormBody_clones_for_a_checkpoint()
    {
        var form = new FormBody().Add("a", "1");
        var copy = (FormBody)form.Clone();
        copy.Add("b", "2");

        form.Should().ContainSingle();
    }
}

/// <summary>
/// A format that implements <see cref="IMessageDataFormat"/> gets the whole message from <c>Marshal</c>,
/// <c>Unmarshal</c> and <c>ConvertBody</c>: the content type with its parameters, the body as it came, the attachments.
/// </summary>
public class MessageDataFormatTests
{
    /// <summary>Records what it was given and writes a parameterised content type, as MIME multipart will.</summary>
    private sealed class ProbeMessageFormat : IMessageDataFormat
    {
        public string? SeenContentType;
        public object? SeenBody;

        public string ContentType => "application/x-probe";
        public byte[] Serialize<T>(T value) => throw new NotSupportedException("needs the message");
        public T? Deserialize<T>(byte[] data) => throw new NotSupportedException("needs the message");
        public object? Deserialize(byte[] data, Type type) => throw new NotSupportedException("needs the message");

        public ValueTask UnmarshalAsync(IExchange exchange, Type targetType, CancellationToken ct = default)
        {
            var message = exchange.In;
            SeenContentType = message.ContentType;
            SeenBody = message.Body;
            message.Body = $"model of {targetType.Name}";
            message.Attachments.Add(new Attachment { Name = "part", Content = AttachmentContent.FromBytes([1, 2]) });
            return ValueTask.CompletedTask;
        }

        public ValueTask MarshalAsync(IExchange exchange, CancellationToken ct = default)
        {
            var message = exchange.In;
            message.Body = Encoding.UTF8.GetBytes($"{message.Body}+{message.Attachments.Count}");
            message.ContentType = "application/x-probe; boundary=b1";
            message.Headers["Content-Type"] = message.ContentType;
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(RouteContext, ProducerTemplate)> Route(ProbeMessageFormat format, Action<InlineRouteBuilder> build)
    {
        var context = new RouteContext($"msgfmt-{Guid.NewGuid():N}");
        context.AddComponent(new redb.Route.Components.DirectComponent());
        context.AddDataFormat(format);
        context.AddRoutes(build);
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();
        return (context, template);
    }

    [Fact]
    public async Task Unmarshal_hands_the_format_the_stream_and_the_content_type_parameters()
    {
        var format = new ProbeMessageFormat();
        IExchange? seen = null;
        var (context, template) = await Route(format, r => r.From("direct://mf-un")
            .Unmarshal<string>("application/x-probe")
            .Process(e => seen = e));
        await using var _ = context;

        var stream = new MemoryStream([9]);
        await template.SendAsync("direct://mf-un", new Message(stream) { ContentType = "application/x-probe; boundary=abc" });

        format.SeenContentType.Should().Be("application/x-probe; boundary=abc");
        format.SeenBody.Should().BeSameAs(stream, "the step does not buffer the body for a message format");
        seen!.In.Body.Should().Be("model of String");
        seen.In.Attachments.Should().ContainSingle();
    }

    [Fact]
    public async Task ConvertBody_to_a_model_uses_the_registered_message_format()
    {
        var format = new ProbeMessageFormat();
        IExchange? seen = null;
        var (context, template) = await Route(format, r => r.From("direct://mf-cb")
            .ConvertBody<List<int>>()
            .Process(e => seen = e));
        await using var _ = context;

        await template.SendAsync("direct://mf-cb", new Message("raw") { ContentType = "application/x-probe; boundary=x" });

        seen!.In.Body.Should().Be("model of List`1");
    }

    [Fact]
    public async Task Marshal_keeps_the_content_type_the_format_wrote()
    {
        var format = new ProbeMessageFormat();
        IExchange? seen = null;
        var (context, template) = await Route(format, r => r.From("direct://mf-m")
            .AddAttachment("a", new redb.Route.Expressions.ConstantExpression("x"))
            .Marshal("application/x-probe")
            .Process(e => seen = e));
        await using var _ = context;

        await template.SendAsync("direct://mf-m", "model");

        Encoding.UTF8.GetString((byte[])seen!.In.Body!).Should().Be("model+1");
        seen.In.ContentType.Should().Be("application/x-probe; boundary=b1");
        seen.In.Headers["Content-Type"].Should().Be("application/x-probe; boundary=b1");
    }
}
