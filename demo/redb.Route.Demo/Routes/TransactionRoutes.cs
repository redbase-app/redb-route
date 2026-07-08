using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using static redb.Route.Demo.Routes.DemoEndpoints;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Transaction showcase: declarative <c>Transacted()</c> wrapping and
/// imperative <c>BeginTransaction/CommitTransaction/RollbackTransaction</c>
/// inside DoTry/DoCatch.
/// </summary>
internal sealed class TransactionRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public TransactionRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureTransactedRoute();
        ConfigureImperativeTxRoute();
    }

    /// <summary>
    /// Transacted() — wraps the entire route in a TransactionScope.
    /// All SQL operations participate in one distributed transaction.
    /// </summary>
    private void ConfigureTransactedRoute()
    {
        From("direct://demo-transacted")
            .RouteId("demo-transacted")
            .Log("[TX-AUTO] ▶ Entering transacted route...")
            .Transacted()
            .Log("[TX-AUTO]   → SQL INSERT inside auto-transaction...")
            .To(SqlInsert)
            .Log("[TX-AUTO]   → VM audit inside auto-transaction...")
            .To("vm://audit-log")
            .Log("[TX-AUTO] ◀ Route complete → auto-commit");
    }

    /// <summary>
    /// BeginTransaction / CommitTransaction / RollbackTransaction —
    /// imperative control. You decide when to commit or rollback.
    /// DoTry/DoCatch wraps the transaction for safety.
    /// </summary>
    private void ConfigureImperativeTxRoute()
    {
        From("direct://demo-imperative-tx")
            .RouteId("demo-imperative-tx")
            .Log("[TX-IMP] ▶ Starting imperative transaction...")

            .BeginTransaction()
            .Log("[TX-IMP]   → Transaction opened")

            .DoTry()
                .Log("[TX-IMP]   → SQL INSERT #1...")
                .To(SqlInsert)
                .Log("[TX-IMP]   → SQL INSERT #2...")
                .To(SqlInsert)
                .CommitTransaction()
                .Log("[TX-IMP]   ✔ Both inserts committed!")

            .DoCatch<Exception>()
                .Log("[TX-IMP]   ✖ Error: ${exception.message}")
                .RollbackTransaction()
                .Log("[TX-IMP]   ✖ Transaction rolled back")

            .End()

            .Log("[TX-IMP] ◀ Done");
    }
}
