# EchoWorkerDemo — a minimal redb.Route module + a debug host

Two projects, one entry point. `InitRoute.main` is called by both the real Tsak
runtime and the debug host, so the route code is never duplicated.

```
EchoWorkerDemo/
├─ EchoModule/          <- the module (class library -> EchoModule.tpkg)
│  ├─ InitRoute.cs      <- main(IRouteContext): 2 routes + the Note class
│  ├─ manifest.json     <- { Name, Version, EntryPoints: ["EchoModule.dll"] }
│  └─ EchoModule.csproj <- + the PackTpkg target (zips manifest + DLL)
└─ EchoWorker/          <- the debug host (exe)
   ├─ Program.cs        <- redb on SQLite + InitRoute.main(ctx) + Start
   └─ EchoWorker.csproj
```

## Endpoints (one HTTP server, port 5099)

| Method | Path | What it does |
|---|---|---|
| POST | `/api/notes` | body `{"tag":"work","text":"hello"}` -> save a note into redb |
| GET | `/api/notes?tag=work` | return notes with that tag (`Where(n => n.Tag == tag).ToListAsync()`) |

Both routes share the same path and split on method via `?methods=POST` / `?methods=GET`;
a wrong method returns 405.

## Debug (no Tsak)

```bash
dotnet run --project EchoWorker
```

```powershell
# PowerShell: use curl.exe (curl is an alias for Invoke-WebRequest), JSON in single quotes
curl.exe -X POST http://localhost:5099/api/notes -H "Content-Type: application/json" -d '{"tag":"work","text":"hello"}'
curl.exe "http://localhost:5099/api/notes?tag=work"
```

```cmd
:: cmd.exe: escape the inner quotes
curl -X POST http://localhost:5099/api/notes -H "Content-Type: application/json" -d "{\"tag\":\"work\",\"text\":\"hello\"}"
```

Storage is `echo_demo.db` (SQLite, Free tier — the same tier the Tsak worker uses by default).

## Build the .tpkg and deploy to a worker

```bash
dotnet build EchoModule -c Debug
# -> EchoModule/output/EchoModule.tpkg  (inside: EchoModule.dll + manifest.json + EchoModule.config.json)
```

Copy the `.tpkg` into the Tsak worker's module folder (`modules/`) — the worker picks it up
via hot-reload. `redb.Route.Http` / `redb.Route.Core` / `redb.Core` already ship in the worker's
shared libraries, so the `.tpkg` stays tiny (just the module DLL + descriptors).
