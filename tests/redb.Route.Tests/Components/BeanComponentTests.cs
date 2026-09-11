using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

// ── Public bean targets (bean: works through exported types only) ────────────

/// <summary>Sync bean with one suitable method returning a value.</summary>
public class GreeterBean
{
    public static int Instances;
    public GreeterBean() => Interlocked.Increment(ref Instances);

    /// <summary>Bound from the URI query.</summary>
    public string Prefix { get; set; } = "hello";

    public string Greet(IExchange exchange) => $"{Prefix}:{exchange.In.Body}";
}

/// <summary>Bean with several suitable methods — method= is mandatory.</summary>
public class MultiBean
{
    public void First(IExchange exchange) => exchange.In.Headers["called"] = "first";
    public void Second(IExchange exchange) => exchange.In.Headers["called"] = "second";
}

/// <summary>Async bean: Task and Task&lt;T&gt; shapes, with the (IExchange, ct) priority form.</summary>
public class AsyncBean
{
    public async Task<string> Handle(IExchange exchange, CancellationToken ct)
    {
        await Task.Yield();
        return $"async:{exchange.In.Body}";
    }

    public async Task Touch(IExchange exchange)
    {
        await Task.Yield();
        exchange.In.Headers["touched"] = true;
    }
}

/// <summary>Void bean: the body must stay untouched.</summary>
public class VoidBean
{
    public void Mark(IExchange exchange) => exchange.In.Headers["marked"] = true;
}

/// <summary>Bean with a secret-bearing property: the key must join the URI redaction set.</summary>
public class SecretBean
{
    [Sensitive]
    public string? ApiKey { get; set; }

    public void Call(IExchange exchange) => exchange.In.Headers["apiKeyLength"] = ApiKey?.Length ?? 0;
}

internal class InternalBean
{
    public void Handle(IExchange exchange) { }
}

/// <summary>Ф1.6 param-binding targets: ordinary typed parameters, overloads, ct, async.</summary>
public class CalcBean
{
    public int Add(int a, int b) => a + b;
    public string Add(string a, string b, string c) => a + b + c;      // другой счёт аргументов
    public string Upper(string text) => text.ToUpperInvariant();
    public async Task<string> Fetch(string id, CancellationToken ct)
    {
        await Task.Yield();
        return $"row-{id}";
    }
    public int Answer() => 42;
}

/// <summary>
/// Route-XML Ф1.2: the <c>bean:</c> component. Both URI forms, method selection, return-value
/// semantics, property binding through the shared converter, [Sensitive] redaction, instance
/// caching by normalized URI, and the loud errors the plan demands.
/// </summary>
public class BeanComponentTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Q(Type t) => $"{t.FullName}, {t.Assembly.GetName().Name}";

    private static IExchange Msg(object? body, params (string Name, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body));
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    /// <summary>
    /// To-endpoints are created lazily, on the first message (engine-wide behaviour, not a bean:
    /// special case; early detection is the packaging checks' and the XML loader's job). The error
    /// paths therefore fire on the first send.
    /// </summary>
    private async Task<Func<Task>> FirstSend(string fromUri)
    {
        var producer = await StartAndProducer(fromUri);
        return () => producer.Process(new Exchange(new Message("x")));
    }

    // ── Registry form ────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistryForm_InvokesTheRegisteredObject_AndResultBecomesTheBody()
    {
        _context.AddToRegistry("greeter", new GreeterBean { Prefix = "reg" });
        _context.AddRoutes(r => r.From("direct://bean-reg").To("bean:#greeter?method=Greet"));
        var producer = await StartAndProducer("direct://bean-reg");

        var exchange = new Exchange(new Message("bob"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("reg:bob");
    }

    [Fact]
    public async Task RegistryForm_MissingObject_FailsTheFirstSendNamingTheReference()
    {
        _context.AddRoutes(r => r.From("direct://bean-regx").To("bean:#absent?method=Greet"));
        var act = await FirstSend("direct://bean-regx");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*#absent*not in the context registry*");
    }

    // ── Type form ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TypeForm_CreatesInstance_BindsProperties_AndResultBecomesTheBody()
    {
        _context.AddRoutes(r => r.From("direct://bean-type")
            .To($"bean:{Q(typeof(GreeterBean))}?method=Greet&prefix=hi"));
        var producer = await StartAndProducer("direct://bean-type");

        var exchange = new Exchange(new Message("ann"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("hi:ann");
    }

    [Fact]
    public async Task TypeForm_MethodOptional_WhenExactlyOneSuitableMethod()
    {
        _context.AddRoutes(r => r.From("direct://bean-single")
            .To($"bean:{Q(typeof(VoidBean))}"));
        var producer = await StartAndProducer("direct://bean-single");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Headers.Should().ContainKey("marked");
        exchange.In.Body.Should().Be("x", "void must not touch the body");
    }

    [Fact]
    public async Task TypeForm_AmbiguousMethods_ErrorListsTheCandidates()
    {
        _context.AddRoutes(r => r.From("direct://bean-multi")
            .To($"bean:{Q(typeof(MultiBean))}"));
        var act = await FirstSend("direct://bean-multi");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*several suitable methods*First*Second*");
    }

    [Fact]
    public async Task TypeForm_VersionPinnedName_IsRefusedWithExplanation()
    {
        _context.AddRoutes(r => r.From("direct://bean-ver")
            .To("bean:Acme.X, Acme, Version=1.0.0.0, PublicKeyToken=null?method=Handle"));
        var act = await FirstSend("direct://bean-ver");

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*pins an assembly version*");
    }

    [Fact]
    public async Task TypeForm_NonPublicType_GetsItsOwnMessage()
    {
        _context.AddRoutes(r => r.From("direct://bean-int")
            .To($"bean:{Q(typeof(InternalBean))}"));
        var act = await FirstSend("direct://bean-int");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*found but is not public*");
    }

    [Fact]
    public async Task TypeForm_UnknownType_SaysNotFound()
    {
        _context.AddRoutes(r => r.From("direct://bean-unk")
            .To("bean:Acme.No.Such.Type, Acme.Missing"));
        var act = await FirstSend("direct://bean-unk");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*was not found in the loaded assemblies*");
    }

    [Fact]
    public async Task TypeForm_UnknownProperty_ErrorNamesTheWritableOnes()
    {
        _context.AddRoutes(r => r.From("direct://bean-prop")
            .To($"bean:{Q(typeof(GreeterBean))}?method=Greet&nosuch=1"));
        var act = await FirstSend("direct://bean-prop");

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*no writable public property 'nosuch'*Prefix*");
    }

    // ── Async shapes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncTaskOfT_AwaitsAndPutsTheResultIntoTheBody()
    {
        _context.AddRoutes(r => r.From("direct://bean-async")
            .To($"bean:{Q(typeof(AsyncBean))}?method=Handle"));
        var producer = await StartAndProducer("direct://bean-async");

        var exchange = new Exchange(new Message("m"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("async:m");
    }

    [Fact]
    public async Task AsyncTask_AwaitsAndLeavesTheBody()
    {
        _context.AddRoutes(r => r.From("direct://bean-task")
            .To($"bean:{Q(typeof(AsyncBean))}?method=Touch"));
        var producer = await StartAndProducer("direct://bean-task");

        var exchange = new Exchange(new Message("m"));
        await producer.Process(exchange);

        exchange.In.Headers.Should().ContainKey("touched");
        exchange.In.Body.Should().Be("m");
    }

    // ── Lifetime and redaction ───────────────────────────────────────────────

    [Fact]
    public async Task SameUri_SharesOneInstance_DifferentParametersGetTheirOwn()
    {
        var before = GreeterBean.Instances;
        _context.AddRoutes(r => r.From("direct://bean-life")
            .To($"bean:{Q(typeof(GreeterBean))}?method=Greet&prefix=a")
            .To($"bean:{Q(typeof(GreeterBean))}?method=Greet&prefix=a")
            .To($"bean:{Q(typeof(GreeterBean))}?method=Greet&prefix=b"));
        var producer = await StartAndProducer("direct://bean-life");

        await producer.Process(new Exchange(new Message("x")));

        (GreeterBean.Instances - before).Should().Be(2,
            "equal URIs share one endpoint (and instance); a different parameter set is another endpoint");
    }

    // ── Ф1.6: Camel-style parameter binding — method=name(arg, ...) ──────────

    [Fact]
    public async Task Binding_TypedParameters_FromHeaderExpressions()
    {
        _context.AddRoutes(r => r.From("direct://bean-bind")
            .To($"bean:{Q(typeof(CalcBean))}?method=Add(${{header.a}}, ${{header.b}})"));
        var producer = await StartAndProducer("direct://bean-bind");

        var exchange = Msg("x", ("a", 40), ("b", 2));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be(42);
    }

    [Fact]
    public async Task Binding_BodyExpression_ConvertsToTheParameterType()
    {
        _context.AddRoutes(r => r.From("direct://bean-bindb")
            .To($"bean:{Q(typeof(CalcBean))}?method=Upper(${{body}})"));
        var producer = await StartAndProducer("direct://bean-bindb");

        var exchange = Msg("hello");
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("HELLO");
    }

    [Fact]
    public async Task Binding_OverloadPickedByArgumentCount()
    {
        _context.AddRoutes(r => r.From("direct://bean-bind3")
            .To($"bean:{Q(typeof(CalcBean))}?method=Add(${{header.a}}, '-', ${{header.b}})"));
        var producer = await StartAndProducer("direct://bean-bind3");

        var exchange = Msg("x", ("a", "left"), ("b", "right"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("left-right");
    }

    [Fact]
    public async Task Binding_TrailingCancellationToken_IsFilledAutomatically()
    {
        _context.AddRoutes(r => r.From("direct://bean-bindct")
            .To($"bean:{Q(typeof(CalcBean))}?method=Fetch(${{header.id}})"));
        var producer = await StartAndProducer("direct://bean-bindct");

        var exchange = Msg("x", ("id", 7));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("row-7");
    }

    [Fact]
    public async Task Binding_EmptyArgumentList_CallsAParameterlessMethod()
    {
        _context.AddRoutes(r => r.From("direct://bean-bind0")
            .To($"bean:{Q(typeof(CalcBean))}?method=Answer()"));
        var producer = await StartAndProducer("direct://bean-bind0");

        var exchange = Msg("x");
        await producer.Process(exchange);

        exchange.In.Body.Should().Be(42);
    }

    [Fact]
    public async Task Binding_WrongArgumentCount_ErrorSaysSo()
    {
        _context.AddRoutes(r => r.From("direct://bean-bindx")
            .To($"bean:{Q(typeof(CalcBean))}?method=Upper(${{body}}, ${{header.a}})"));
        var act = await FirstSend("direct://bean-bindx");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no public method 'Upper' taking 2 argument*");
    }

    [Fact]
    public async Task Binding_NullIntoValueTypeParameter_FailsWithTheParameterName()
    {
        _context.AddRoutes(r => r.From("direct://bean-bindn")
            .To($"bean:{Q(typeof(CalcBean))}?method=Add(${{header.missing}}, ${{header.b}})"));
        var producer = await StartAndProducer("direct://bean-bindn");

        var act = () => producer.Process(Msg("x", ("b", 2)));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*parameter 'a'*value type*");
    }

    [Fact]
    public async Task SensitiveProperty_JoinsTheUriRedactionSet()
    {
        _context.AddRoutes(r => r.From("direct://bean-secret")
            .To($"bean:{Q(typeof(SecretBean))}?apiKey=super-secret-123"));
        var producer = await StartAndProducer("direct://bean-secret");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Headers["apiKeyLength"].Should().Be("super-secret-123".Length);
        EndpointUri.IsSensitiveKey("ApiKey").Should().BeTrue("[Sensitive] on a bean property must redact the URI parameter");
    }
}
