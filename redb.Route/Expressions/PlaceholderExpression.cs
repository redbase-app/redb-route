using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Expressions;

/// <summary>
/// A text with <c>{{key}}</c> / <c>{{key:default}}</c> placeholders, resolved through the route context's configuration
/// (<c>IConfiguration</c>, then the context's properties) the way an endpoint URI is. The value a route sets from
/// configuration: <c>&lt;setHeader name="x" value="{{ldap.user}}"/&gt;</c> in markup, <c>new PlaceholderExpression("{{ldap.user}}")</c>
/// in C#. Resolved once per route context and then reused; a key with no value and no default fails the exchange with
/// the same message an endpoint URI gives.
/// </summary>
public sealed class PlaceholderExpression : Expression
{
    private (IRouteContext? Context, string Value)? _resolved;

    /// <summary>Creates the expression over <paramref name="text"/>.</summary>
    public PlaceholderExpression(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The text as written, placeholders included.</summary>
    public string Text { get; }

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        var value = Resolve(exchange.Context);
        if (value is T typed)
            return typed;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The text with its placeholders resolved through <paramref name="context"/>; without one, defaults only.</summary>
    public string Resolve(IRouteContext? context)
    {
        if (_resolved is { } cached && ReferenceEquals(cached.Context, context))
            return cached.Value;

        var value = context is RouteContext live
            ? live.ResolvePlaceholders(Text)
            : PropertyPlaceholderResolver.Resolve(Text, _ => null);
        _resolved = (context, value);
        return value;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a value from configuration is read-only.</exception>
    public override void SetValue(IExchange exchange, object value)
        => throw new NotSupportedException("A placeholder expression cannot be written to.");

    /// <inheritdoc />
    public override string ToTemplateString() => Text;
}
