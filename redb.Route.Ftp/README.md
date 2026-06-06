# redb.Route.Ftp

FTP/FTPS transport for the [redb.Route](../../README.md) ESB framework.  
Polling consumer and atomic producer powered by FluentFTP — passive/active mode, FTPS (TLS), glob filtering, idempotency, temp-file upload, recursive directories, and jail-path protection.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Ftp?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Ftp)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Quick Start

```csharp
// Poll remote FTP directory
.From(Ftp.Directory("/inbox")
    .Host("ftp.example.com")
    .Username("user").Password("secret")
    .Include("*.csv")
    .Delay(30000)
    .MoveTo("/inbox/archive")
    .Recursive()
    .Idempotent())

// Upload files atomically
.To(Ftp.Directory("/outbox")
    .Host("ftp.example.com")
    .Username("user").Password("secret")
    .TempPrefix(".uploading-")
    .AutoCreate())

// FTPS with passive mode
.From(Ftp.Directory("/secure")
    .Host("ftp.example.com")
    .UseFtps()
    .ValidateCertificate(false)
    .PassiveMode())
```

## URI Format

```
ftp:///remote/path?host=server&port=21&username=admin&password=secret&param=value
```

The path part of the URI is the remote base directory. All other parameters go in the query string.

## Consumer

The consumer polls a remote FTP directory at a configurable interval, delivers each file as an exchange, and then applies post-processing (delete / move / noop).

### Polling Pipeline

1. Connect to FTP server
2. List files in the remote directory (optionally recursive)
3. Apply filters: glob include/exclude, min/max age, idempotency, done-file check
4. Sort results (by name, date, or size)
5. For each file: pre-move → read body → invoke processor → post-process
6. On failure: move file to `moveFailed` directory (if configured)

### Consumer Headers

| Header | Type | Description |
|--------|------|-------------|
| `redbFtp.Name` | `string` | Full file name (with extension) |
| `redbFtp.NameOnly` | `string` | File name without extension |
| `redbFtp.Extension` | `string` | File extension (e.g. `.csv`) |
| `redbFtp.RemotePath` | `string` | Full remote path of the file |
| `redbFtp.RelativePath` | `string` | Path relative to the base directory |
| `redbFtp.RemoteParent` | `string` | Parent directory path |
| `redbFtp.Length` | `long` | File size in bytes |
| `redbFtp.LastModified` | `DateTimeOffset` | Last modified timestamp |
| `redbFtp.Host` | `string` | FTP host |
| `redbFtp.Port` | `int` | FTP port |
| `redbFtp.Username` | `string` | FTP username |

### Post-Processing Modes

| Mode | Description |
|------|-------------|
| `Noop()` | Leave the file in place (default) |
| `Delete()` | Delete after successful processing |
| `MoveTo("/archive")` | Move to another directory after processing |

Only one mode can be active at a time.

### Idempotency

Prevents re-processing the same file. Default key: `"{fullPath}|{lastModifiedUtc}|{length}"`.

```csharp
.From(Ftp.Directory("/inbox")
    .Host("ftp.example.com")
    .Idempotent()                          // default key
    .Idempotent(Constant("${file.name}"))) // custom key expression
```

### Done Files

Only process a file when a companion "done" marker exists:

```csharp
.From(Ftp.Directory("/inbox")
    .DoneFileName(Constant("${file:name}.done")))
```

Supports `${file:name}` and `${file:name.noext}` substitutions.

## Producer

The producer uploads files to the FTP server using an atomic temp-file-then-rename strategy.

### Write Pipeline

1. Resolve target file name (from options, header, or auto-generated GUID)
2. Validate path against jail directory
3. Create parent directories if `autoCreate=true`
4. Write body to a temp file (configurable prefix/name)
5. Handle existing file (Override / Append / Fail / Ignore / Move / TryRename)
6. Atomically rename temp file to target

### File Name Resolution

Priority order:
1. `FileName` option (dynamic expression)
2. `redbFtp.Name` header
3. `redbFile.Name` header (generic file header)
4. Auto-generated: `redb-{GUID}`

### File Exist Strategies

| Strategy | Description |
|----------|-------------|
| `Override` | Overwrite existing file (default) |
| `Append` | Append to existing file |
| `Fail` | Throw exception |
| `Ignore` | Skip silently |
| `Move` | Rename existing file before writing |
| `TryRename` | Try alternate names |

### Move Existing Strategies

When `FileExist=Move`, the existing file is renamed with a suffix:

| Strategy | Suffix Example |
|----------|---------------|
| `Timestamp` | `.20260405143022567` (default) |
| `Backup` | `.bak` |
| `Guid` | `.a1b2c3d4e5f6...` |

### Jail Path Protection

By default (`jailStartingDirectory=true`), the producer rejects any path that resolves outside the base directory via `..` traversal. Set `JailStartingDirectory(false)` to disable.

### Flatten

When `flatten=true`, directory components in the file name are stripped — only the base name is used for the target path.

### Producer Header

| Header | Description |
|--------|-------------|
| `redbFtp.NameProduced` | Full path of the written file |

## FTPS (FTP over TLS)

```csharp
.From(Ftp.Directory("/secure")
    .Host("ftp.example.com")
    .UseFtps()                      // Explicit TLS
    .ValidateCertificate(false))    // Skip certificate validation
```

Uses `FtpEncryptionMode.Explicit`. Certificate validation is enabled by default.

## Data Connection Mode

```csharp
.From(Ftp.Directory("/data")
    .PassiveMode())   // default — AutoPassive

.From(Ftp.Directory("/data")
    .ActiveMode())    // AutoActive
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Host()`, `.Port()`, `.Username()`, `.Password()`, `.ConnectionTimeout()`, `.OperationTimeout()`, `.PassiveMode()`, `.ActiveMode()` |
| **TLS** | `.UseFtps()`, `.ValidateCertificate()` |
| **Reconnect** | `.MaximumReconnectAttempts()`, `.ReconnectDelay()`, `.Disconnect()` |
| **Consumer** | `.Delay()`, `.InitialDelay()`, `.Include()`, `.Exclude()`, `.Recursive()`, `.MaxDepth()`, `.MinDepth()`, `.SortBy()`, `.MaxMessagesPerPoll()`, `.MinAge()`, `.MaxAge()` |
| **Post-process** | `.Noop()`, `.Delete()`, `.MoveTo()`, `.MoveExisting()`, `.PreMove()`, `.MoveFailed()` |
| **Idempotency** | `.Idempotent()`, `.DoneFileName()` |
| **Transfer** | `.TransferType()` (Binary/Ascii), `.StreamBody()`, `.Charset()`, `.IgnoreFileNotFoundOrPermissionError()`, `.StartingDirectoryMustExist()`, `.SendEmptyMessageWhenIdle()` |
| **Producer** | `.FileName()`, `.FileExist()`, `.MoveExistingFileStrategy()`, `.TempPrefix()`, `.TempFileName()`, `.AutoCreate()`, `.AllowNullBody()`, `.EagerDeleteTargetFile()`, `.Flatten()`, `.JailStartingDirectory()`, `.AppendChars()` |

Most builder methods accept both constant values and `IExpression` for runtime resolution.

## Configuration Reference

### Connection

| Parameter | Default | Description |
|-----------|---------|-------------|
| `host` | `localhost` | FTP server hostname |
| `port` | `21` | FTP server port |
| `username` | — | FTP username |
| `password` | — | FTP password |
| `connectionTimeout` | `30000` | Connection timeout (ms) |
| `operationTimeout` | `60000` | Operation timeout (ms) |
| `passiveMode` | `true` | Use passive data connection |
| `useFtps` | `false` | Enable FTPS (Explicit TLS) |
| `validateCertificate` | `true` | Validate server certificate |
| `transferType` | `Binary` | `Binary` or `Ascii` |

### Reconnection

| Parameter | Default | Description |
|-----------|---------|-------------|
| `maximumReconnectAttempts` | `3` | Max reconnect attempts |
| `reconnectDelay` | `1000` | Delay between reconnect attempts (ms) |
| `disconnect` | `false` | Disconnect after each poll/write |

### Consumer / Polling

| Parameter | Default | Description |
|-----------|---------|-------------|
| `delay` | `60000` | Poll interval (ms) |
| `initialDelay` | `1000` | Delay before first poll (ms) |
| `include` | — | Glob include pattern (e.g. `*.csv,*.xml`) |
| `exclude` | — | Glob exclude pattern |
| `recursive` | `false` | Recurse into subdirectories |
| `maxDepth` | `0` | Max recursion depth (0 = unlimited) |
| `minDepth` | `0` | Min depth for file selection |
| `sortBy` | `None` | Sort order: `Name`, `NameDesc`, `Modified`, `ModifiedDesc`, `Size`, `SizeDesc` |
| `maxMessagesPerPoll` | `0` | Max files per poll (0 = unlimited) |
| `minAge` | `0` | Min file age (ms) |
| `maxAge` | `0` | Max file age (ms, 0 = unlimited) |
| `startingDirectoryMustExist` | `true` | Fail if base directory doesn't exist |
| `sendEmptyMessageWhenIdle` | `false` | Send empty exchange when no files found |

### Consumer / Post-Processing

| Parameter | Default | Description |
|-----------|---------|-------------|
| `noop` | `false` | Leave file in place |
| `delete` | `false` | Delete after processing |
| `moveTo` | — | Move after processing (expression) |
| `preMove` | — | Move before processing (expression) |
| `moveFailed` | — | Move on failure (expression) |
| `moveExisting` | `Override` | How to handle existing file at move target |

### Consumer / Idempotency

| Parameter | Default | Description |
|-----------|---------|-------------|
| `idempotent` | `false` | Enable idempotent consumer |
| `idempotentKey` | — | Custom idempotency key expression |
| `doneFileName` | — | Done-file pattern (e.g. `${file:name}.done`) |

### Consumer / Body

| Parameter | Default | Description |
|-----------|---------|-------------|
| `streamBody` | `false` | `true` → body is `Stream`, `false` → `byte[]` |
| `charset` | `utf-8` | Character encoding |

### Producer

| Parameter | Default | Description |
|-----------|---------|-------------|
| `fileName` | — | Target file name (expression) |
| `fileExist` | `Override` | Strategy: `Override`, `Append`, `Fail`, `Ignore`, `Move`, `TryRename` |
| `moveExistingFileStrategy` | `Timestamp` | Rename strategy for existing files: `Backup`, `Timestamp`, `Guid` |
| `tempPrefix` | — | Temp file prefix (expression) |
| `tempFileName` | — | Temp file name (expression) |
| `autoCreate` | `true` | Auto-create parent directories |
| `allowNullBody` | `false` | Allow null body (creates empty file) |
| `eagerDeleteTargetFile` | `true` | Delete target before writing |
| `flatten` | `false` | Strip directory components from file name |
| `jailStartingDirectory` | `true` | Reject paths outside the base directory |
| `appendChars` | — | Characters to append after each write |
| `ignoreFileNotFoundOrPermissionError` | `false` | Ignore listing errors for missing/inaccessible dirs |

## DI Registration

```csharp
services.AddRedbRouteFtp();
```

## Requirements

- .NET 8.0 / 9.0 / 10.0
- `FluentFTP` 54.1.0
- `redb.Route.GenericFile` (shared file transport abstractions)
