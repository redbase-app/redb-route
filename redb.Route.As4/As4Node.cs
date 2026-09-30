using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.As4;

/// <summary>
/// The resolved, validated view of one <see cref="As4ConnectionFactory"/>: the factory and its partners by
/// name, looked up in the route context once, when an endpoint starts. Resolving at start rather than at
/// endpoint creation is deliberate: the registry may be filled after the routes are defined, and a
/// misconfigured node then stops the route from starting instead of failing the first message a partner sends.
/// </summary>
internal sealed class As4Node
{
    private As4Node(string name, As4ConnectionFactory factory, IReadOnlyDictionary<string, As4Partner> partners)
    {
        Name = name;
        Factory = factory;
        Partners = partners;
    }

    /// <summary>Registry name of the factory.</summary>
    public string Name { get; }

    /// <summary>The node configuration.</summary>
    public As4ConnectionFactory Factory { get; }

    /// <summary>The node's partners, keyed by <see cref="As4Partner.Name"/> (ordinal).</summary>
    public IReadOnlyDictionary<string, As4Partner> Partners { get; }

    /// <summary>
    /// Looks up the factory and validates it with its partners together. Throws with the offending name on
    /// anything missing, of the wrong type, invalid, or ambiguous on receipt.
    /// </summary>
    public static As4Node Resolve(IRouteContext? context, string factoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryName);

        var factory = context.GetRequiredFromRegistry<As4ConnectionFactory>(factoryName);
        factory.Validate(factoryName);

        var partners = new Dictionary<string, As4Partner>(StringComparer.Ordinal);
        foreach (var partner in factory.Partners)
        {
            partner.Validate();
            partners[partner.Name!] = partner;
        }

        EnsureUnambiguous(factoryName, partners);

        return new As4Node(factoryName, factory, partners);
    }

    /// <summary>
    /// The agreement a received user message belongs to, matched as Domibus and Holodeck match a P-Mode: the
    /// message must be addressed to our party; the sender, service and action must be those of the partner's
    /// request leg or reply leg; an agreement reference, when the partner declares one, must be the message's.
    /// No match is <see cref="As4ErrorCode.ValueNotRecognized"/>; more than one — which node validation already
    /// prevents — <see cref="As4ErrorCode.ProcessingModeMismatch"/>.
    /// </summary>
    public (As4Partner Partner, bool IsReplyLeg) MatchInbound(Messaging.UserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // eDelivery AS4 1.16 REQUIRES one eb:PartyId per side. Refused as phase4 (CEF profile validator) and Holodeck
        // (P-Mode match by the whole set) refuse it; picking one of several would pick an arbitrary sender.
        foreach (var (side, party) in new[] { ("From", message.From), ("To", message.To) })
            if (party.PartyIds.Count > 1)
                throw new As4ProcessingException(As4ErrorCode.ProcessingModeMismatch,
                    $"PartyInfo/{side} carries {party.PartyIds.Count} eb:PartyId elements; eDelivery allows one.");

        // An agreement here is a push leg on the default MPC. Another mpc names a leg we do not have: EBMS:0001, as Domibus
        // drops a leg whose defaultMpc does not match (CachingPModeProvider.checkMpcMismatch); no mpc means the default.
        if (message.Mpc is { } mpc && mpc != Messaging.EbmsNamespaces.DefaultMpc)
            throw new As4ProcessingException(As4ErrorCode.ValueNotRecognized,
                $"the message is for mpc '{mpc}'; this node exchanges on the default MPC only.");

        if (!message.To.PartyIds.Any(p => p.Value == Factory.OurPartyId && p.Type == Factory.OurPartyIdType))
            throw new As4ProcessingException(As4ErrorCode.ValueNotRecognized,
                $"the message is addressed to '{string.Join(", ", message.To.PartyIds.Select(p => p.Value))}', not to this node.");

        var c = message.CollaborationInfo;
        var matches = new List<(As4Partner, bool)>();
        foreach (var partner in Partners.Values)
        {
            if (!message.From.PartyIds.Any(p => p.Value == partner.PartyId && p.Type == partner.PartyIdType)) continue;
            if (partner.AgreementRef is not null
                && (c.AgreementRef is null || c.AgreementRef.Value != partner.AgreementRef || c.AgreementRef.Type != partner.AgreementRefType))
                continue;

            if (Leg(partner.Service, partner.ServiceType, partner.Action))
                matches.Add((partner, false));
            else if (partner.ReplyLeg is { } reply && Leg(reply.Service, reply.ServiceType, reply.Action))
                matches.Add((partner, true));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new As4ProcessingException(As4ErrorCode.ValueNotRecognized,
                $"no agreement of node '{Name}' matches sender '{string.Join(", ", message.From.PartyIds.Select(p => p.Value))}', " +
                $"service '{c.Service.Value}', action '{c.Action}', agreement '{c.AgreementRef?.Value}'."),
            _ => throw new As4ProcessingException(As4ErrorCode.ProcessingModeMismatch,
                $"agreements {string.Join(", ", matches.Select(m => m.Item1.Name))} of node '{Name}' all match the message."),
        };

        bool Leg(string? service, string? serviceType, string? action) =>
            c.Service.Value == service && c.Service.Type == serviceType && c.Action == action;
    }

    /// <summary>
    /// Two partners that an incoming user message could not tell apart would make receipt depend on
    /// dictionary order. A message is matched on everything its agreement pins — agreement reference, sender,
    /// service and action, as Domibus and Holodeck match a P-Mode — so that combination must be unique across
    /// the node, for every leg. One agreement shared by many partners is fine: the sender tells them apart.
    /// </summary>
    private static void EnsureUnambiguous(string factoryName, IReadOnlyDictionary<string, As4Partner> partners)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        void Claim(As4Partner p, string partnerName, string? service, string? serviceType, string? action, string leg)
        {
            var key = string.Join('\n', p.AgreementRef, p.AgreementRefType, p.PartyId, p.PartyIdType, service, serviceType, action);
            if (byKey.TryGetValue(key, out var other))
                throw new InvalidOperationException(
                    $"AS4 connection factory '{factoryName}': partners '{other}' and '{partnerName}' ({leg}) share agreement, " +
                    $"sender, service and action ('{p.AgreementRef}', '{p.PartyId}', '{service}', '{action}'); " +
                    "a received message could not be matched to one agreement.");
            byKey[key] = partnerName;
        }

        foreach (var (name, p) in partners)
        {
            Claim(p, name, p.Service, p.ServiceType, p.Action, "request leg");
            if (p.ReplyLeg is { } reply)
                Claim(p, name, reply.Service, reply.ServiceType, reply.Action, "reply leg");
        }
    }
}
