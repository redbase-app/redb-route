using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using Scriban;
using Scriban.Parsing;

namespace redb.Route.Templates;

/// <summary>A parsed template plus the source it came from.</summary>
internal sealed record CompiledTemplate(TextSource Source, Template Template);

/// <summary>
/// Compiles templates once (cache keyed by source identity and syntax) and renders them per message.
/// Compilation errors become <see cref="TemplateCompilationException"/> with the template name and
/// the first error's position; they are raised at route build, i.e. from <c>Start()</c>.
/// </summary>
internal static class TemplateEngine
{
    private static readonly ConcurrentDictionary<string, Lazy<CompiledTemplate>> Cache = new(StringComparer.Ordinal);
    private static int _compileCount;

    /// <summary>Number of actual parses so far — lets a test prove the cache (two routes, one file, one parse).</summary>
    public static int CompileCount => Volatile.Read(ref _compileCount);

    /// <summary>Drops every cached template (tests).</summary>
    public static void ClearCache() => Cache.Clear();

    public static CompiledTemplate Compile(TextSource source, RouteTemplateOptions options)
    {
        // Keyed by the resolved file (path, size, write time) and the syntax: the cache is process-wide,
        // and a relative name alone would make two base directories share one template.
        var key = (options.Liquid ? "liquid|" : "scriban|") + source.ResolveCacheKey(options.BaseDirectory);
        var lazy = Cache.GetOrAdd(key, _ => new Lazy<CompiledTemplate>(() => Parse(source, options), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Cache.TryRemove(key, out _);   // do not cache a failure: the file may be fixed and the context restarted
            throw;
        }
    }

    private static CompiledTemplate Parse(TextSource source, RouteTemplateOptions options)
    {
        string text;
        try
        {
            text = source.Read(options.BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TemplateCompilationException(source.Name, null, null, ex.Message, ex);
        }

        Interlocked.Increment(ref _compileCount);
        var template = options.Liquid
            ? Template.ParseLiquid(text, source.Name)
            : Template.Parse(text, source.Name);

        if (template.HasErrors)
        {
            var first = template.Messages.FirstOrDefault(m => m.Type == ParserMessageType.Error) ?? template.Messages.First();
            var all = string.Join("; ", template.Messages.Select(m => m.Message));
            throw new TemplateCompilationException(source.Name, first.Span.Start.Line + 1, first.Span.Start.Column + 1, all);
        }

        return new CompiledTemplate(source, template);
    }

    public static string Render(CompiledTemplate compiled, IExchange exchange, MediaType mediaType, TemplateArgs? args, RouteTemplateOptions options)
    {
        TemplateContext context = options.Liquid
            ? new LiquidPayloadTemplateContext(mediaType, options)
            : new PayloadTemplateContext(mediaType, options);
        // Arguments and expr() evaluate route-language expressions written as data: inside the sandbox
        // they read members but can never invoke a method on the objects they reach.
        using var sandbox = redb.Route.Expressions.ExpressionSandbox.Enter();
        var evaluatedArgs = args?.Evaluate(exchange) ?? new Dictionary<string, object?>();
        context.PushGlobal(TemplateModel.Build(exchange, evaluatedArgs));
        return compiled.Template.Render(context);
    }
}
