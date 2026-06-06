# redb.Route.GenericFile

Shared base library for file-based transports in the [redb.Route](../../README.md) ESB framework.  
Provides abstract consumer, producer, options, and file-operations interfaces that concrete transports (**File**, **FTP**, **SFTP**) inherit from. Not used directly — include one of the transport-specific packages instead.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.GenericFile?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.GenericFile)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Architecture

```
GenericFileEndpointOptions          ← base options (polling, filtering, producer write)
 └─ RemoteFileEndpointOptions       ← + host/port/auth/reconnect

GenericFileConsumer<TOptions>       ← template-method poll loop
 └─ RemoteFileConsumer<TOptions>    ← + connect/reconnect lifecycle

GenericFileProducer<TOptions>       ← template-method write flow
 └─ RemoteFileProducer<TOptions>    ← + connect/reconnect lifecycle

IFileOperations                     ← protocol abstraction (list, read, write, move, delete)
 └─ IRemoteFileOperations           ← + connect/disconnect
```

Transport implementations (`File`, `Ftp`, `Sftp`) provide concrete `IFileOperations` and transport-specific headers/DSL. All polling logic, filtering, idempotency, post-processing, atomic write, and file-exist strategies are implemented in this base library.

## Consumer Pipeline

The `GenericFileConsumer` runs a poll loop with the following steps:

1. **List files** in the base directory (optionally recursive)
2. **Filter** — exclude temp/internal files (`.redb_*`, temp prefix), apply glob `include`/`exclude` patterns
3. **Sort** — by name, date, or size (ascending or descending)
4. **Limit** — apply `maxMessagesPerPoll`
5. **Per-file checks** — `minAge`, `maxAge`, done-file presence, idempotency repository
6. **Pre-move** — optionally move to a staging directory before processing
7. **Read body** — as `byte[]` (default) or `Stream` (`streamBody=true`)
8. **Invoke processor** — pass exchange to the downstream pipeline
9. **Post-process** — `Noop` (leave in place) / `Delete` / `MoveTo`
10. **Confirm** — mark idempotent key, delete done file

On processing failure: remove idempotent key, move to `moveFailed` directory (if configured).

## Producer Pipeline

The `GenericFileProducer` writes files with atomic temp-then-rename:

1. **Resolve body** — from `exchange.Out` (if set) or `exchange.In`
2. **Resolve target path** — from `FileName` option / header / auto-generated GUID
3. **Validate path** — jail-directory check (transport-specific)
4. **Create directories** — if `autoCreate=true`
5. **Write to temp file** — using `tempPrefix` or `tempFileName`
6. **Handle existing** — apply `fileExist` strategy (Override / Append / Fail / Ignore / Move / TryRename)
7. **Atomic rename** — move temp file to target
8. **Cleanup** — delete temp file on failure

## Options Reference

### GenericFileEndpointOptions (Base)

#### Consumer / Polling

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Delay` | `int` | `500` | Poll interval (ms) |
| `InitialDelay` | `int` | `0` | Delay before first poll (ms) |
| `Include` | `string?` | — | Glob include pattern (`*.csv,*.xml`) |
| `Exclude` | `string?` | — | Glob exclude pattern |
| `Recursive` | `bool` | `false` | Recurse subdirectories |
| `MaxDepth` | `int` | `0` | Max recursion depth (0 = unlimited) |
| `MinDepth` | `int` | `0` | Min depth for file selection |
| `SortBy` | `GenericFileSortBy` | `None` | Sort: `Name`, `NameDesc`, `Modified`, `ModifiedDesc`, `Size`, `SizeDesc` |
| `MaxMessagesPerPoll` | `int` | `0` | Max files per poll (0 = unlimited) |
| `MinAge` | `long` | `0` | Min file age (ms) |

#### Consumer / Post-Processing

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Noop` | `bool` | `false` | Leave file in place after processing |
| `Delete` | `bool` | `false` | Delete file after processing |
| `MoveTo` | `DynamicValue<string>?` | — | Move after processing (expression) |
| `MoveExisting` | `GenericFileExistStrategy` | `Override` | Strategy at move target |
| `PreMove` | `DynamicValue<string>?` | — | Move before processing |

Only one of `Noop`, `Delete`, `MoveTo` can be set.

#### Consumer / Idempotency

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Idempotent` | `bool` | `false` | Enable idempotent consumer |
| `IdempotentKey` | `DynamicValue<string>?` | — | Custom key expression |
| `DoneFileName` | `DynamicValue<string>?` | — | Done-file pattern (`${file:name}.done`) |

Default idempotent key: `"{fullPath}|{lastModifiedUtc:O}|{length}"`.

#### Consumer / Body

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StreamBody` | `bool` | `false` | `false` → `byte[]`, `true` → `Stream` |
| `Charset` | `string` | `utf-8` | Character encoding |

#### Producer

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FileName` | `DynamicValue<string>?` | — | Target file name (expression) |
| `FileExist` | `GenericFileExistStrategy` | `Override` | `Override`, `Append`, `Fail`, `Ignore`, `Move`, `TryRename` |
| `TempPrefix` | `DynamicValue<string>?` | — | Temp file prefix |
| `TempFileName` | `DynamicValue<string>?` | — | Full temp file name |
| `AutoCreate` | `bool` | `true` | Auto-create parent directories |
| `AllowNullBody` | `bool` | `false` | Allow null body (empty file) |
| `EagerDeleteTargetFile` | `bool` | `true` | Delete target before writing |
| `AppendChars` | `string?` | — | Chars appended after each write |

### RemoteFileEndpointOptions (extends Base)

#### Connection

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | `string` | `localhost` | Server hostname |
| `Port` | `int` | (transport-specific) | Server port |
| `Username` | `string?` | — | Auth username |
| `Password` | `string?` | — | Auth password |
| `ConnectionTimeout` | `int` | `30000` | Connection timeout (ms) |
| `OperationTimeout` | `int` | `60000` | Operation timeout (ms) |

#### Reconnection

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaximumReconnectAttempts` | `int` | `3` | Max reconnect attempts on failure |
| `ReconnectDelay` | `int` | `1000` | Delay between attempts (ms) |
| `Disconnect` | `bool` | `false` | Disconnect after each poll/write |

#### Consumer-Specific

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxAge` | `long` | `0` | Max file age (ms, 0 = unlimited) |
| `MoveFailed` | `DynamicValue<string>?` | — | Move on failure (expression) |
| `StartingDirectoryMustExist` | `bool` | `false` | Fail if base dir doesn't exist |
| `SendEmptyMessageWhenIdle` | `bool` | `false` | Send empty exchange when no files |

## Enums

### GenericFileExistStrategy

| Value | Description |
|-------|-------------|
| `Override` | Overwrite existing file |
| `Append` | Append to existing file |
| `Fail` | Throw exception |
| `Ignore` | Skip silently |
| `Move` | Rename existing file before writing |
| `TryRename` | Try alternate names |

### GenericFileSortBy

| Value | Description |
|-------|-------------|
| `None` | No sorting |
| `Name` / `NameDesc` | Sort by name |
| `Modified` / `ModifiedDesc` | Sort by last modified |
| `Size` / `SizeDesc` | Sort by size |

## File Operations Interface

Transport implementations provide `IFileOperations` (or `IRemoteFileOperations` for network transports):

- `ListFilesAsync` — enumerate files in a directory
- `ReadAllBytesAsync` / `OpenReadAsync` — read file contents
- `WriteAsync` (byte[] / Stream) / `AppendAsync` / `AppendTextAsync` — write
- `ExistsAsync` / `DeleteAsync` / `MoveAsync` — file operations
- `CreateDirectoryAsync` / `DirectoryExistsAsync` — directory operations
- Path helpers: `CombinePath`, `GetParentPath`, `GetFileName`, `GetExtension`, etc.

## Glob Patterns

Include/exclude patterns support comma-separated values and `*`/`?` wildcards:

```
*.csv          — all CSV files
*.csv,*.xml    — CSV and XML files
report_*       — files starting with "report_"
data?.txt      — data1.txt, dataA.txt, etc.
```

## Done File Substitutions

| Variable | Description |
|----------|-------------|
| `${file:name}` | Full file name with extension |
| `${file:name.noext}` | File name without extension |

## Requirements

- .NET 8.0 / 9.0 / 10.0
- `redb.Route` (core) — no external dependencies
