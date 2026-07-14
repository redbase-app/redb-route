using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql.Connection;

namespace redb.Route.Sql;

/// <summary>
/// SQL component. Scheme: <c>sql</c>.
/// Provides database polling (consumer) and query execution (producer)
/// over any ADO.NET provider via <see cref="System.Data.Common.DbProviderFactory"/>.
/// <para>
/// URI examples:
/// <list type="bullet">
///   <item><c>sql:SELECT * FROM orders?mode=Poll&amp;dataSource=main&amp;delay=5000</c></item>
///   <item><c>sql:INSERT INTO audit(msg) VALUES(@msg)?dataSource=main</c></item>
///   <item><c>sql:sp_Calculate?mode=Procedure&amp;dataSource=main</c></item>
/// </list>
/// </para>
/// </summary>
public class SqlComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "sql";

    /// <summary>Gets or sets the named query registry for resolving ref: queries.</summary>
    internal ISqlNamedQueryRegistry? NamedQueryRegistry { get; set; }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SqlEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // In Procedure mode the URI path IS the procedure name (same as the query text is in the
        // other modes). Without this, every procedure URI had to repeat the name in procedureName=,
        // and the fluent builder — which only ever emits the path — produced a URI that failed
        // validation. An explicit procedureName= still wins.
        if (options.Mode == SqlMode.Procedure && string.IsNullOrEmpty(options.ProcedureName))
            options.ProcedureName = uri.Path;

        options.Validate();

        return new SqlEndpoint(uri, this, options);
    }
}

/// <summary>
/// SQL endpoint. Creates a consumer (polling) or producer (execute/procedure)
/// based on the <see cref="SqlMode"/> in options.
/// The path part of the URI is the SQL query or stored procedure name.
/// </summary>
public class SqlEndpoint : EndpointBase<SqlEndpointOptions>
{
    /// <summary>Creates a SQL endpoint.</summary>
    /// <param name="uri">Parsed endpoint URI.</param>
    /// <param name="component">The SQL component that created this endpoint.</param>
    /// <param name="options">Bound and validated endpoint options.</param>
    public SqlEndpoint(EndpointUri uri, SqlComponent component, SqlEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>Gets the SQL component that owns this endpoint.</summary>
    internal SqlComponent SqlComponent => (SqlComponent)Component;

    /// <summary>Gets the endpoint options for access by consumer/producer.</summary>
    internal SqlEndpointOptions EndpointOptions => Options;

    /// <summary>
    /// The SQL query or procedure name from the URI path.
    /// This is the raw text after <c>sql:</c> and before <c>?</c>.
    /// </summary>
    public string QueryOrProcedure => Uri.Path;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return Options.Mode switch
        {
            SqlMode.Procedure => new SqlProcedureProducer(this, Options),
            _ => new SqlProducer(this, Options)
        };
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);

        if (Options.Mode != SqlMode.Poll)
            throw new InvalidOperationException(
                $"SQL consumer requires Mode=Poll, but Mode={Options.Mode}. " +
                "Use Sql.Poll() or add mode=Poll to the URI.");

        return new SqlConsumer(this, processor, Options);
    }

    /// <summary>
    /// Resolves <see cref="ISqlConnectionFactory"/> using registry lookup:
    /// <list type="number">
    ///   <item>Context registry: <c>context.GetFromRegistry&lt;ISqlConnectionFactory&gt;(dataSource)</c></item>
    ///   <item>Inline <c>connectionString</c> from the URI</item>
    /// </list>
    /// </summary>
    internal ISqlConnectionFactory ResolveConnectionFactory()
    {
        if (Options.DataSource != null)
        {
            var factory = SqlComponent.Context?.GetFromRegistry<ISqlConnectionFactory>(Options.DataSource);
            if (factory != null)
                return factory;

            throw new InvalidOperationException(
                $"DataSource '{Options.DataSource}' is not found in context registry. " +
                "Register it via context.AddToRegistry(name, ISqlConnectionFactory).");
        }

        if (Options.ConnectionString != null)
        {
            return new SqlConnectionFactory(new SqlConnectionOptions
            {
                ConnectionString = Options.ConnectionString,
                ProviderName = Options.Provider ?? string.Empty
            });
        }

        throw new InvalidOperationException("Either DataSource or ConnectionString must be configured.");
    }
}
