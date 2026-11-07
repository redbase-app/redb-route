using redb.Core;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core.Services;

/// <summary>
/// Serial numbers and the quota ledger, as plain SQL through <c>redb.Context</c>. Inside the request
/// route's <c>.Transacted()</c> block it runs on the transaction's connection, so the numbers, the
/// ledger row and the redb objects of the request commit or roll back together.
/// <para>
/// Parameters are written <c>$1</c>, <c>$2</c>...: redb maps them to the provider's own syntax.
/// </para>
/// </summary>
public static class SerialNumberAllocator
{
    /// <summary>
    /// The size of the block each serial number is drawn from: one chance in 10 000 to guess a
    /// number, as the verification service of a medicines agency expects.
    /// </summary>
    public const int BlockSize = 10_000;

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
    /// Reserves one block per serial number, writes the ledger row and one row per serial number,
    /// and returns the id of the allocation.
    /// <para>
    /// The numbers are not consecutive: the i-th number is a random value from its own block
    /// (<c>(first + i) * 10000 + 1</c> .. <c>(first + i + 1) * 10000</c>), taken from
    /// <c>CRYPT_GEN_RANDOM</c>, the cryptographic generator of SQL Server. Blocks never overlap, so
    /// two numbers cannot collide, and the whole set is written by one statement however large it is.
    /// </para>
    /// </summary>
    public static async Task<long> AllocateAsync(
        IRedbService redb, SerialNumberRequest request, long requestObjectId, int year, CancellationToken ct)
    {
        var firstBlock = await redb.Context.ExecuteScalarAsync<long>(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.serial_blocks WITH (UPDLOCK, HOLDLOCK) WHERE gtin = $1)
                INSERT INTO dbo.serial_blocks (gtin, next_block) VALUES ($1, 0);
            UPDATE dbo.serial_blocks
            SET next_block = next_block + $2
            OUTPUT deleted.next_block
            WHERE gtin = $1;
            """,
            [request.Gtin, request.Quantity], ct);

        var allocationId = await redb.Context.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.serial_allocations
                (gtin, partner_code, request_id, request_object_id, first_block, quantity, allocation_year)
            OUTPUT inserted.id
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            [request.Gtin, request.PartnerCode, request.RequestId, requestObjectId, firstBlock, request.Quantity, year], ct);

        // CRYPT_GEN_RANDOM(4) is evaluated per row; as bigint it is 0 .. 2^32 - 1, and modulo 10 000
        // it is an offset inside the block.
        await redb.Context.ExecuteAsync(
            """
            INSERT INTO dbo.serial_numbers (gtin, serial_value, allocation_id)
            SELECT $1,
                   (CAST($3 AS bigint) + s.value) * $5 + 1 + CAST(CRYPT_GEN_RANDOM(4) AS bigint) % $5,
                   $2
            FROM GENERATE_SERIES(CAST(0 AS bigint), CAST($4 AS bigint) - 1) AS s;
            """,
            [request.Gtin, allocationId, firstBlock, request.Quantity, BlockSize], ct);

        return allocationId;
    }

    /// <summary>The serial numbers of an allocation, in block order.</summary>
    public static Task<List<long>> SerialNumbersOfAsync(IRedbService redb, long allocationId, CancellationToken ct) =>
        redb.Context.QueryScalarListAsync<long>(
            "SELECT serial_value FROM dbo.serial_numbers WHERE allocation_id = $1 ORDER BY serial_value",
            [allocationId], ct);
}
