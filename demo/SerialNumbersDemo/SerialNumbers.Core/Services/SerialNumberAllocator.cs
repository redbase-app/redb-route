using redb.Core;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core.Services;

/// <summary>
/// Serial numbers and the quota ledger, as plain SQL through <c>redb.Context</c>. It runs on the
/// same connection and inside the same transaction as the redb objects of the request, so the
/// numbers, the ledger row and the response commit or roll back together.
/// <para>
/// Parameters are written <c>$1</c>, <c>$2</c>...: redb maps them to the provider's own syntax.
/// </para>
/// </summary>
public static class SerialNumberAllocator
{
    /// <summary>
    /// Quantity already allocated for the GTIN this year. The range lock (UPDLOCK, HOLDLOCK) holds
    /// until the transaction ends, so two requests for one product cannot both pass the quota check.
    /// </summary>
    public static async Task<long> AllocatedThisYearAsync(IRedbService redb, string gtin, int year, CancellationToken ct) =>
        await redb.Context.ExecuteScalarAsync<long>(
            """
            SELECT COALESCE(SUM(CAST(quantity AS bigint)), 0)
            FROM dbo.serial_allocations WITH (UPDLOCK, HOLDLOCK)
            WHERE gtin = $1 AND allocation_year = $2
            """,
            [gtin, year], ct);

    /// <summary>
    /// Reserves a contiguous range of serial values for the request, writes the ledger row and one
    /// row per serial number (set-based, however large the quantity), and returns the first value.
    /// </summary>
    public static async Task<long> AllocateAsync(
        IRedbService redb, SerialNumberRequest request, long requestObjectId, int year, CancellationToken ct)
    {
        var first = await redb.Context.ExecuteScalarAsync<long>(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.serial_counters WITH (UPDLOCK, HOLDLOCK) WHERE gtin = $1)
                INSERT INTO dbo.serial_counters (gtin, next_value) VALUES ($1, 1);
            UPDATE dbo.serial_counters
            SET next_value = next_value + $2
            OUTPUT deleted.next_value
            WHERE gtin = $1;
            """,
            [request.Gtin, request.Quantity], ct);

        var allocationId = await redb.Context.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.serial_allocations
                (gtin, partner_code, request_id, request_object_id, first_value, quantity, allocation_year)
            OUTPUT inserted.id
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            [request.Gtin, request.PartnerCode, request.RequestId, requestObjectId, first, request.Quantity, year], ct);

        await redb.Context.ExecuteAsync(
            """
            INSERT INTO dbo.serial_numbers (gtin, serial_value, allocation_id)
            SELECT $1, value, $2
            FROM GENERATE_SERIES(CAST($3 AS bigint), CAST($4 AS bigint));
            """,
            [request.Gtin, allocationId, first, first + request.Quantity - 1], ct);

        return first;
    }
}
