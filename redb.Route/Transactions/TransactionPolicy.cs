using System.Transactions;

namespace redb.Route.Transactions;

/// <summary>
/// Defines the transactional behavior for a route or a segment of a route.
/// Wraps <see cref="TransactionScopeOption"/> and <see cref="TransactionOptions"/> with sensible defaults.
/// </summary>
public sealed class TransactionPolicy
{
    /// <summary>Default policy: <see cref="TransactionScopeOption.Required"/>, 30-second timeout.</summary>
    public static readonly TransactionPolicy Default = new();

    /// <summary>
    /// Policy that always creates a new transaction, suspending any ambient one.
    /// </summary>
    public static readonly TransactionPolicy RequiresNew = new()
    {
        ScopeOption = TransactionScopeOption.RequiresNew
    };

    /// <summary>
    /// Policy that suppresses the ambient transaction so that the inner code runs
    /// without any transaction context.
    /// </summary>
    public static readonly TransactionPolicy Suppress = new()
    {
        ScopeOption = TransactionScopeOption.Suppress
    };

    /// <summary>
    /// Policy that requires an ambient transaction to already exist; throws
    /// <see cref="InvalidOperationException"/> at scope creation time if there is none.
    /// Use this to enforce that a route segment can never run outside of a transaction.
    /// </summary>
    public static readonly TransactionPolicy Mandatory = new()
    {
        // System.Transactions has no Mandatory option directly; emulated via a custom
        // ScopeOption marker checked in CreateScope (see implementation below).
        ScopeOption = MandatoryScopeMarker
    };

    // Sentinel value outside the defined enum range used to mark Mandatory policies.
    // System.Transactions.TransactionScopeOption only defines Required, RequiresNew, Suppress.
    private const TransactionScopeOption MandatoryScopeMarker = (TransactionScopeOption)1000;

    /// <summary>
    /// Determines whether to join an existing ambient transaction, create a new one,
    /// or suppress it entirely. Default is <see cref="TransactionScopeOption.Required"/>.
    /// </summary>
    public TransactionScopeOption ScopeOption { get; init; } = TransactionScopeOption.Required;

    /// <summary>
    /// Transaction timeout. Default is 30 seconds. Use <see cref="Timeout.InfiniteTimeSpan"/>
    /// for long-running scenarios (not recommended for production).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Transaction isolation level. Default is <see cref="IsolationLevel.ReadCommitted"/>.
    /// </summary>
    public IsolationLevel IsolationLevel { get; init; } = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Creates a <see cref="TransactionScope"/> based on this policy with <see cref="TransactionScopeAsyncFlowOption.Enabled"/>.
    /// </summary>
    /// <returns>A configured transaction scope.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ScopeOption"/> is the Mandatory marker and no ambient
    /// <see cref="Transaction.Current"/> is present.
    /// </exception>
    internal TransactionScope CreateScope()
    {
        if (ScopeOption == MandatoryScopeMarker)
        {
            if (Transaction.Current is null)
            {
                throw new InvalidOperationException(
                    "TransactionPolicy.Mandatory requires an ambient System.Transactions.Transaction " +
                    "to be present, but Transaction.Current was null.");
            }

            // Join the existing ambient transaction. We deliberately omit TransactionOptions
            // here because System.Transactions throws ArgumentException when the requested
            // IsolationLevel differs from the ambient transaction's level. Mandatory honors
            // the ambient transaction's isolation level by definition.
            return new TransactionScope(
                TransactionScopeOption.Required,
                TransactionScopeAsyncFlowOption.Enabled);
        }

        var options = new TransactionOptions
        {
            Timeout = Timeout,
            IsolationLevel = IsolationLevel
        };

        return new TransactionScope(
            ScopeOption,
            options,
            TransactionScopeAsyncFlowOption.Enabled);
    }

    /// <summary>
    /// Parses a well-known policy name into a <see cref="TransactionPolicy"/> instance.
    /// Supported values: <c>"Required"</c>, <c>"RequiresNew"</c>, <c>"Suppress"</c>, <c>"Mandatory"</c>.
    /// </summary>
    /// <param name="name">Case-insensitive policy name.</param>
    /// <returns>Matching policy.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is not recognized.</exception>
    public static TransactionPolicy FromName(string name) =>
        name?.Trim().ToUpperInvariant() switch
        {
            "REQUIRED" => Default,
            "REQUIRESNEW" => RequiresNew,
            "SUPPRESS" => Suppress,
            "MANDATORY" => Mandatory,
            _ => throw new ArgumentException($"Unknown transaction policy name: '{name}'. Expected: Required, RequiresNew, Suppress, Mandatory.", nameof(name))
        };
}
