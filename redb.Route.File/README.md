# redb.Route.File

File system transport for redb.Route. Polling consumer with glob filtering, read locking, and idempotency. Atomic file producer with temp-file writes, append, and conflict strategies.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.File?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.File)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.File
```

No additional dependencies.

## Usage

### Fluent DSL

```csharp
using redb.Route.File.Fluent;

// Poll directory for new files
From(FileDsl.Read("/data/incoming")
        .Include("*.csv")
        .Delay(5000)
        .SortBy(SortMode.LastModified)
        .MoveTo("/data/processed")
        .Idempotent())
    .Log("Processing: ${header.CamelFileName}")
    .To("direct://parse");

// Write files atomically
From("direct://export")
    .To(FileDsl.Write("/data/outgoing")
        .TempPrefix(".tmp-")
        .FileExist(FileExistStrategy.Override)
        .AutoCreate());

// Binary + append
From("direct://logs")
    .To(FileDsl.Write("/var/log/app")
        .FileExist(FileExistStrategy.Append)
        .AppendChars("\n"));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Consumer** | `FileDsl.Read(dir)`, `.Delay()`, `.InitialDelay()`, `.Include()`, `.Exclude()`, `.Recursive()`, `.SortBy()`, `.MaxMessagesPerPoll()`, `.MinAge()` |
| **Post-process** | `.Noop()`, `.Delete()`, `.MoveTo()`, `.MoveExisting()`, `.PreMove()` |
| **Idempotency** | `.Idempotent()`, `.DoneFileName()` |
| **Read Lock** | `.ReadLock(strategy)`, `.ReadLockTimeout()`, `.ReadLockCheckInterval()`, `.ReadLockMinAge()`, `.ReadLockMarkerFileExtension()` |
| **Producer** | `FileDsl.Write(dir)`, `.FileExist()`, `.TempPrefix()`, `.TempFileName()`, `.Charset()`, `.AutoCreate()`, `.AllowNullBody()`, `.AppendChars()`, `.EagerDeleteTargetFile()` |

## Read Lock Strategies

| Strategy | Description |
|----------|-------------|
| `None` | No locking |
| `MarkerFile` | Creates `.lock` marker file |
| `Changed` | Waits until file size stops changing |
| `FileLock` | OS-level file lock |
| `Rename` | Renames file during processing |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
