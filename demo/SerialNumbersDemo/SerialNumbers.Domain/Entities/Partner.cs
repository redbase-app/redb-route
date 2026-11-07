using redb.Core.Attributes;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Domain.Entities;

/// <summary>
/// A trading partner. The code is also the object's unique key (<c>ValueUnique</c>), so two
/// partners can never share one. Adding a partner is adding data: the module registers one
/// connection factory and one set of routes per partner when it starts.
/// </summary>
[RedbScheme(Name = "SerialNumbers.Partner")]
public sealed class Partner
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><see cref="Transports.Sftp"/> or <see cref="Transports.As2"/>.</summary>
    public string Transport { get; set; } = Transports.Sftp;

    /// <summary>SFTP folder the partner drops its requests into.</summary>
    public string? SftpInboundFolder { get; set; }

    /// <summary>SFTP folder the hub delivers responses to.</summary>
    public string? SftpOutboundFolder { get; set; }

    /// <summary>The partner's AS2 identifier.</summary>
    public string? As2Id { get; set; }

    /// <summary>The partner's AS2 endpoint the hub posts responses to.</summary>
    public string? As2Url { get; set; }
}
