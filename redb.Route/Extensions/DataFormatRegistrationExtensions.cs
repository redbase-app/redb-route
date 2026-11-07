using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Extensions;

/// <summary>
/// Registers a data format in the context's <see cref="IDataFormatRegistry"/> so it can be addressed
/// by content type: <c>Marshal("text/csv")</c>, <c>Unmarshal&lt;T&gt;("text/csv")</c>, and the
/// content-type-driven <c>Unmarshal&lt;T&gt;()</c> / <c>ConvertBody&lt;T&gt;()</c>. Format packages wrap this
/// in a named helper (<c>AddCsvDataFormat()</c>).
/// </summary>
public static class DataFormatRegistrationExtensions
{
    /// <summary>Registers <paramref name="serializer"/> under its <see cref="IMessageSerializer.ContentType"/>, its <see cref="IMessageSerializer.MediaTypes"/> and any extra content types.</summary>
    public static IRouteContext AddDataFormat(this IRouteContext context, IMessageSerializer serializer, params string[] additionalContentTypes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(serializer);

        var registry = context.GetService<IDataFormatRegistry>()
            ?? throw new InvalidOperationException("IDataFormatRegistry is not available on the route context.");
        registry.Register(serializer.ContentType, serializer);
        foreach (var contentType in additionalContentTypes)
            registry.Register(contentType, serializer);
        return context;
    }

    /// <summary>DI form: the format is registered on the hosted context when it is built.</summary>
    public static RedbRouteBuilder AddDataFormat(this RedbRouteBuilder builder, IMessageSerializer serializer, params string[] additionalContentTypes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serializer);
        builder.Services.AddSingleton<IRouteContextConfigurator>(new DataFormatConfigurator(serializer, additionalContentTypes));
        return builder;
    }

    private sealed class DataFormatConfigurator(IMessageSerializer serializer, string[] additionalContentTypes) : IRouteContextConfigurator
    {
        public void Configure(RouteContext context) => context.AddDataFormat(serializer, additionalContentTypes);
    }
}
