using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace redb.Route.Expressions;

public static partial class ExpressionResolver
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// The public instance property a path step names. The exact-case name wins; the case-insensitive match is used
    /// only when it is the one name that fits. <c>body.Id</c> on a type with both <c>Id</c> and <c>id</c> (a redb
    /// object) reads <c>Id</c>; <c>body.ID</c> there is ambiguous and fails naming both.
    /// </summary>
    private static PropertyInfo? FindProperty(Type type, string name)
        => Pick(type, name, type.GetProperties(MemberFlags).Where(p => p.GetIndexParameters().Length == 0), "property");

    /// <summary>The public instance field a path step names, by the rule of <see cref="FindProperty"/>.</summary>
    private static FieldInfo? FindField(Type type, string name)
        => Pick(type, name, type.GetFields(MemberFlags), "field");

    private static T? Pick<T>(Type type, string name, IEnumerable<T> members, string kind) where T : MemberInfo
    {
        var matches = members.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return null;

        var exact = matches.Where(m => m.Name == name).ToList();
        if (exact.Count > 0)
            return MostDerived(exact);

        var names = matches.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 1)
            return MostDerived(matches);

        throw new ExpressionEvaluationException(
            $"'{name}' matches more than one {kind} of {type.FullName} when case is ignored: {string.Join(", ", names)}. " +
            "Write the member name in its exact case.");
    }

    /// <summary>A member a derived type redeclares (<c>new</c>) is listed once per declaring type: the derived one wins.</summary>
    private static T MostDerived<T>(List<T> members) where T : MemberInfo
        => members.Count == 1
            ? members[0]
            : members.First(m => members.All(o => o == m || m.DeclaringType!.IsSubclassOf(o.DeclaringType!)));
}

