using System.Collections;
using System.Globalization;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Sorts a collection taken from the exchange and puts the sorted list back into the body
/// (Camel <c>sort()</c>). The optional key selector is evaluated per element; in the string form
/// (<c>Sort("body.items", "body.priority")</c>) the key expression sees the element as <c>body</c>.
/// Numbers compare numerically across CLR types, strings ordinally, <c>null</c> first.
/// </summary>
public sealed class SortDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, IEnumerable?> _source;
    private readonly Func<object?, object?>? _key;
    private readonly IComparer<object?> _comparer;
    private readonly bool _descending;
    private readonly Func<IEnumerable<object?>, object> _materialize;

    /// <summary>Creates the node.</summary>
    /// <param name="source">Collection to sort (evaluated on the exchange).</param>
    /// <param name="key">Optional per-element key; <c>null</c> sorts the elements themselves.</param>
    /// <param name="comparer">Optional comparer of keys; default compares numbers numerically, strings ordinally.</param>
    /// <param name="descending">Reverse order.</param>
    /// <param name="materialize">Builds the body from the sorted elements (a typed <c>List&lt;T&gt;</c> for the generic DSL form).</param>
    public SortDefinition(Func<IExchange, IEnumerable?> source, Func<object?, object?>? key, IComparer<object?>? comparer, bool descending,
        Func<IEnumerable<object?>, object>? materialize = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _key = key;
        _comparer = comparer ?? ValueComparer.Instance;
        _descending = descending;
        _materialize = materialize ?? (items => items.ToList());
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            var source = _source(exchange);
            if (source is null) return;
            if (source is string)
                throw new InvalidOperationException("Sort: the expression evaluated to a string, not a collection.");

            var items = source.Cast<object?>();
            var keyed = _key is null ? items.Select(i => (Key: i, Item: i)) : items.Select(i => (Key: _key(i), Item: i));
            var ordered = _descending ? keyed.OrderByDescending(p => p.Key, _comparer) : keyed.OrderBy(p => p.Key, _comparer);
            exchange.In.Body = _materialize(ordered.Select(p => p.Item));
        });

    /// <summary>Default key comparison: nulls first, numbers numerically, strings ordinally, then <see cref="IComparable"/>.</summary>
    public sealed class ValueComparer : IComparer<object?>
    {
        /// <summary>Shared instance.</summary>
        public static readonly ValueComparer Instance = new();

        /// <inheritdoc />
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (IsNumber(x) && IsNumber(y))
                return Convert.ToDecimal(x, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture));
            if (x is string sx && y is string sy)
                return string.CompareOrdinal(sx, sy);
            if (x is IComparable cx && x.GetType() == y.GetType())
                return cx.CompareTo(y);
            return string.CompareOrdinal(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture));
        }

        private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}
