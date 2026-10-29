using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Serialization;

namespace redb.Route.DataFormats.Mime;

/// <summary>DSL and registration sugar for <see cref="MimeMultipartDataFormat"/>.</summary>
public static class MimeMultipartRouteDefinitionExtensions
{
    /// <summary>
    /// Reads a <c>multipart/form-data</c> body: the fields into a <see cref="FormBody"/>, the files into attachments.
    /// A body that is not multipart passes unchanged.
    /// </summary>
    public static IRouteDefinition UnmarshalMimeMultipart(this IRouteDefinition route, Action<MimeMultipartDataFormatOptions>? configure = null)
        => route.Unmarshal<FormBody>(new MimeMultipartDataFormat(Build(configure)));

    /// <summary>
    /// Reads a multipart body into <typeparamref name="T"/>: <see cref="FormBody"/> for <c>multipart/form-data</c>;
    /// <c>byte[]</c>, <c>string</c> or <c>Stream</c> for the first part of any other <c>multipart/*</c>.
    /// </summary>
    public static IRouteDefinition UnmarshalMimeMultipart<T>(this IRouteDefinition route, Action<MimeMultipartDataFormatOptions>? configure = null)
        => route.Unmarshal<T>(new MimeMultipartDataFormat(Build(configure)));

    /// <summary>Writes the body and the attachments as a multipart (<c>form-data</c> by default).</summary>
    public static IRouteDefinition MarshalMimeMultipart(this IRouteDefinition route, Action<MimeMultipartDataFormatOptions>? configure = null)
        => route.Marshal(new MimeMultipartDataFormat(Build(configure)));

    /// <summary>
    /// Registers the format under <c>multipart/form-data</c> (or the configured subtype), for
    /// <c>Marshal("multipart/form-data")</c>, <c>Unmarshal&lt;FormBody&gt;("multipart/form-data")</c> and
    /// <c>ConvertBody&lt;FormBody&gt;()</c> after an HTTP consumer. <c>multipart/related</c> is never registered: it
    /// belongs to SOAP MTOM.
    /// </summary>
    public static IRouteContext AddMimeMultipartDataFormat(this IRouteContext context, Action<MimeMultipartDataFormatOptions>? configure = null)
        => context.AddDataFormat(new MimeMultipartDataFormat(Build(configure)));

    /// <summary>DI form of <see cref="AddMimeMultipartDataFormat(IRouteContext, Action{MimeMultipartDataFormatOptions})"/>.</summary>
    public static RedbRouteBuilder AddMimeMultipartDataFormat(this RedbRouteBuilder builder, Action<MimeMultipartDataFormatOptions>? configure = null)
        => builder.AddDataFormat(new MimeMultipartDataFormat(Build(configure)));

    private static MimeMultipartDataFormatOptions Build(Action<MimeMultipartDataFormatOptions>? configure)
    {
        var options = new MimeMultipartDataFormatOptions();
        configure?.Invoke(options);
        return options;
    }
}
