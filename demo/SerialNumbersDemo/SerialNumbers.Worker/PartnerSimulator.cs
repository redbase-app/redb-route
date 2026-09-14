using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using redb.Route.As2;
using redb.Route.As2.Fluent;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Sftp;
using SerialNumbers.Core;

namespace SerialNumbers.Worker;

/// <summary>
/// Two trading partners, built from redb.Route like the hub itself, in a context of their own. They
/// connect to the hub with the hub's own settings: same SFTP server, same certificate folder.
/// <list type="bullet">
///   <item>ACME uploads the files dropped into its outbox to the hub's SFTP folder and downloads the responses.</item>
///   <item>GLOBEX sends the files dropped into its outbox to the hub over AS2 and receives the responses over AS2.</item>
/// </list>
/// </summary>
public static class PartnerSimulator
{
    public static RouteContext Create(IServiceProvider services, ModuleSettings hub, string partnersDirectory)
    {
        var context = new RouteContext(services, contextId: "partners",
            loggerFactory: (ILoggerFactory?)services.GetService(typeof(ILoggerFactory)));
        context.AddComponent(new FileComponent());
        context.AddComponent(new SftpComponent());
        context.AddComponent(new As2Component());

        context.AddToRegistry("acme-side", new SftpConnectionFactory
        {
            Host = hub.SftpHost,
            Port = hub.SftpPort,
            Username = hub.SftpUsername,
            Password = hub.SftpPasswordFor("acme"),
        });

        context.AddToRegistry("globex-side", new As2ConnectionFactory
        {
            OurCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(hub.As2CertificateDirectory, "globex.pfx"), hub.As2CertificatePassword, X509KeyStorageFlags.Exportable),
            PartnerCertificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(hub.As2CertificateDirectory, "hub.cer")),
            As2From = "GLOBEX",
            As2To = hub.As2HubId,
            Sign = true,
            Encrypt = true,
            SignedMdn = true,
            MdnMode = As2MdnMode.Sync,
        });

        string Folder(params string[] parts) => Path.Combine([partnersDirectory, .. parts]).Replace('\\', '/');

        context.AddRoutes(r =>
        {
            r.From(FileDsl.Read(Folder("acme", "outbox")).Include("*.xml").Delay(1000).MoveTo(".sent"))
                .RouteId("acme-upload")
                .To(Sftp.Directory("/upload/acme/to-hub")
                    .ConnectionFactory("acme-side")
                    .FileName("${header.redbFile.Name}")
                    .TempFileName("${header.redbFile.Name}.part"))
                .Log("ACME uploaded ${header.redbFile.Name} over SFTP");

            r.From(Sftp.Directory("/upload/acme/from-hub").ConnectionFactory("acme-side").Include("*.xml").Delay(2000).Delete())
                .RouteId("acme-download")
                .To(FileDsl.Write(Folder("acme", "inbox")).FileName("${header.redbSftp.Name}"))
                .Log("ACME received ${header.redbSftp.Name}");

            r.From(FileDsl.Read(Folder("globex", "outbox")).Include("*.xml").Delay(1000).MoveTo(".sent"))
                .RouteId("globex-send")
                .Process(e => e.In.ContentType = "application/xml")
                .To(As2.Send($"http://localhost:{hub.As2ReceivePort}/as2/globex").ConnectionFactory("globex-side"))
                .Log("GLOBEX sent ${header.redbFile.Name} over AS2");

            r.From(As2.Receive("/as2/globex-inbox").Host("0.0.0.0").Port(4081).ConnectionFactory("globex-side"))
                .RouteId("globex-receive")
                .SetHeader("responseFile", e => $"SNRESP_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.xml")
                .To(FileDsl.Write(Folder("globex", "inbox")).FileName("${header.responseFile}"))
                .Log("GLOBEX received ${header.responseFile} over AS2");
        });

        foreach (var partner in new[] { "acme", "globex" })
        {
            Directory.CreateDirectory(Path.Combine(partnersDirectory, partner, "outbox"));
            Directory.CreateDirectory(Path.Combine(partnersDirectory, partner, "inbox"));
        }

        CreateAcmeSftpFolders(hub);
        return context;
    }

    /// <summary>
    /// On a real SFTP server the partner's folders are set up once, with the account. The hub treats a
    /// missing inbound folder as a misconfiguration and logs every poll of it, so the demo creates them
    /// before anything starts.
    /// </summary>
    private static void CreateAcmeSftpFolders(ModuleSettings hub)
    {
        using var sftp = new SftpClient(hub.SftpHost, hub.SftpPort, hub.SftpUsername, hub.SftpPasswordFor("acme"));
        sftp.Connect();

        foreach (var folder in new[] { "/upload/acme", "/upload/acme/to-hub", "/upload/acme/from-hub" })
        {
            if (!sftp.Exists(folder))
                sftp.CreateDirectory(folder);
        }

        sftp.Disconnect();
    }
}
