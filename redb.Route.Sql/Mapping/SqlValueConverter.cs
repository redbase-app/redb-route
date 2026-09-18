using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Converts a database value to a property type only when the conversion is lossless and unambiguous: a number the target
/// holds exactly, a defined enum member, text in the invariant culture and ISO 8601 form, a timestamp that is UTC or carries
/// its offset. Every other combination is refused with a reason. Reasons never quote the value, so a mapping error does not
/// leak row data into a log. Apache Camel's <c>BeanPropertyRowMapper</c> refuses a type mismatch the same way.
/// </summary>
internal static partial class SqlValueConverter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Whether a NULL column may be assigned to <paramref name="type"/>: a reference type or a nullable value type.</summary>
    internal static bool AcceptsNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>A short name of <paramref name="type"/> for messages: <c>Int32?</c> for a nullable value type.</summary>
    internal static string TypeName(Type type) =>
        Nullable.GetUnderlyingType(type) is { } underlying ? underlying.Name + "?" : type.Name;

    /// <summary>
    /// Converts the non-null <paramref name="value"/> to <paramref name="targetType"/> (a nullable type converts to its
    /// underlying type); returns false with the reason when the conversion would lose or guess anything.
    /// </summary>
    internal static bool TryConvert(object value, Type targetType, out object? result, out string reason)
    {
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        result = null;
        reason = "";

        if (target.IsInstanceOfType(value))
        {
            result = value;
            return true;
        }

        if (target.IsEnum)
            return TryEnum(value, target, out result, out reason);
        if (target == typeof(string))
            return TryString(value, out result, out reason);
        if (target == typeof(bool))
            return TryBool(value, out result, out reason);
        if (IsIntegerType(target))
            return TryInteger(value, target, out result, out reason);
        if (target == typeof(decimal) || target == typeof(double) || target == typeof(float))
            return TryReal(value, target, out result, out reason);
        if (target == typeof(Guid))
            return TryGuid(value, out result, out reason);
        if (target == typeof(DateTime))
            return TryDateTime(value, out result, out reason);
        if (target == typeof(DateTimeOffset))
            return TryDateTimeOffset(value, out result, out reason);
        if (target == typeof(DateOnly))
            return TryDateOnly(value, out result, out reason);
        if (target == typeof(TimeOnly))
            return TryTimeOnly(value, out result, out reason);
        if (target == typeof(TimeSpan))
            return TryTimeSpan(value, out result, out reason);
        if (target == typeof(char) && value is string { Length: 1 } single)
        {
            result = single[0];
            return true;
        }

        reason = "there is no lossless conversion between these types";
        return false;
    }

    // ── Numbers ─────────────────────────────────────────────────────

    private static bool IsIntegerType(Type type) => Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;

    private static bool IsInteger(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static BigInteger ToBigInteger(object integer) =>
        integer is ulong unsigned ? new BigInteger(unsigned) : new BigInteger(Convert.ToInt64(integer, Invariant));

    /// <summary>The exact integer <paramref name="value"/> stands for, if any.</summary>
    private static bool TryExactInteger(object value, out BigInteger integer, out string reason)
    {
        integer = default;
        reason = "";
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                integer = ToBigInteger(value);
                return true;
            case decimal d when decimal.Truncate(d) == d:
                integer = new BigInteger(d);
                return true;
            case double f when double.IsFinite(f) && Math.Truncate(f) == f:
                integer = new BigInteger(f);
                return true;
            case float f when float.IsFinite(f) && MathF.Truncate(f) == f:
                integer = new BigInteger(f);
                return true;
            case decimal or double or float:
                reason = "the number has a fractional part";
                return false;
            case string s when BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, Invariant, out var parsed):
                integer = parsed;
                return true;
            case string:
                reason = "the text is not an integer";
                return false;
            default:
                reason = "the value is not a number";
                return false;
        }
    }

    private static bool TryInteger(object value, Type target, out object? result, out string reason)
    {
        result = null;
        if (!TryExactInteger(value, out var integer, out reason))
            return false;

        var code = Type.GetTypeCode(target);
        var inRange = code switch
        {
            TypeCode.SByte => integer >= sbyte.MinValue && integer <= sbyte.MaxValue,
            TypeCode.Byte => integer >= byte.MinValue && integer <= byte.MaxValue,
            TypeCode.Int16 => integer >= short.MinValue && integer <= short.MaxValue,
            TypeCode.UInt16 => integer >= ushort.MinValue && integer <= ushort.MaxValue,
            TypeCode.Int32 => integer >= int.MinValue && integer <= int.MaxValue,
            TypeCode.UInt32 => integer >= uint.MinValue && integer <= uint.MaxValue,
            TypeCode.Int64 => integer >= long.MinValue && integer <= long.MaxValue,
            _ => integer >= ulong.MinValue && integer <= ulong.MaxValue,
        };
        if (!inRange)
        {
            reason = $"the number is outside the range of {target.Name}";
            return false;
        }

        result = code switch
        {
            TypeCode.SByte => (object)(sbyte)integer,
            TypeCode.Byte => (byte)integer,
            TypeCode.Int16 => (short)integer,
            TypeCode.UInt16 => (ushort)integer,
            TypeCode.Int32 => (int)integer,
            TypeCode.UInt32 => (uint)integer,
            TypeCode.Int64 => (long)integer,
            _ => (ulong)integer,
        };
        return true;
    }

    private static bool TryReal(object value, Type target, out object? result, out string reason)
    {
        result = null;
        reason = "";

        if (value is string text)
            return TryParseReal(text, target, out result, out reason);

        if (!IsInteger(value) && value is not (decimal or double or float))
        {
            reason = "the value is not a number";
            return false;
        }

        if (target == typeof(decimal))
        {
            decimal? exact = value switch
            {
                double f when double.IsFinite(f) && Math.Abs(f) < 7.9e28 && (double)(decimal)f == f => (decimal)f,
                float f when float.IsFinite(f) && Math.Abs(f) < 7.9e28f && (float)(decimal)f == f => (decimal)f,
                double or float => null,
                _ => Convert.ToDecimal(value, Invariant),
            };
            result = exact;
            reason = exact is null ? "the number cannot be held exactly by Decimal" : "";
            return exact is not null;
        }

        if (target == typeof(double))
        {
            double? exact = value switch
            {
                float f => f,
                decimal d when (decimal)(double)d == d => (double)d,
                decimal => null,
                _ when IsInteger(value) && new BigInteger((double)ToBigInteger(value)) == ToBigInteger(value) => (double)ToBigInteger(value),
                _ => null,
            };
            result = exact;
            reason = exact is null ? "the number cannot be held exactly by Double" : "";
            return exact is not null;
        }

        float? single = value switch
        {
            double f when !double.IsFinite(f) || (double)(float)f == f => (float)f,
            decimal d when (decimal)(float)d == d => (float)d,
            _ when IsInteger(value) && new BigInteger((float)ToBigInteger(value)) == ToBigInteger(value) => (float)ToBigInteger(value),
            _ => null,
        };
        result = single;
        reason = single is null ? "the number cannot be held exactly by Single" : "";
        return single is not null;
    }

    private static bool TryParseReal(string text, Type target, out object? result, out string reason)
    {
        result = null;
        reason = "the text is not a number";
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

        if (target == typeof(decimal) && decimal.TryParse(text, styles, Invariant, out var d))
            result = d;
        else if (target == typeof(double) && double.TryParse(text, styles, Invariant, out var f) && double.IsFinite(f))
            result = f;
        else if (target == typeof(float) && float.TryParse(text, styles, Invariant, out var s) && float.IsFinite(s))
            result = s;

        if (result is null)
            return false;
        reason = "";
        return true;
    }

    private static bool TryBool(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        if (value is string text && bool.TryParse(text, out var parsed))
        {
            result = parsed;
            return true;
        }

        // Any number that is exactly 0 or 1: an integer column, or a numeric one without a fractional part (Oracle NUMBER(1),
        // PostgreSQL numeric come as decimal).
        if (TryExactInteger(value, out var integer, out _))
        {
            if (integer == 0 || integer == 1)
            {
                result = integer == 1;
                return true;
            }

            reason = "only 0 or 1 is a Boolean";
            return false;
        }

        reason = "only 0, 1, 'true' or 'false' is a Boolean";
        return false;
    }

    // ── Enums, text, GUIDs ──────────────────────────────────────────

    private static bool TryEnum(object value, Type target, out object? result, out string reason)
    {
        result = null;
        object candidate;

        if (value is string text)
        {
            var name = text.Trim();
            if (name.Length == 0 || char.IsDigit(name[0]) || name[0] is '-' or '+' ||
                !Enum.TryParse(target, name, ignoreCase: true, out var parsed))
            {
                reason = $"the text is not the name of a {target.Name} member";
                return false;
            }

            candidate = parsed!;
        }
        else if (TryExactInteger(value, out _, out _))
        {
            // An integer column, or a numeric one without a fractional part, as for the integer properties.
            if (!TryInteger(value, Enum.GetUnderlyingType(target), out var underlying, out reason))
                return false;
            candidate = Enum.ToObject(target, underlying!);
        }
        else
        {
            reason = $"only a number or a member name is a {target.Name}";
            return false;
        }

        if (!IsMember(target, candidate))
        {
            reason = $"the value is not a member of {target.Name}";
            return false;
        }

        result = candidate;
        reason = "";
        return true;
    }

    private static bool IsMember(Type enumType, object candidate)
    {
        if (Enum.IsDefined(enumType, candidate))
            return true;
        if (!enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
            return false;

        // A [Flags] combination is a member when every bit it sets belongs to a defined value.
        var all = Enum.GetValues(enumType).Cast<object>().Aggregate(BigInteger.Zero, (mask, v) => mask | ToBigInteger(Convert.ChangeType(v, Enum.GetUnderlyingType(enumType), Invariant)));
        var bits = ToBigInteger(Convert.ChangeType(candidate, Enum.GetUnderlyingType(enumType), Invariant));
        return (bits & ~all) == 0;
    }

    private static bool TryString(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        if (IsInteger(value) || value is decimal or double or float or bool or char or Guid)
        {
            result = Convert.ToString(value, Invariant);
            return true;
        }

        reason = value is DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
            ? "a date or time has no single text form; map it to a date or time property"
            : "the value has no single text form";
        return false;
    }

    private static bool TryGuid(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        if (value is string text && Guid.TryParse(text, out var parsed))
        {
            result = parsed;
            return true;
        }

        reason = value is byte[]
            ? "the byte order of a binary GUID differs between databases; store the key as text or map it to byte[]"
            : "the text is not a GUID";
        return false;
    }

    // ── Dates and times ─────────────────────────────────────────────

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?)?(?<zone>Z|[+-]\d{2}(:\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTime();

    private const string NoTimeZone =
        "the timestamp has no time zone; map it to a DateTime property, or store a type that keeps the offset " +
        "(timestamptz, datetimeoffset)";

    private static bool TryDateTime(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        switch (value)
        {
            case DateOnly date:
                result = date.ToDateTime(TimeOnly.MinValue);
                return true;
            case DateTimeOffset:
                reason = "the offset would be lost; map it to a DateTimeOffset property";
                return false;
            case string text when IsoTimestamp().Match(text) is { Success: true } match:
                var zone = match.Groups["zone"].Value;
                if (zone.Length > 0 && zone != "Z")
                {
                    reason = "the text carries an offset that a DateTime cannot keep; map it to a DateTimeOffset property";
                    return false;
                }

                // The pattern checks the shape only; the ranges (month 13, hour 25) are checked here, without the value.
                if (!DateTime.TryParse(text, Invariant, DateTimeStyles.RoundtripKind, out var dateTime))
                {
                    reason = "the text has the shape of an ISO 8601 date and time but is not a valid one";
                    return false;
                }

                result = dateTime;
                return true;
            default:
                reason = "only a date, or ISO 8601 text, is a DateTime";
                return false;
        }
    }

    private static bool TryDateTimeOffset(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        switch (value)
        {
            case DateTime { Kind: DateTimeKind.Utc } utc:
                result = new DateTimeOffset(utc, TimeSpan.Zero);
                return true;
            case DateTime:
                reason = NoTimeZone;
                return false;
            case string text when IsoTimestamp().Match(text) is { Success: true } match:
                if (match.Groups["zone"].Value.Length == 0)
                {
                    reason = NoTimeZone;
                    return false;
                }

                if (!DateTimeOffset.TryParse(text, Invariant, DateTimeStyles.None, out var dateTimeOffset))
                {
                    reason = "the text has the shape of an ISO 8601 date and time but is not a valid one";
                    return false;
                }

                result = dateTimeOffset;
                return true;
            default:
                reason = "only a UTC timestamp, or ISO 8601 text with an offset, is a DateTimeOffset";
                return false;
        }
    }

    private static bool TryDateOnly(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        switch (value)
        {
            case DateTime dateTime when dateTime.TimeOfDay == TimeSpan.Zero:
                result = DateOnly.FromDateTime(dateTime);
                return true;
            case DateTime:
                reason = "the timestamp has a time of day; map it to a DateTime property";
                return false;
            case string text when IsoDate().IsMatch(text) &&
                                  DateOnly.TryParseExact(text, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date):
                result = date;
                return true;
            default:
                reason = "only a date, or yyyy-MM-dd text, is a DateOnly";
                return false;
        }
    }

    private static bool TryTimeOnly(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        switch (value)
        {
            case TimeSpan span when span >= TimeSpan.Zero && span < TimeSpan.FromDays(1):
                result = TimeOnly.FromTimeSpan(span);
                return true;
            case string text when IsoTime().IsMatch(text) && TimeOnly.TryParse(text, Invariant, DateTimeStyles.None, out var time):
                result = time;
                return true;
            default:
                reason = "only a time of day, or HH:mm[:ss] text, is a TimeOnly";
                return false;
        }
    }

    private static bool TryTimeSpan(object value, out object? result, out string reason)
    {
        result = null;
        reason = "";
        switch (value)
        {
            case TimeOnly time:
                result = time.ToTimeSpan();
                return true;
            case string text when TimeSpan.TryParseExact(text, "c", Invariant, out var span):
                result = span;
                return true;
            default:
                reason = "only a time, or [d.]hh:mm:ss text, is a TimeSpan";
                return false;
        }
    }
}
