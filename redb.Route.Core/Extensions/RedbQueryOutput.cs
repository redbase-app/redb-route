namespace redb.Route.RedbCore.Extensions;

/// <summary>What <c>RedbQuery</c> puts into its target — one terminal operation of the redb query.</summary>
public enum RedbQueryOutput
{
    /// <summary><c>ToListAsync</c>: the list of <c>RedbObject&lt;TProps&gt;</c> (the default).</summary>
    List,

    /// <summary><c>FirstOrDefaultAsync</c>: the first object in the query's order, or null when nothing matched.</summary>
    First,

    /// <summary><c>CountAsync</c>: the number of matches, an <see cref="int"/>.</summary>
    Count,

    /// <summary><c>AnyAsync</c>: whether anything matched, a <see cref="bool"/>.</summary>
    Any,
}
