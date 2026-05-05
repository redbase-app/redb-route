using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for evaluating JsonPath queries against the message body in an <see cref="IExchange"/>.
/// </summary>
/// <remarks>
/// Extracts data from a JSON document using JsonPath expressions.
/// Supports both string-based JSON and <see cref="JObject"/>/<see cref="JArray"/> bodies.
/// </remarks>
public class JsonPathExpression : Expression
{
    private readonly string _jsonPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonPathExpression"/> class.
    /// </summary>
    /// <param name="jsonPath">The JsonPath expression used to extract data.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonPath"/> is <c>null</c>.</exception>
    public JsonPathExpression(string jsonPath)
    {
        _jsonPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Thrown when the JsonPath expression cannot be evaluated.</exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        try
        {
            // Get the current message body
            var body = exchange.In.getBody<object>();
            if (body == null)
            {
                throw new InvalidOperationException("Exchange body is null. Cannot evaluate JsonPath expression.");
            }

            // Convert the message body to a JToken for JsonPath evaluation
            JToken jsonToken;
            if (body is JToken jToken)
            {
                jsonToken = jToken;
            }
            else if (body is string bodyString && TryParseJson(bodyString, out var parsedToken))
            {
                jsonToken = parsedToken;
            }
            else
            {
                // Serialize the object to a JToken
                jsonToken = JToken.FromObject(body);
            }

            // Use SelectTokens for recursive descent (..) and other advanced JsonPath features
            if (_jsonPath.Contains("..") || _jsonPath.Contains("[?") || _jsonPath.Contains("[*]") || 
               (_jsonPath.Contains("[") && System.Text.RegularExpressions.Regex.IsMatch(_jsonPath, @"\[\d+:\d+\]")))
            {
                var tokens = jsonToken.SelectTokens(_jsonPath).ToList();

                // For filter queries, if the return type is bool and there is at least one result, return true
                if (_jsonPath.Contains("[?") && typeof(T) == typeof(bool) && tokens.Count > 0)
                {
                    return (T)(object)true;
                }

                return ProcessJTokens<T>(tokens, _jsonPath);
            }
            else
            {
                var token = jsonToken.SelectToken(_jsonPath);
                return ConvertJTokenToType<T>(token, _jsonPath);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON parsing error in JsonPathExpression: {ex.Message}", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error evaluating JsonPath '{_jsonPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Processes a collection of <see cref="JToken"/> results obtained from a <c>SelectTokens</c> query.
    /// </summary>
    /// <typeparam name="T">The target type for conversion.</typeparam>
    /// <param name="tokens">The collection of <see cref="JToken"/> from the query.</param>
    /// <param name="jsonPath">The JsonPath string (used in error messages).</param>
    /// <returns>The converted value of type <typeparamref name="T"/>.</returns>
    private T ProcessJTokens<T>(List<JToken> tokens, string jsonPath)
    {
        // If there are tokens in the result collection
        if (tokens.Count > 0)
        {
            // For string and primitive types with a single result
            if (tokens.Count == 1 && tokens[0] is JValue singleJValue)
            {
                if (typeof(T) == typeof(string))
                    return (T)(object)singleJValue.ToString();

                if (singleJValue.Value is T directResult)
                    return directResult;

                try
                {
                    return (T)Convert.ChangeType(singleJValue.Value, typeof(T));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JsonPath '{jsonPath}': Convert.ChangeType({singleJValue.Value?.GetType().Name} -> {typeof(T).Name}) failed: {ex.Message}");
                    // Fall through to the next handlers
                }
            }

            // Create a JArray from the results
            var resultArray = new JArray(tokens);

            // Handle as an array
            if (typeof(T) == typeof(string))
            {
                // Enhanced string conversion for arrays of simple values
                if (tokens.All(t => t is JValue))
                {
                    // If all array elements are simple values, join them with a comma
                    return (T)(object)string.Join(", ", tokens.Select(t => t.ToString()));
                }
                else
                {
                    // For complex objects, use the standard JArray-to-string conversion
                    return (T)(object)resultArray.ToString(Formatting.None);
                }
            }

            if (typeof(T) == typeof(JArray) || typeof(T) == typeof(JToken))
                return (T)(object)resultArray;

            // Try converting to the requested array or list type
            try
            {
                return resultArray.ToObject<T>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonPath '{jsonPath}': ToObject<{typeof(T).Name}> failed: {ex.Message}");
                // If conversion failed, fall back to a string representation
                if (typeof(T) == typeof(string))
                {
                    if (tokens.All(t => t is JValue))
                    {
                        return (T)(object)string.Join(", ", tokens.Select(t => t.ToString()));
                    }
                    else
                    {
                        return (T)(object)resultArray.ToString(Formatting.None);
                    }
                }
            }
        }

        // If nothing was found
        if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
        {
            throw new InvalidOperationException($"No values found for JsonPath: {jsonPath}");
        }

        return default!;
    }

    /// <summary>
    /// Converts a single <see cref="JToken"/> to the specified type.
    /// </summary>
    /// <typeparam name="T">The target type for conversion.</typeparam>
    /// <param name="token">The <see cref="JToken"/> to convert.</param>
    /// <param name="jsonPath">The JsonPath string (used in error messages).</param>
    /// <returns>The converted value of type <typeparamref name="T"/>.</returns>
    private T ConvertJTokenToType<T>(JToken token, string jsonPath)
    {
        if (token == null)
        {
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
            {
                throw new InvalidOperationException($"No value found for JsonPath: {jsonPath}");
            }
            return default!;
        }

        // Handle arrays
        if (token is JArray array)
        {
            // Check if this is an array of primitive types
            if (array.Count > 0 && array.All(t => t is JValue))
            {
                // Determine the element type of the array
                Type elementType;
                if (array.Count == 0)
                {
                    elementType = typeof(object);
                }
                else
                {
                    // Check whether all elements have the same type
                    var firstTokenType = array[0].Type;
                    bool isHomogeneous = array.All(t => t.Type == firstTokenType);

                    if (isHomogeneous)
                    {
                        elementType = firstTokenType switch
                        {
                            JTokenType.String => typeof(string),
                            JTokenType.Integer => typeof(int),
                            JTokenType.Float => typeof(double),
                            JTokenType.Boolean => typeof(bool),
                            JTokenType.Null => typeof(object),
                            JTokenType.Undefined => typeof(object),
                            JTokenType.Date => typeof(DateTime),
                            JTokenType.Raw => typeof(string),
                            JTokenType.Bytes => typeof(byte[]),
                            JTokenType.Guid => typeof(Guid),
                            JTokenType.Uri => typeof(Uri),
                            JTokenType.TimeSpan => typeof(TimeSpan),
                            JTokenType.Object => typeof(object),
                            JTokenType.Array => typeof(object[]),
                            JTokenType.Constructor => typeof(object),
                            JTokenType.Property => typeof(object),
                            JTokenType.Comment => typeof(string),
                            JTokenType.None => typeof(object),
                            _ => typeof(object)
                        };
                    }
                    else
                    {
                        // If types differ, fall back to object
                        elementType = typeof(object);
                    }
                }

                // Create a typed array
                var typedArray = Array.CreateInstance(elementType, array.Count);
                for (int i = 0; i < array.Count; i++)
                {
                    try
                    {
                        var value = array[i].ToObject(elementType);
                        typedArray.SetValue(value, i);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Cannot convert array element at index {i} to type {elementType.Name}", ex);
                    }
                }

                // If T is object[], return as-is
                if (typeof(T) == typeof(object[]))
                {
                    return (T)(object)typedArray;
                }

                // If T is a specific array type, check compatibility
                if (typeof(T).IsArray)
                {
                    var requestedElementType = typeof(T).GetElementType();
                    if (requestedElementType != null && (requestedElementType == elementType || requestedElementType == typeof(object)))
                    {
                        return (T)(object)typedArray;
                    }
                    else if (requestedElementType != null)
                    {
                        // Try converting to the requested element type
                        var convertedArray = Array.CreateInstance(requestedElementType, array.Count);
                        for (int i = 0; i < array.Count; i++)
                        {
                            try
                            {
                                var convertedValue = Convert.ChangeType(typedArray.GetValue(i), requestedElementType);
                                convertedArray.SetValue(convertedValue, i);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException($"Cannot convert array element at index {i} to requested type {requestedElementType.Name}", ex);
                            }
                        }
                        return (T)(object)convertedArray;
                    }
                }

                // If T is IEnumerable<>, List<>, or IList<>, try converting
                if (typeof(T).IsGenericType)
                {
                    var genericTypeDef = typeof(T).GetGenericTypeDefinition();
                    if (genericTypeDef == typeof(List<>) || genericTypeDef == typeof(IEnumerable<>) || genericTypeDef == typeof(IList<>))
                    {
                        var requestedElementType = typeof(T).GetGenericArguments()[0];
                        var listType = typeof(List<>).MakeGenericType(requestedElementType);
                        var list = Activator.CreateInstance(listType);
                        var addMethod = listType.GetMethod("Add");

                        if (addMethod != null)
                        {
                            for (int i = 0; i < array.Count; i++)
                            {
                                try
                                {
                                    var value = typedArray.GetValue(i);
                                    var convertedValue = requestedElementType == elementType ? value : Convert.ChangeType(value, requestedElementType);
                                    addMethod.Invoke(list, new[] { convertedValue });
                                }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException($"Cannot convert array element at index {i} to type {requestedElementType.Name}", ex);
                                }
                            }
                            return (T)list!;
                        }
                    }
                }

                // If conversion to the desired type failed, return the typed array if compatible
                if (typedArray is T primitiveResult)
                {
                    return primitiveResult;
                }
            }

            // If it is not an array of primitives or conversion failed, return the JArray as-is
            return (T)(object)array;
        }

        // Handle objects
        if (token is JObject obj)
        {
            return obj.ToObject<T>();
        }

        // Handle JValue — extract the primitive value
        if (token is JValue jValue)
        {
            // If JValue contains null
            if (jValue.Value == null)
            {
                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
                {
                    throw new InvalidOperationException($"JsonPath returned null but type {typeof(T).Name} is not nullable");
                }
                return default!;
            }

            // Get the primitive value from JValue
            var primitiveValue = jValue.Value;

            // If T matches the primitive value type directly
            if (primitiveValue is T directResult)
            {
                return directResult;
            }

            // Try converting to the desired type
            try
            {
                return (T)Convert.ChangeType(primitiveValue, typeof(T));
            }
            catch (InvalidCastException)
            {
                throw new InvalidCastException($"Cannot convert JValue with value '{primitiveValue}' of type {primitiveValue.GetType().Name} to type {typeof(T).Name}.");
            }
            catch (FormatException)
            {
                throw new FormatException($"JValue with value '{primitiveValue}' cannot be converted to type {typeof(T).Name} due to format issues.");
            }
        }

        // Return JToken as-is via deserialization
        return token.ToObject<T>();
    }

    /// <summary>
    /// Attempts to parse a JSON string into a <see cref="JToken"/>.
    /// </summary>
    /// <param name="jsonString">The JSON string to parse.</param>
    /// <param name="parsedToken">When successful, the parsed <see cref="JToken"/>.</param>
    /// <returns><c>true</c> if parsing succeeded; otherwise, <c>false</c>.</returns>
    private static bool TryParseJson(string jsonString, out JToken parsedToken)
    {
        try
        {
            parsedToken = JToken.Parse(jsonString);
            return true;
        }
        catch (JsonReaderException)
        {
            parsedToken = null!;
            return false;
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because <see cref="JsonPathExpression"/> does not support setting values.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("JsonPathExpression does not support setting values.");
    }

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{jsonpath({_jsonPath})}}";
}