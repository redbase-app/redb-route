namespace redb.Route.Sql.Mapping;

/// <summary>
/// A column of a result row cannot become its property (or the scalar type) without loss or guessing: the endpoint's
/// <c>outputClass</c> does not fit the rows the statement returns. It is the endpoint's configuration, the same for every
/// row, so a batch that goes on past failed items does not treat it as an item's failure: it ends the batch.
/// </summary>
public sealed class SqlRowMappingException(string message) : InvalidOperationException(message);
