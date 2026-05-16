using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Extensions;

/// <summary>
/// Fluent extensions for configuring message codecs on a <see cref="RedbRouteBuilder"/>.
/// <para>
/// These extensions operate on <see cref="IDataFormatRegistry"/>, which lives inside
/// <see cref="RouteContext"/> (not the DI container). Registration is deferred via
/// <see cref="IRouteContextConfigurator"/> so it is applied at context-start time.
/// </para>
/// </summary>
public static class RedbRouteCodecExtensions
{
    /// <summary>
    /// Replaces the default <c>application/json</c> codec in the route context's
    /// <see cref="IDataFormatRegistry"/> with one built from the supplied action.
    /// </summary>
    /// <param name="builder">Route builder.</param>
    /// <param name="configure">Action applied to a fresh <see cref="JsonSerializerOptions"/>
    /// instance (already seeded with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// and case-insensitive property matching). Mutate to taste.</param>
    /// <remarks>
    /// Profile-scoped codecs (SCIM, Problem Details, …) registered by other modules
    /// (e.g. <c>AddRedbIdentityServer</c>) are not affected — this tunes only the
    /// generic <c>application/json</c> entry.
    /// </remarks>
    public static RedbRouteBuilder ConfigureJsonCodec(
        this RedbRouteBuilder builder,
        Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        configure(options);

        builder.Services.AddSingleton<IRouteContextConfigurator>(
            new JsonCodecConfigurator(options));

        return builder;
    }

    /// <summary>
    /// Applies a bound <see cref="JsonCodecOptions"/> instance to the default
    /// <c>application/json</c> codec. Callers typically populate the options from
    /// <c>IConfiguration.GetSection("redbRoute:Codecs:Json").Get&lt;JsonCodecOptions&gt;()</c>
    /// in their host composition root.
    /// </summary>
    /// <param name="builder">Route builder.</param>
    /// <param name="options">Bound codec options; may not be <c>null</c>.</param>
    public static RedbRouteBuilder ConfigureJsonCodec(
        this RedbRouteBuilder builder,
        JsonCodecOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.ConfigureJsonCodec(jso =>
        {
            jso.PropertyNamingPolicy = ParseNamingPolicy(options.PropertyNamingPolicy);
            jso.PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive;
            jso.DefaultIgnoreCondition = options.IgnoreNullValues
                ? JsonIgnoreCondition.WhenWritingNull
                : JsonIgnoreCondition.Never;
            jso.WriteIndented = options.WriteIndented;
            jso.Encoder = options.UnsafeRelaxedEscaping
                ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                : JavaScriptEncoder.Default;
        });
    }

    private static JsonNamingPolicy? ParseNamingPolicy(string? name) => name switch
    {
        null => null,
        "" => null,
        _ when string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) => null,
        _ when string.Equals(name, "CamelCase", StringComparison.OrdinalIgnoreCase) => JsonNamingPolicy.CamelCase,
        _ when string.Equals(name, "SnakeCaseLower", StringComparison.OrdinalIgnoreCase) => JsonNamingPolicy.SnakeCaseLower,
        _ when string.Equals(name, "SnakeCaseUpper", StringComparison.OrdinalIgnoreCase) => JsonNamingPolicy.SnakeCaseUpper,
        _ when string.Equals(name, "KebabCaseLower", StringComparison.OrdinalIgnoreCase) => JsonNamingPolicy.KebabCaseLower,
        _ when string.Equals(name, "KebabCaseUpper", StringComparison.OrdinalIgnoreCase) => JsonNamingPolicy.KebabCaseUpper,
        _ => throw new ArgumentException(
            $"Unknown JSON naming policy '{name}'. Supported: None, CamelCase, SnakeCaseLower, SnakeCaseUpper, KebabCaseLower, KebabCaseUpper.")
    };

    private sealed class JsonCodecConfigurator : IRouteContextConfigurator
    {
        private readonly JsonSerializerOptions _options;

        public JsonCodecConfigurator(JsonSerializerOptions options) => _options = options;

        public void Configure(RouteContext context)
        {
            var registry = context.GetService<IDataFormatRegistry>()
                ?? throw new InvalidOperationException(
                    "IDataFormatRegistry is not available on RouteContext. " +
                    "Ensure AddRedbRoute() was called before ConfigureJsonCodec().");

            // Replace the generic application/json entry. Media-type aliases fan out
            // automatically via DataFormatRegistry.Register + MediaTypes contract.
            registry.Register("application/json", new JsonMessageSerializer(_options));
        }
    }
}
