using System.Xml.Linq;

namespace redb.Route.Xml;

/// <summary>
/// The parsed form of a <c>&lt;bean&gt;</c> declaration: one grammar for the loader, the C#
/// generator and the package gate, so none of them re-reads the markup by its own rules.
/// </summary>
internal sealed record BeanDeclaration(
    string? Name,
    string TypeName,
    string? FactoryMethod,
    IReadOnlyList<BeanArgument> ConstructorArgs,
    IReadOnlyList<BeanProperty> Properties,
    XElement At)
{
    /// <summary>
    /// Whether the arguments name their parameter types — then the constructor or factory method
    /// is the one with exactly that signature. All arguments do or none do (the parser enforces it).
    /// </summary>
    public bool TypedArguments => ConstructorArgs.Count > 0 && ConstructorArgs[0].TypeName is not null;
}

/// <summary>
/// One <c>&lt;constructorArg&gt;</c>: its value and, when written, the parameter type it binds to
/// (<c>type="System.String"</c>, Spring's <c>constructor-arg type</c>) — how a constructor or a
/// factory method is chosen among overloads the values alone do not tell apart.
/// </summary>
internal sealed record BeanArgument(BeanValue Value, string? TypeName);

/// <summary>One <c>&lt;property key=…&gt;</c> of a bean with its parsed value.</summary>
internal sealed record BeanProperty(string Key, BeanValue Value, XElement At);

/// <summary>A value slot of a bean: a property, a constructor argument or a list item.</summary>
internal abstract record BeanValue(XElement At);

/// <summary>A literal text, <c>{{key}}</c> placeholders included; converted to the target type.</summary>
internal sealed record ScalarBeanValue(string Text, XElement At) : BeanValue(At);

/// <summary>A registered bean, by name: <c>ref="name"</c> or <c>&lt;ref bean="name"/&gt;</c>.</summary>
internal sealed record RefBeanValue(string Bean, XElement At) : BeanValue(At);

/// <summary>A nested anonymous bean, built in place and never registered.</summary>
internal sealed record NestedBeanValue(BeanDeclaration Bean) : BeanValue(Bean.At);

/// <summary>
/// A <c>&lt;list&gt;</c>: its items build into the target's collection shape. <c>of=</c> names
/// the element type where the target does not (a constructor argument).
/// </summary>
internal sealed record ListBeanValue(string? ElementTypeName, IReadOnlyList<BeanValue> Items, XElement At) : BeanValue(At);

/// <summary>
/// The <c>&lt;bean&gt;</c> grammar. A slot (<c>&lt;property&gt;</c>, <c>&lt;constructorArg&gt;</c>)
/// holds exactly one of: <c>value=</c>, <c>ref=</c>, a nested <c>&lt;bean&gt;</c>, a
/// <c>&lt;list&gt;</c>. A list item is exactly one of: <c>&lt;value&gt;text&lt;/value&gt;</c>,
/// <c>&lt;ref bean=…/&gt;</c>, <c>&lt;bean&gt;</c>, <c>&lt;list&gt;</c>. Every violation is
/// reported at its element; the parse returns null when anything was reported.
/// </summary>
internal static class BeanModel
{
    private const string SlotShape =
        "takes exactly one of: value=, ref=, one nested anonymous <bean type=…>, one <list>";

    /// <summary>Parses a declaration; <paramref name="error"/> receives every problem with its element.</summary>
    internal static BeanDeclaration? Parse(XElement bean, Action<XElement, string> error)
    {
        var failed = false;
        void Fail(XElement at, string message)
        {
            failed = true;
            error(at, message);
        }
        var declaration = ParseBean(bean, Fail);
        return failed ? null : declaration;
    }

    /// <summary>Every <c>ref</c> of a declaration, nested beans and lists included, in document order.</summary>
    internal static IEnumerable<RefBeanValue> References(BeanDeclaration bean)
        => bean.ConstructorArgs.Select(a => a.Value).Concat(bean.Properties.Select(p => p.Value)).SelectMany(References);

    /// <summary>Every nested anonymous bean of a declaration, at any depth.</summary>
    internal static IEnumerable<BeanDeclaration> NestedBeans(BeanDeclaration bean)
        => bean.ConstructorArgs.Select(a => a.Value).Concat(bean.Properties.Select(p => p.Value)).SelectMany(NestedBeans);

    private static IEnumerable<RefBeanValue> References(BeanValue value) => value switch
    {
        RefBeanValue reference => [reference],
        NestedBeanValue nested => References(nested.Bean),
        ListBeanValue list => list.Items.SelectMany(References),
        _ => [],
    };

    private static IEnumerable<BeanDeclaration> NestedBeans(BeanValue value) => value switch
    {
        NestedBeanValue nested => NestedBeans(nested.Bean).Prepend(nested.Bean),
        ListBeanValue list => list.Items.SelectMany(NestedBeans),
        _ => [],
    };

    private static BeanDeclaration ParseBean(XElement bean, Action<XElement, string> fail)
    {
        var typeName = bean.Attribute("type")?.Value;
        if (string.IsNullOrWhiteSpace(typeName))
            fail(bean, "<bean> requires the 'type' attribute.");
        var args = new List<BeanArgument>();
        var properties = new List<BeanProperty>();
        foreach (var child in bean.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "constructorArg":
                    var argType = child.Attribute("type")?.Value;
                    if (argType is not null && string.IsNullOrWhiteSpace(argType))
                    {
                        fail(child, "<constructorArg type=\"\"> names no type.");
                        break;
                    }
                    if (ParseSlot(child, fail) is { } arg)
                        args.Add(new BeanArgument(arg, argType));
                    break;
                case "property":
                    var key = child.Attribute("key")?.Value;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        fail(child, "<property> requires the 'key' attribute.");
                        break;
                    }
                    if (ParseSlot(child, fail) is { } value)
                        properties.Add(new BeanProperty(key, value, child));
                    break;
                default:
                    fail(child, $"<bean> does not take <{child.Name.LocalName}> (valid: <constructorArg>, <property>).");
                    break;
            }
        }
        // One contract per bean: the signature is named in full or chosen from the values alone.
        if (args.Count > 0 && args.Any(a => a.TypeName is null) && args.Any(a => a.TypeName is not null))
            fail(bean, "<constructorArg type=…> is written on some arguments only — name every parameter type, or none.");
        return new BeanDeclaration(
            bean.Attribute("name")?.Value, typeName ?? "", bean.Attribute("factoryMethod")?.Value is { Length: > 0 } factory ? factory : null,
            args, properties, bean);
    }

    private static BeanValue? ParseSlot(XElement slot, Action<XElement, string> fail)
    {
        var value = slot.Attribute("value");
        var reference = slot.Attribute("ref");
        var children = slot.Elements().ToList();
        var forms = (value is null ? 0 : 1) + (reference is null ? 0 : 1) + children.Count;
        if (forms != 1)
        {
            fail(slot, $"<{slot.Name.LocalName}> {SlotShape}.");
            return null;
        }
        if (value is not null)
            return new ScalarBeanValue(value.Value, slot);
        if (reference is not null)
            return ParseReference(reference.Value, slot, fail);
        return children[0].Name.LocalName switch
        {
            "bean" => ParseNested(children[0], fail),
            "list" => ParseList(children[0], fail),
            _ => Refuse(children[0], $"<{slot.Name.LocalName}> {SlotShape}; <{children[0].Name.LocalName}> belongs inside a <list>.", fail),
        };
    }

    private static BeanValue? ParseItem(XElement item, Action<XElement, string> fail)
    {
        switch (item.Name.LocalName)
        {
            case "value":
                if (item.HasElements)
                    return Refuse(item, "<value> holds text only.", fail);
                return new ScalarBeanValue(item.Value, item);
            case "ref":
                return item.Attribute("bean")?.Value is { } name
                    ? ParseReference(name, item, fail)
                    : Refuse(item, "<ref> requires the 'bean' attribute.", fail);
            case "bean":
                return ParseNested(item, fail);
            case "list":
                return ParseList(item, fail);
            default:
                return Refuse(item, $"<list> does not take <{item.Name.LocalName}> (valid: <value>, <ref>, <bean>, <list>).", fail);
        }
    }

    private static BeanValue? ParseNested(XElement bean, Action<XElement, string> fail)
    {
        if (bean.Attribute("name") is not null)
            return Refuse(bean, "a nested <bean> is anonymous — drop name=, or declare it at the top level and use ref=.", fail);
        return new NestedBeanValue(ParseBean(bean, fail));
    }

    private static BeanValue ParseList(XElement list, Action<XElement, string> fail)
    {
        var items = new List<BeanValue>();
        foreach (var item in list.Elements())
        {
            if (ParseItem(item, fail) is { } parsed)
                items.Add(parsed);
        }
        return new ListBeanValue(list.Attribute("of")?.Value is { Length: > 0 } of ? of : null, items, list);
    }

    private static BeanValue? ParseReference(string name, XElement at, Action<XElement, string> fail)
    {
        // One spelling: the attribute already says it is a reference, so the URI form '#name'
        // is not repeated here.
        if (name.Length == 0)
            return Refuse(at, "a bean reference needs a name.", fail);
        if (name.StartsWith('#'))
            return Refuse(at, $"a bean reference is the bare name: '{name[1..]}', not '{name}'.", fail);
        return new RefBeanValue(name, at);
    }

    private static BeanValue? Refuse(XElement at, string message, Action<XElement, string> fail)
    {
        fail(at, message);
        return null;
    }
}
