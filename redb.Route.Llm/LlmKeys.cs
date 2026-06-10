namespace redb.Route.Llm;

/// <summary>
/// Property keys placed on <c>IExchange.Properties</c> by the LLM connector
/// (vs. <see cref="LlmHeaders"/>, which is the wire-format header surface).
/// Properties live for the lifetime of one exchange and are not serialised to
/// transport headers; they are the right place for cross-cutting per-exchange
/// configuration that storage layers / engine plumbing read on the way through.
/// </summary>
public static class LlmKeys
{
    /// <summary>
    /// Name of the named <see cref="redb.Core.IRedbService"/> instance the LLM
    /// stores should write to for this exchange. Set by <see cref="LlmProducer"/>
    /// from <see cref="LlmEndpointOptions.Redb"/> (i.e. <c>?redb=my-llm-db</c> on the URI).
    /// When null/empty, stores fall back to the default (unnamed) <c>IRedbService</c>
    /// from the route context — typically the host-wide instance Tsak/your app
    /// already registered via <c>services.AddRedb()</c>.
    /// </summary>
    public const string RedbName = "llm.redb.name";
}
