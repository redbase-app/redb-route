using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Transactions;

namespace redb.Route.Definitions;

/// <summary>
/// Projects live <see cref="IProcessorDefinition"/> instances built by the DSL into the
/// canonical <see cref="RouteStep"/> records exposed via <see cref="RouteDefinition.Steps"/>.
/// Returns <c>null</c> for definitions that have no corresponding record.
/// </summary>
internal static class RouteStepProjection
{
    public static RouteStep? TryProject(IProcessorDefinition def) => def switch
    {
        TransactionDefinition t => new TransactedStep(t.Policy),

        BeanDefinition b => new BeanStep(b.ServiceType, b.Method),

        SetBodyExpressionDefinition sb when sb.Expression is StringExpression se
            => new SetBodyStringExpressionStep(se.Template),
        SetBodyExpressionDefinition sb => new SetBodyExpressionStep(sb.Expression),
        SetBodyStringExpressionDefinition sbt => new SetBodyStringExpressionStep(sbt.Template),

        SetHeaderExpressionDefinition sh when sh.Expression is StringExpression se
            => new SetHeaderStringExpressionStep(sh.Name, se.Template),
        SetHeaderExpressionDefinition sh => new SetHeaderExpressionStep(sh.Name, sh.Expression),
        SetHeaderStringExpressionDefinition sht => new SetHeaderStringExpressionStep(sht.Name, sht.Template),

        TransformExpressionDefinition t when t.Expression is StringExpression se
            => new TransformStringExpressionStep(se.Template),
        TransformExpressionDefinition t => new TransformExpressionStep(t.Expression),
        TransformStringExpressionDefinition tt => new TransformStringExpressionStep(tt.Template),

        LogTemplateDefinition l => new LogTemplateStep(l.Template, l.Level),

        LogStaticDefinition ls when ls.Message.Contains("${", StringComparison.Ordinal)
            => new LogTemplateStep(ls.Message, ls.Level),

        FilterDefinition f when f.SourcePredicate is not null
            => new FilterPredicateStep(f.SourcePredicate),
        FilterDefinition f when f.SourceTemplate is not null
            => new FilterExpressionStep(f.SourceTemplate),

        SplitDefinition s when s.SourceExpression is not null
            => new SplitExpressionStep(s.SourceExpression, null),

        ChoiceDefinition c => ProjectChoice(c),

        _ => null,
    };

    private static ChoiceStep ProjectChoice(ChoiceDefinition choice)
    {
        var whenClauses = new List<ChoiceWhenClause>();
        var predicateClauses = new List<ChoiceWhenPredicateClause>();
        var expressionClauses = new List<ChoiceWhenExpressionClause>();

        foreach (var when in choice.Whens)
        {
            whenClauses.Add(new ChoiceWhenClause(when.Predicate, Array.Empty<RouteStep>()));

            if (when.SourcePredicate is not null)
                predicateClauses.Add(new ChoiceWhenPredicateClause(when.SourcePredicate, Array.Empty<RouteStep>()));

            if (when.SourceExpression is not null)
                expressionClauses.Add(new ChoiceWhenExpressionClause(when.SourceExpression, Array.Empty<RouteStep>()));
        }

        return new ChoiceStep(
            whenClauses,
            OtherwiseSteps: null,
            PredicateClauses: predicateClauses.Count > 0 ? predicateClauses : null,
            ExpressionClauses: expressionClauses.Count > 0 ? expressionClauses : null);
    }
}
