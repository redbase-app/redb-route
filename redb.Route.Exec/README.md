# redb.Route.Exec

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Exec.svg)](https://www.nuget.org/packages/redb.Route.Exec/)

Process-execution transport for the [redb.Route](https://github.com/redbase-app/redb) ESB framework.
Run shell commands as part of a route — synchronously (request/response) or on a fixed schedule —
with first-class allowlisting, working-directory and environment overrides, wall-clock timeouts,
and stdout/stderr byte caps.

> Scheme: `exec`
> URI: `exec://run?command=git&args=status&timeoutMs=5000&allowedCommands=git,ls,pwd`

## Why

The framework already speaks 22+ transports (HTTP, Kafka, SQL, …) but reaching out to the
operating system itself was a gap. `redb.Route.Exec` closes it. Two patterns dominate:

- **Tool execution from an LLM agent.** Wire the producer into `.AsLlmTool("shell")` and the
  language model can issue commands through your route — with the same allowlist, timeout
  and audit you put in front of every other tool.
- **Scheduled jobs.** Need a cron-less daily backup, health probe, or log rotation? The
  consumer fires any command on a fixed interval and pipes the result into the next step
  of your route (Slack, Kafka, S3, redb persistence, …).

## Install

```bash
dotnet add package redb.Route.Exec
```

## Quick start — request/response producer

```csharp
using redb.Route.Exec;

services.AddRedbRoute()
        .AddRedbRouteExec();

routes.From(Direct.From("git-status"))
      .To(ExecDsl.Run("git")
                 .Args("status", "--short")
                 .WorkingDirectory("/srv/app")
                 .AllowedCommands("git")
                 .TimeoutMs(5_000));
```

The exchange `Out.Body` is JSON shaped for the LLM tool ABI:

```json
{ "stdout": "M README.md\n", "stderr": "", "exitCode": 0, "timedOut": false }
```

Headers also expose `redbExec.ExitCode`, `redbExec.Stdout`, `redbExec.Stderr`,
`redbExec.DurationMs`, `redbExec.TimedOut`, `redbExec.CommandLine`.

## Quick start — scheduled consumer

```csharp
routes.From(ExecDsl.Run("./scripts/health-check.sh")
                   .Schedule("5m")
                   .TimeoutMs(30_000))
      .Choice()
        .When(e => e.In.Headers["redbExec.ExitCode"]?.ToString() != "0")
          .To(slackAlerts)
        .Otherwise()
          .To(metricsLog);
```

`Schedule` accepts `500ms`, `30s`, `5m`, `1h`. For cron, chain `From("quartz://<cron>")`.

## Inputs (producer)

The producer resolves the command in this order:

1. **JSON body** — `{"command": "git", "args": ["status"]}`
2. **Headers** — `redbExec.Command` + `redbExec.Args`
3. **URI options** — `?command=git&args=status`

This is exactly the shape an LLM agent will produce when the producer is exposed as a
tool — no glue code required.

## Security

`AllowedCommands` is the primary control. When set, any request that resolves to a
command outside the list is rejected with `UnauthorizedAccessException`. Match is on the
file-name-only (case-insensitive), so `/usr/bin/git` and `git.exe` both match `git`.

Other controls:

- `WorkingDirectory` — pin the cwd; the spawned process cannot escape it on its own.
- `EnvironmentOverrides` — `KEY=VALUE,KEY2=VALUE2`. Overlaid on the host environment.
- `ScrubEnvironment` — start with an empty environment and apply only the overrides.
- `TimeoutMs` — wall-clock kill-switch. The whole process tree is terminated.
- `MaxStdoutBytes` / `MaxStderrBytes` — caps protect the host from runaway processes.

## Headers reference

| Header                  | Direction | Type   | Notes                                           |
| ----------------------- | --------- | ------ | ----------------------------------------------- |
| `redbExec.Command`      | in        | string | Overrides the URI option                        |
| `redbExec.Args`         | in        | string \| string[] | Whitespace-split or array            |
| `redbExec.ExitCode`     | out       | int    | `-1` when the process timed out                 |
| `redbExec.Stdout`       | out       | string | Capped at `MaxStdoutBytes`                      |
| `redbExec.Stderr`       | out       | string | Capped at `MaxStderrBytes`                      |
| `redbExec.StdoutBytes`  | out       | long   | Total bytes captured (post-cap may be lower)    |
| `redbExec.StderrBytes`  | out       | long   | Total bytes captured (post-cap may be lower)    |
| `redbExec.DurationMs`   | out       | long   | Wall-clock execution time                       |
| `redbExec.TimedOut`     | out       | bool   | `true` when the watchdog killed the process     |
| `redbExec.CommandLine`  | out       | string | Exact command line launched                     |

## License

Apache-2.0
