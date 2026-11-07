namespace redb.Route.Core;

/// <summary>
/// Marks an endpoint option as a credential, so its value is redacted wherever the endpoint URI is
/// rendered — logs, OpenTelemetry tags, metric labels, health checks, and the Tsak dashboard.
/// <para>
/// This is the <b>source of truth</b> for what counts as a secret: masking is driven by an explicit
/// declaration on the property rather than by guessing from the parameter name. The name-keyword
/// heuristic in <see cref="Abstractions.EndpointUri.IsSensitiveKey"/> remains only as a backstop for
/// URIs whose options type has not been bound yet.
/// </para>
/// <para>
/// The mechanism mirrors Apache Camel, where <c>@UriParam(secret = true)</c> on the option field is
/// the declaration and the runtime keyword list (<c>SensitiveUtils.SENSITIVE_KEYS</c>) is generated
/// from those annotations rather than hand-maintained. Camel harvests them with a build plugin;
/// here <see cref="EndpointOptions.BindFromUri"/> harvests them by reflection, once per options
/// type, and feeds <see cref="Abstractions.EndpointUri.AddSensitiveKeys"/>. Either way the list is
/// never written by hand, so a new credential option cannot be forgotten.
/// </para>
/// <example>
/// <code>
/// public sealed class LdapEndpointOptions : EndpointOptions
/// {
///     public string Server { get; set; } = "localhost";      // printed in logs
///
///     [Sensitive]
///     public string? BindPassword { get; set; }              // always rendered as ****
/// }
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SensitiveAttribute : Attribute
{
}
