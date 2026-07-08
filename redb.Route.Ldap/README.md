# redb.Route.Ldap

LDAP / Active Directory connector for redb.Route. Search, CRUD, authentication, and change tracking — all directory operations as endpoints via Novell.Directory.Ldap.NETStandard.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Ldap?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Ldap)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Ldap
```

## Usage

### URI Format

```
ldap:OPERATION:baseDn?server=host&port=389&bindDn=cn=admin,dc=example,dc=com&bindPassword=secret
```

### Fluent DSL

```csharp
using redb.Route.Ldap;

// Search users
From(Ldap.Search("ou=users,dc=example,dc=com")
        .Server("ldap.example.com")
        .BindDn("cn=admin,dc=example,dc=com")
        .BindPassword("secret")
        .Filter("(objectClass=inetOrgPerson)")
        .Scope(LdapSearchScope.Subtree)
        .Attributes("cn", "mail", "uid")
        .PageSize(500))
    .Log("Found: ${header['redbLdap.ResultCount']} entries")
    .To("direct://process");

// Watch for changes (consumer — polls for modifications)
From(Ldap.Watch("ou=users,dc=example,dc=com")
        .Server("ldap.example.com")
        .BindDn("cn=admin,dc=example,dc=com")
        .BindPassword("secret")
        .ChangeTracking(LdapChangeTrackingMode.ModifyTimestamp)
        .PollInterval(5000)
        .InitialLoad())
    .Log("Changed entry: ${body}")
    .To("direct://sync");

// Add entry
From("direct://create-user")
    .To(Ldap.Add("ou=users,dc=example,dc=com")
        .Server("ldap.example.com")
        .BindDn("cn=admin,dc=example,dc=com")
        .BindPassword("secret"));

// Modify entry
From("direct://update-user")
    .To(Ldap.Modify("cn=alice,ou=users,dc=example,dc=com")
        .Server("ldap.example.com")
        .BindDn("cn=admin,dc=example,dc=com")
        .BindPassword("secret"));

// Bind (authentication check)
From("direct://authenticate")
    .To(Ldap.Bind("dc=example,dc=com")
        .Server("ldap.example.com"));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Operations** | `Ldap.Search()`, `Ldap.Compare()`, `Ldap.Add()`, `Ldap.Modify()`, `Ldap.Delete()`, `Ldap.Rename()`, `Ldap.Bind()`, `Ldap.Watch()` |
| **Connection** | `.Server()`, `.Port()`, `.Ssl()`, `.StartTls()`, `.ConnectionFactory()`, `.ConnectTimeout()`, `.OperationTimeout()` |
| **Auth** | `.BindDn()`, `.BindPassword()` |
| **Search** | `.Filter()`, `.Scope()`, `.Attributes()`, `.PageSize()`, `.SizeLimit()`, `.TimeLimit()` |
| **Consumer** | `.PollInterval()`, `.ChangeTracking()`, `.InitialLoad()` |
| **Protocol** | `.ProtocolVersion()`, `.Referrals()` |
| **Pool** | `.MaxConnections()` |
| **TLS** | `.SkipCertificateValidation()`, `.ClientCert()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Exchange Headers

| Header | Description |
|--------|-------------|
| `redbLdap.Operation` | Operation type (Search, Add, etc.) |
| `redbLdap.BaseDn` | Base DN used in the operation |
| `redbLdap.Filter` | LDAP filter applied |
| `redbLdap.Scope` | Search scope (Base, OneLevel, Subtree) |
| `redbLdap.ResultCount` | Number of entries returned |
| `redbLdap.SearchTime` | Search duration in milliseconds |
| `redbLdap.Server` | Target LDAP server |
| `redbLdap.Port` | Target port |
| `redbLdap.Ssl` | Whether SSL/TLS was used |
| `redbLdap.ChangeType` | Change type for Watch consumer |

## Change Tracking Modes

| Mode | Description |
|------|-------------|
| `ModifyTimestamp` | Polls using `modifyTimestamp` attribute (standard LDAP) |
| `Usn` | Polls using `uSNChanged` attribute (Active Directory) |
| `Persistent` | Persistent search control (if supported by server) |

## Docker (for testing)

```yaml
services:
  openldap:
    image: osixia/openldap:1.5.0
    ports:
      - "389:389"
      - "636:636"
    environment:
      LDAP_ORGANISATION: "redb"
      LDAP_DOMAIN: "redb.test"
      LDAP_ADMIN_PASSWORD: "admin"
```

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
