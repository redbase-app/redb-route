using System.Text;

namespace redb.Route.Serialization;

/// <summary>
/// The <c>application/x-www-form-urlencoded</c> parser and serializer of the WHATWG URL standard, the one browsers and
/// ASP.NET follow: pairs split on <c>&amp;</c> and the first <c>=</c>, <c>+</c> is a space, <c>%XX</c> is a byte, a
/// <c>%</c> not followed by two hex digits stays as it is.
/// </summary>
internal static class FormUrlEncoding
{
    public static FormBody Decode(ReadOnlySpan<byte> data, Encoding encoding, FormUrlEncodedOptions limits)
    {
        var form = new FormBody();
        var bytes = new List<byte>();
        while (!data.IsEmpty)
        {
            var amp = data.IndexOf((byte)'&');
            var pair = amp < 0 ? data : data[..amp];
            data = amp < 0 ? [] : data[(amp + 1)..];
            if (pair.IsEmpty)
                continue;

            if (form.Count == limits.MaxFields)
                throw new InvalidDataException($"Form data has more than {limits.MaxFields} fields (FormUrlEncodedOptions.MaxFields).");

            var eq = pair.IndexOf((byte)'=');
            var name = DecodeComponent(eq < 0 ? pair : pair[..eq], encoding, bytes);
            var value = eq < 0 ? string.Empty : DecodeComponent(pair[(eq + 1)..], encoding, bytes);
            if (name.Length > limits.MaxNameLength)
                throw new InvalidDataException($"Form data has a field name longer than {limits.MaxNameLength} characters (FormUrlEncodedOptions.MaxNameLength).");
            if (value.Length > limits.MaxValueLength)
                throw new InvalidDataException($"Form data has a field value longer than {limits.MaxValueLength} characters (FormUrlEncodedOptions.MaxValueLength).");
            form.Add(name, value);
        }
        return form;
    }

    private static string DecodeComponent(ReadOnlySpan<byte> text, Encoding encoding, List<byte> buffer)
    {
        buffer.Clear();
        for (var i = 0; i < text.Length; i++)
        {
            var b = text[i];
            if (b == (byte)'+')
            {
                buffer.Add((byte)' ');
            }
            else if (b == (byte)'%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                buffer.Add((byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                i += 2;
            }
            else
            {
                buffer.Add(b);
            }
        }
        return encoding.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
    }

    public static string Encode(IEnumerable<KeyValuePair<string, string>> fields, Encoding encoding)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            if (sb.Length > 0)
                sb.Append('&');
            EncodeComponent(sb, name, encoding);
            sb.Append('=');
            EncodeComponent(sb, value, encoding);
        }
        return sb.ToString();
    }

    // The application/x-www-form-urlencoded percent-encode set: everything but ASCII alphanumerics and *-._ is
    // encoded, and a space becomes '+'.
    private static void EncodeComponent(StringBuilder sb, string text, Encoding encoding)
    {
        foreach (var b in encoding.GetBytes(text))
        {
            if (b == (byte)' ')
                sb.Append('+');
            else if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                     or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                sb.Append((char)b);
            else
                sb.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 0xF]);
        }
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte b) => b <= (byte)'9' ? b - '0' : (b | 0x20) - 'a' + 10;
}
