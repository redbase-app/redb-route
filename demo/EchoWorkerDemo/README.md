# EchoWorkerDemo — минимальный модуль redb.Route + отладочный хост

Два проекта. Одна точка входа `InitRoute.main` — её зовёт и настоящий Tsak-воркер, и
отладочный хост, поэтому код роутов не дублируется.

```
EchoWorkerDemo/
├─ EchoModule/          ← модуль (classlib → EchoModule.tpkg)
│  ├─ InitRoute.cs      ← main(IRouteContext): 2 роута + класс Note
│  ├─ manifest.json     ← { Name, Version, EntryPoints:["EchoModule.dll"] }
│  └─ EchoModule.csproj ← + таргет PackTpkg (зипует manifest + DLL)
└─ EchoWorker/          ← отладочный хост (exe)
   ├─ Program.cs        ← redb на SQLite + InitRoute.main(ctx) + Start
   └─ EchoWorker.csproj
```

## Эндпоинты (один HTTP-сервер, порт 5099)

| Метод | Путь | Что делает |
|---|---|---|
| POST | `/api/notes` | тело `{"tag":"work","text":"hello"}` → сохранить заметку в redb |
| GET | `/api/notes?tag=work` | вернуть заметки с этим тегом (`Where(n => n.Tag == tag).ToListAsync()`) |

Два роута на одном пути расходятся по `?methods=POST` / `?methods=GET`; чужой метод → 405.

## Отладка (без Tsak)

```bash
dotnet run --project EchoWorker
```
```bash
# PowerShell: curl.exe, иначе curl = алиас Invoke-WebRequest
curl.exe -X POST http://localhost:5099/api/notes -H "Content-Type: application/json" -d "{\"tag\":\"work\",\"text\":\"hello\"}"
curl.exe "http://localhost:5099/api/notes?tag=work"
```
Хранилище — `echo_demo.db` (SQLite Free, тот же тир, что у Tsak по умолчанию).

## Сборка .tpkg и деплой в воркер

```bash
dotnet build EchoModule -c Debug
# → EchoModule/output/EchoModule.tpkg  (внутри: EchoModule.dll + manifest.json)
```
Скопировать `.tpkg` в каталог модулей Tsak-воркера (`modules/`) — воркер подхватит его
горячим reload'ом. redb.Route.Http / redb.Route.Core / redb.Core воркер уже несёт в
своих shared-библиотеках, поэтому tpkg остаётся лёгким (только DLL модуля + манифест).
