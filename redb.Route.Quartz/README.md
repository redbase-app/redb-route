# redb.Route.Quartz

Quartz.NET scheduling transport for redb.Route. Cron-expression schedules and interval timers backed by the Quartz scheduler with thread pool management.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Quartz?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Quartz)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Quartz
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Quartz.Fluent;

// Cron schedule — every weekday at 8:00 AM
From(Cron.Schedule("daily-report", "0 0 8 ? * MON-FRI"))
    .Log("Running daily report")
    .To("direct://generate-report");

// Cron with thread pool
From(Cron.Schedule("batch-job", "0 */15 * * * ?").Threads(4))
    .To("direct://batch-process");

// Interval timer — every 5 seconds
From(QTimer.Every("heartbeat").Period(5000).Delay(1000))
    .SetBody(_ => new { status = "alive", time = DateTime.UtcNow })
    .To("direct://monitor");

// Fixed-rate timer
From(QTimer.Every("metrics").Period(10000).FixedRate().Threads(2))
    .To("direct://collect-metrics");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Cron** | `Cron.Schedule(name, expression)`, `.Threads(int)` |
| **Timer** | `QTimer.Every(name)`, `.Period(ms)`, `.Delay(ms)`, `.FixedRate()`, `.Threads(int)` |

## Two Schemes

| Scheme | Component | Description |
|--------|-----------|-------------|
| `cron` | `CronComponent` | Cron expression scheduling (e.g., `0 0 8 ? * MON-FRI`) |
| `qtimer` | `QuartzTimerComponent` | Interval-based timer with optional initial delay |

Both schemes are consumer-only — they generate messages on schedule.

> Builder methods (Threads, Period, Delay) accept both constant values and `IExpression` for runtime resolution via the expression engine.

## The scheduler of a context

As Apache Camel's `QuartzComponent`, each route context has one scheduler, created by its first `cron:`/`qtimer:`
consumer — a context without such a route creates none:

- an `IScheduler` handed to the context (`context.AddService(typeof(IScheduler), scheduler)`) is used as it is; its host
  owns it;
- otherwise the context builds its own, from `QuartzSchedulerOptions` when the context or DI has them (store, cluster,
  serializer, thread pool), in memory without them, named `{InstanceNamePrefix}-{context id}`, and shuts it down when
  the context stops.

Either is started once the context has started — every consumer registered before the first fire (camel-quartz
`autoStartScheduler`); a route started later in a running context starts it at once.

```csharp
// A host shares the options, not a scheduler: every context gets its own, its own job namespace and, in a clustered
// store, a cluster of the nodes that run that context.
context.AddService(typeof(QuartzSchedulerOptions), new QuartzSchedulerOptions
{
    InstanceNamePrefix = "MyCluster",
    Properties = new Dictionary<string, string>
    {
        ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
        ["quartz.jobStore.clustered"] = "true",
        // dataSource, driverDelegateType, serializer, threadPool...
    },
});
```

A persistent store keys the scheduler by the context id, so the context needs a given id (`new RouteContext("billing")`):
with a generated one the scheduler name would differ on every start and every node, and starting fails with a message.

## One job, one consumer

The job key is `{groupName}.{jobName}` — `redb` and the endpoint path by default (`cron://nightly` → `redb.nightly`).
It depends on the URI only, so it is the same after a restart and on every node of a cluster. On one scheduler a job key
has one consumer: a second claimant — the same path with another schedule, `cron://nightly` beside `qtimer://nightly`, two
contexts deliberately sharing one scheduler — fails at its start, naming both endpoints, instead of silently replacing the
first one's schedule. Give it its own name: another path, or `jobName=` / `groupName=`.

## A persistent or clustered store

The store is shared — with this node's next start, or with the other nodes of a cluster — so a node never decides about
the schedule by itself:

- stopping a route stops this node's consumer only; `deleteJob` / `pauseJob` apply to an in-memory store (a warning
  when set on a persistent one); pause or remove a schedule for the cluster explicitly;
- a fire taken by a node that runs no consumer for it — a rolling upgrade, a route stopped on one node — is skipped and
  logged once; the job stays in the store (in memory it is cleaned up, it was the process's own);
- nodes that share a scheduler name must carry the same schedules: separate roles get separate names. With a scheduler
  per context the cluster of a context is made of the nodes that run it;
- `.Cluster(true)` on a Quartz route over a clustered store is a warning when a leader-only policy is attached: Quartz
  already fires a job on one node, and a leader-only route loses the fires the other nodes take. A host may decline the
  policy for Quartz routes (redb.Tsak does); the route then runs on every node.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`), with `redb.system` = the scheme (`cron`, `qtimer`) and
`redb.route.endpoint`: one `Consumer` span per fire, `{scheme} receive`. A fire carries no trace context, so the span is
a root, never a child of the activity the scheduler thread holds; the route is its child. A failed route marks it an
error; our own stop does not. `RouteEngineOptions.EnableTelemetry = false` opens none.
