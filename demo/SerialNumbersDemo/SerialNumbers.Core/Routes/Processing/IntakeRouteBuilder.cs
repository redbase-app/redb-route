using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.RedbCore.Extensions;
using SerialNumbers.Core.Infrastructure;
using SerialNumbers.Core.Integration.Xml;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Core.Routes.Processing;

/// <summary>
/// The common intake for every transport: archive the raw file first, then route it by what it is.
/// </summary>
public sealed class IntakeRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        var settings = ModuleSettings.FromContext(Context!);

        From(RouteUris.Intake)
            .RouteId("intake")
            .ConvertBody<string>()

            // The raw copy goes to the archive before anything can fail on its content.
            .SetHeader(SerialHeaders.ArchivePath, e => ArchivePaths.For(
                e.In.GetHeader<string>(SerialHeaders.Partner)!,
                e.In.GetHeader<string>(SerialHeaders.FileName)!,
                DateTimeOffset.UtcNow))
            .To(FileDsl.Write(settings.ArchiveDirectory).FileName("${header.serials.archivePath}"))

            .SetHeader(SerialHeaders.MessageType, e => XmlMessageInspector.RootElementName(e.In.Body as string))
            .Choice()
                .When(e => e.In.GetHeader<string>(SerialHeaders.MessageType) is null)
                    .ProcessWithRedb(IntakeRecorder.RecordInvalidAsync)
                    .Log("${header.serials.partner}: ${header.serials.fileName} is not well-formed XML, recorded as Invalid")
                .When(XPath("/SerialNumberRequest"))
                    .To(RouteUris.SerialNumberRequest)
                .Otherwise()
                    // Handled context-wide by ExceptionRouteBuilder: archived, recorded, parked.
                    .ThrowException<UnsupportedMessageTypeException>("The message type has no route in this module.")
            .EndChoice();
    }
}
