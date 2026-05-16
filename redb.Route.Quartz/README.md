# redb.Route.Quartz

Quartz.NET scheduling transport for redb.Route. Cron-expression schedules and interval timers backed by the Quartz scheduler with thread pool management.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Quartz?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Quartz)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
