namespace redb.Route.Abstractions;

/// <summary>
/// Defines the message exchange pattern for a route.
/// Determines how In/Out messages are used during processing.
/// </summary>
public enum ExchangePattern
{
    /// <summary>
    /// Fire-and-forget. Producer result is written to In.
    /// Out remains null. Default pattern for most routes.
    /// </summary>
    InOnly = 0,

    /// <summary>
    /// Request-reply. Original message preserved in In,
    /// response written to Out. Used for enrichment scenarios.
    /// </summary>
    InOut = 1,

    /// <summary>
    /// Explicit response. Created by DSL .Respond().
    /// RPC reply is taken from Out.
    /// </summary>
    OutOnly = 2
}
