using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace SerialNumbers.Core;

/// <summary>
/// Everything the module needs from outside, read once when the module loads.
/// <list type="bullet">
///   <item>Settings come from the route context. The Tsak worker merges <c>Tsak:Contexts:default</c>,
///   <c>Tsak:Contexts:{context}</c>, this module's <c>SerialNumbers.Core.config.json</c> and
///   <c>Tsak:Contexts:{context}:Override</c>, and sets every root key as a context property; a section
///   arrives as a dictionary.</item>
///   <item>Passwords arrive the same way, through the Override layer: environment variables on the
///   worker such as <c>Tsak__Contexts__serial-numbers__Override__Sftp__Password</c>. They are never
///   written into the config file that ships with the module.</item>
///   <item>The connection string is the worker's own <c>ConnectionStrings:MSSql</c>, the one its default
///   redb uses when <c>Tsak:Redb:Provider</c> is <c>mssql</c>: the flat tables live in the database redb
///   lives in, so it is configured in one place.</item>
/// </list>
/// </summary>
public sealed record ModuleSettings(
    string SqlConnectionString,
    string ArchiveDirectory,
    string SftpHost,
    int SftpPort,
    string SftpUsername,
    string SftpPassword,
    IDictionary<string, object?>? SftpPartnerPasswords,
    int As2ReceivePort,
    string As2HubId,
    string As2CertificateDirectory,
    string As2CertificatePassword,
    string ReportDirectory,
    string ReportSchedule,
    string ReportTimeZone,
    int ApiPort)
{
    /// <summary>The connection string of the worker's redb database.</summary>
    public const string ConnectionStringName = "MSSql";

    public static ModuleSettings FromContext(IRouteContext context)
    {
        var config = new ContextSections(context);
        var archive = config.Section("Archive");
        var sftp = config.Section("Sftp");
        var as2 = config.Section("As2");
        var report = config.Section("Report");
        var api = config.Section("Api");

        var configuration = context.GetService<IConfiguration>()
            ?? context.GetServiceProvider()?.GetService<IConfiguration>()
            ?? throw new InvalidOperationException("The route context has no IConfiguration; the connection string cannot be read.");

        return new(
            SqlConnectionString: configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not set on the worker."),
            ArchiveDirectory: FullPath(config.Value(archive, "Archive", "Directory")),
            SftpHost: config.Value(sftp, "Sftp", "Host"),
            SftpPort: int.Parse(config.Value(sftp, "Sftp", "Port"), CultureInfo.InvariantCulture),
            SftpUsername: config.Value(sftp, "Sftp", "Username"),
            SftpPassword: config.Value(sftp, "Sftp", "Password"),
            SftpPartnerPasswords: sftp.TryGetValue("Passwords", out var own) ? own as IDictionary<string, object?> : null,
            As2ReceivePort: int.Parse(config.Value(as2, "As2", "ReceivePort"), CultureInfo.InvariantCulture),
            As2HubId: config.Value(as2, "As2", "Id"),
            As2CertificateDirectory: FullPath(config.Value(as2, "As2", "CertificateDirectory")),
            As2CertificatePassword: config.Value(as2, "As2", "CertificatePassword"),
            ReportDirectory: FullPath(config.Value(report, "Report", "Directory")),
            // Quartz cron format: seconds first.
            ReportSchedule: config.Value(report, "Report", "Cron"),
            // The zone the schedule is read in: an IANA id such as Europe/Berlin.
            ReportTimeZone: config.Value(report, "Report", "TimeZone"),
            // The port of the product API (REST).
            ApiPort: int.Parse(config.Value(api, "Api", "Port"), CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The SFTP password for one partner: <c>Sftp:Passwords:{code}</c> when it is set, the shared
    /// <c>Sftp:Password</c> otherwise. In the demo every partner lives on the same SFTP server; in
    /// production each partner usually has its own server and credentials.
    /// </summary>
    public string SftpPasswordFor(string partnerCode) =>
        SftpPartnerPasswords is not null
        && SftpPartnerPasswords.TryGetValue(partnerCode, out var own)
        && Convert.ToString(own, CultureInfo.InvariantCulture) is { Length: > 0 } password
            ? password
            : SftpPassword;

    /// <summary>Relative paths resolve against the worker's current directory.</summary>
    private static string FullPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private sealed class ContextSections(IRouteContext context)
    {
        public IDictionary<string, object?> Section(string name) =>
            context.GetProperty<IDictionary<string, object?>>(name)
            ?? throw Missing(name);

        public string Value(IDictionary<string, object?> section, string sectionName, string key) =>
            section.TryGetValue(key, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text
                ? text
                : throw Missing($"{sectionName}:{key}");

        private InvalidOperationException Missing(string key) => new(
            $"{key} is not configured for context '{context.ContextId}': set it in SerialNumbers.Core.config.json, " +
            $"or, for a secret or a value of one environment, in Tsak:Contexts:{context.ContextId}:Override.");
    }
}
