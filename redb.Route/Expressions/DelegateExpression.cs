using System;
using System.Collections.Generic;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// An expression based on a delegate (function) that evaluates its value from an <see cref="IExchange"/>.
/// </summary>
/// <typeparam name="T">The return type of the delegate.</typeparam>
public class DelegateExpression<T> : Expression
{
    private readonly Func<IExchange, T> _delegate;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateExpression{T}"/> class with the specified delegate.
    /// </summary>
    /// <param name="delegate">A function that accepts an <see cref="IExchange"/> and returns a value of type <typeparamref name="T"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="delegate"/> is <c>null</c>.</exception>
    public DelegateExpression(Func<IExchange, T> @delegate)
    {
        _delegate = @delegate ?? throw new ArgumentNullException(nameof(@delegate));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidCastException">Thrown when the delegate result cannot be cast to <typeparamref name="K"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the delegate execution fails.</exception>
    public override K Evaluate<K>(IExchange exchange)
    {
        try
        {
            var result = _delegate(exchange);

            // If the result is null, return default(K)
            if (result == null)
            {
                return default;
            }

            if (result is K typedResult)
            {
                return typedResult;
            }

            // If K is some IEnumerable<> and result is a collection of another type,
            // try converting via reflection
            if (typeof(K).IsGenericType &&
                typeof(K).GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                result is System.Collections.IEnumerable collection)
            {
                // Element type of the target collection
                var targetElementType = typeof(K).GetGenericArguments()[0];

                // Convert collection elements and return as IEnumerable<targetElementType>
                var convertedList = Activator.CreateInstance(typeof(List<>).MakeGenericType(targetElementType));
                if (convertedList != null)
                {
                    var addMethod = convertedList.GetType().GetMethod("Add");
                    if (addMethod != null)
                    {
                        foreach (var item in collection)
                        {
                            if (item != null && item.GetType().IsAssignableTo(targetElementType))
                            {
                                addMethod.Invoke(convertedList, new[] { item });
                            }
                        }
                    }

                    if (convertedList is K listAsK)
                    {
                        return listAsK;
                    }
                }
            }

            // Try standard type conversion
            return (K)Convert.ChangeType(result, typeof(K));
        }
        catch (InvalidCastException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Wrap exceptions originating from inside the delegate
            throw new InvalidOperationException($"Error executing delegate expression: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because a delegate expression is read-only.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("Cannot set value on a delegate expression");
    }
}

