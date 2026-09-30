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
        .SortBy("Modified")
        .MoveTo("/data/processed")
        .Idempotent())
    .Log("Processing: ${header.redbFile.Name}")
    .To("direct://parse");

// Write files atomically
From("direct://export")
    .To(FileDsl.Write("/data/outgoing")
        .TempPrefix(".tmp-")
        .FileExist("Override")
        .AutoCreate());

// Binary + append
From("direct://logs")
    .To(FileDsl.Write("/var/log/app")
        .FileExist("Append")
        .AppendChars("\n"));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Consumer** | `FileDsl.Read(dir)`, `.Delay()`, `.InitialDelay()`, `.Include()`, `.Exclude()`, `.Recursive()`, `.SortBy()`, `.MaxMessagesPerPoll()`, `.MinAge()` |
| **Path filters** | `.AntInclude()`, `.AntExclude()`, `.AntFilterCaseSensitive()`, `.FilterDirectory()`, `.FilterFile()`, `.Filter()` — see [the base package](../redb.Route.GenericFile/README.md#path-filters) |
| **Poll backoff** | `.BackoffMultiplier()`, `.BackoffIdleThreshold()`, `.BackoffErrorThreshold()`, `.BackoffOnFailedExchanges()` |
| **Post-process** | `.Noop()`, `.Delete()`, `.MoveTo()`, `.MoveExisting()`, `.PreMove()` |
| **Idempotency** | `.Idempotent()`, `.DoneFileName()` |
| **Read Lock** | `.ReadLock(strategy)`, `.ReadLockTimeout()`, `.ReadLockCheckInterval()`, `.ReadLockMinAge()`, `.ReadLockMarkerFileExtension()` |
| **Producer** | `FileDsl.Write(dir)`, `.FileExist()`, `.TempPrefix()`, `.TempFileName()`, `.Charset()`, `.AutoCreate()`, `.AllowNullBody()`, `.AppendChars()`, `.EagerDeleteTargetFile()`, `.JailStartingDirectory()` |

## Read Lock Strategies

| Strategy | Description |
|----------|-------------|
| `None` | No locking |
| `MarkerFile` | Creates a `.redbLock` marker file next to the target |
| `Changed` | Waits until file size stops changing |
| `FileLock` | OS-level exclusive handle, held for the duration of processing |
| `Rename` | Renames the file while it is being processed |

## Expressions in options

Two groups, resolved at different moments:

| Option | What resolves |
|--------|---------------|
| `FileName` (producer) | Full expression engine against the exchange — `${header.x}`, `${body}`, functions |
| `MoveTo`, `PreMove`, `IdempotentKey`, `DoneFileName` (consumer) | File variables only: `${file:name}`, `${file:name.noext}` |

The consumer options are resolved from file metadata because they are needed before the
exchange exists (idempotency, pre-move) or while it is being disposed (post-processing).

## Producer safety

The target file name usually comes from the incoming message, so by default the producer
refuses to write outside its endpoint directory: a `../` name or an absolute path throws
`UnauthorizedAccessException`. Pass `.JailStartingDirectory(false)` when writing outside
is intended.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`), with `redb.route.endpoint`. The same holds for every
file-based connector built on `redb.Route.GenericFile` (`ftp`, `sftp`):

- **Consumer.** One `Consumer` span, `file read {scheme}`, per routed file. A file carries no trace context, so the span
  is a root, never a child of the activity the poll loop inherited from whoever started the routes. An empty poll opens
  none. A failed route, a timeout inside it included, marks it an error; our own stop does not.
- **Producer.** One `Producer` span per write, `file write {scheme}`; an error when the write fails, unless our own token
  cancelled it.
- `RouteEngineOptions.EnableTelemetry = false` opens neither span.
