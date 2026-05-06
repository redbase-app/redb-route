# redb.Route — Connectors Roadmap

> Базовые коннекторы (компоненты) для покрытия основных протоколов.
> SQL-коннектор (`redb.Route.Sql`) — отдельно, будет расширением через `redb.Core`.

## Текущее состояние

| Пакет | Scheme | Статус | Тесты |
|---|---|---|---|
| `redb.Route` (core) | `direct:`, `seda:`, `timer:`, `log:`, `mock:`, `validator:` | ✅ Готово | 844 × 3 TFM |
| `redb.Route.Kafka` | `kafka:` | ✅ Готово | 54 × 3 TFM |
| `redb.Route.RabbitMQ` | `rabbitmq:` | ✅ Готово | 48 × 3 TFM |
| `redb.Route.Redis` | `redis:` | ✅ Готово | 134 × 3 TFM |
| `redb.Route.Amqp` | `amqp:` | ✅ Готово | 83 × 3 TFM |
| `redb.Route.Http` | `http:`, `https:` | ✅ Готово | 119 × 3 TFM |
| `redb.Route.Tcp` | `tcp:` | ✅ Готово | 89 × 3 TFM |
| `redb.Route.WebSocket` | `ws:`, `wss:` | ✅ Готово | 79 × 3 TFM |
| `redb.Route.Grpc` | `grpc:` | ✅ Готово | 64 × 3 TFM |
| `redb.Route.File` | `file:` | ✅ Готово | 104 × 3 TFM |
| `redb.Route.Mail` | `smtp:`, `pop3:`, `imap:` | ✅ Готово | 95 × 3 TFM |
| `redb.Route.Quartz` | `quartz:` | ✅ Готово | 62 × 3 TFM |
| `redb.Route.Sftp` | `sftp:` | ✅ Готово | 180 × 3 TFM |
| `redb.Route.IbmMq` | `wmq:` | ✅ Готово | 99 × 3 TFM |

---

## Спецификации реализованных коннекторов

### 1. `redb.Route.File` — файловый коннектор ✅
- **Scheme:** `file:`
- **Зависимости:** 0 (BCL `System.IO`)
- **Producer:** запись файлов (Append / Overwrite / TempRename)
- **Consumer:** polling каталога с фильтрами
- **Ключевые фичи:**
  - `fileName` — динамическое имя через Simple-выражения (`${header.orderId}.json`)
  - `fileExist` — стратегия: `Append`, `Override`, `Fail`, `Ignore`
  - `noop=true` — не перемещать и не удалять файл после обработки
  - `moveTo` / `delete=true` — после обработки
  - `idempotent=true` — пропускать уже обработанные файлы
  - `include` / `exclude` — glob-фильтры (`*.csv`, `*.json`)
  - `recursive=true` — рекурсивный обход подкаталогов
  - `delay` — интервал polling в мс
  - `sortBy` — сортировка файлов (name, date, size)
  - `charset` — кодировка (default: UTF-8)
  - `tempPrefix` — запись через temp-файл с последующим rename (атомарность)
- **DSL-примеры:**
```csharp
// Поллинг каталога, идемпотентная обработка CSV
.From("file:C:/input?include=*.csv&noop=true&idempotent=true&delay=5000")

// Запись результата в файл с динамическим именем
.To("file:C:/output?fileName=${header.orderId}.json&fileExist=Append")

// Перемещение обработанных файлов
.From("file:C:/inbox?moveTo=C:/archive&delay=10000")
```

---

### 2. `redb.Route.Http` — HTTP-коннектор ✅
- **Scheme:** `http:`, `https:`
- **Зависимости:** 0 (BCL `HttpClient`; ASP.NET опционально для consumer)
- **Producer:** HTTP-клиент (GET/POST/PUT/DELETE)
- **Consumer:** встроенный HTTP-сервер (webhook receiver)
- **Ключевые фичи:**
  - `method` — HTTP-метод (default: GET для read, POST для write)
  - `timeout` — таймаут запроса
  - `throwOnError=true` — бросать исключение на 4xx/5xx
  - Headers: `exchange.In.Headers["Content-Type"]` → HTTP-заголовки
  - Body: exchange body → HTTP body, response body → exchange Out body
  - Query параметры из URI или из headers
  - Basic Auth / Bearer Token через headers или URI-параметры
  - Consumer: bind address, allowed methods, CORS
- **DSL-примеры:**
```csharp
// POST запрос к внешнему API
.To("https:api.example.com/orders?method=POST&timeout=30000")

// GET с bearer token
.To("http:api.example.com/users?method=GET&authToken=${header.token}")

// Webhook receiver (consumer)
.From("http:0.0.0.0:8080/webhook?methods=POST")
```

---

### 3. `redb.Route.Tcp` — TCP-коннектор ✅
- **Scheme:** `tcp:`
- **Зависимости:** 0 (BCL `System.Net.Sockets`)
- **Producer:** TCP-клиент (отправка данных)
- **Consumer:** TCP-сервер (приём подключений)
- **Ключевые фичи:**
  - `codec` — фреймирование: `LineDelimited` (`\n`), `LengthPrefix` (4-byte BE), `Raw`, `FixedLength`
  - `maxConnections` — лимит подключений для сервера
  - `keepAlive=true` — persistent connections
  - `bufferSize` — размер буфера чтения
  - `idleTimeout` — таймаут неактивного соединения
  - `ICodec` интерфейс для кастомного фреймирования
- **DSL-примеры:**
```csharp
// TCP-клиент: отправка с line-delimited framing
.To("tcp:192.168.1.100:9000?codec=LineDelimited")

// TCP-сервер: приём подключений
.From("tcp:0.0.0.0:9000?codec=LengthPrefix&maxConnections=100")
```

---

### 4. `redb.Route.Mail` — Email-коннектор (SMTP + POP3 + IMAP) ✅
- **Scheme:** `smtp:`, `pop3:`, `imap:`
- **Зависимости:** **MailKit** (единый пакет для всех email-протоколов)
- **Компоненты:** `SmtpComponent`, `Pop3Component`, `ImapComponent`
- **Ключевые фичи:**
  - SMTP Producer: отправка email (To/CC/BCC из headers, body → email body)
  - POP3 Consumer: поллинг входящих, `delete=true`
  - IMAP Consumer: поллинг с IDLE push, folder select, unseen filter
  - TLS/SSL: `tls=true`, `port=587` / `port=993`
  - Вложения: `exchange.In.Headers["Attachments"]` → `MimePart[]`
  - HTML/Plain: `contentType=text/html`
- **DSL-примеры:**
```csharp
// Отправка email
.To("smtp:mail.example.com:587?username=bot@ex.com&password=xxx&tls=true")

// Поллинг входящих по POP3
.From("pop3:mail.example.com?username=inbox@ex.com&password=xxx&delete=true&delay=60000")

// IMAP с IDLE push
.From("imap:mail.example.com:993?username=inbox@ex.com&password=xxx&folder=INBOX&unseen=true&tls=true")
```

---

### 5. `redb.Route.Sftp` — SFTP-коннектор ✅
- **Scheme:** `sftp:`, `ftp:`
- **Зависимости:** **SSH.NET** (SFTP), **FluentFTP** (FTP, опционально)
- **Producer:** upload файлов на удалённый сервер
- **Consumer:** polling удалённого каталога
- **Ключевые фичи:**
  - Аналог File, но удалённый: `include`, `exclude`, `moveTo`, `delete`, `noop`, `idempotent`
  - Auth: `username/password` или `privateKey` (путь к SSH-ключу)
  - `knownHosts` — проверка ключа сервера
  - `tempPrefix` — атомарная загрузка через temp-файл
  - `stepwise=true` — пошаговый cd (для совместимости с кривыми серверами)
  - FTP: active/passive mode, binary/ascii
- **DSL-примеры:**
```csharp
// SFTP: download с удалённого сервера
.From("sftp:sftp.example.com/incoming?username=user&privateKey=~/.ssh/id_rsa&delay=30000")

// SFTP: upload
.To("sftp:sftp.example.com/outgoing?username=user&password=xxx&tempPrefix=.tmp")

// FTP: passive mode
.From("ftp:ftp.example.com/data?username=ftp&password=xxx&passive=true")
```

---

### 6. `redb.Route.IbmMq` — IBM MQ коннектор ✅
- **Scheme:** `wmq:`
- **Зависимости:** **IBMMQDotnetClient** 9.4.1.1 (managed .NET client)
- **Producer:** отправка сообщений в очереди и топики (immediate / transacted / RPC)
- **Consumer:** получение сообщений через MQGET polling с backout
- **Ключевые фичи:**
  - `destinationType` — Queue (default) / Topic
  - `concurrentConsumers` — параллельные потребители
  - `waitInterval` — интервал MQGET в мс
  - `transacted=true` — MQCMIT/MQBACK транзакции
  - `persistence` — App / Persistent / NonPersistent
  - `backoutThreshold` / `backoutQueue` — автоматический dead-letter
  - `rpcEnabled=true` — Request/Reply с динамической reply-queue
  - `rpcTimeout` — таймаут RPC в мс (default: 20000)
  - `correlationPattern` — MsgId / CorrelId для RPC
  - `sslCipherSpec` / `sslPeerName` — TLS/SSL
  - W3C Distributed Tracing через message properties
  - MQMD-заголовки ↔ Exchange headers (MsgId, CorrelId, Format, CCSID, Priority, Expiry, ReplyToQueue и т.д.)
- **DSL-примеры:**
```csharp
// Отправка в очередь
.To(Wmq.Queue("DEV.QUEUE.1")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .User("app").Password("passw0rd"))

// Получение из очереди с транзакциями
.From(Wmq.Queue("ORDERS.IN")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .Transacted(true)
    .BackoutThreshold(3)
    .BackoutQueue("ORDERS.DLQ"))

// RPC запрос
.To(Wmq.Queue("REQUEST.Q")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .RpcEnabled(true).RpcTimeout(30000))
```

---

### 7. `redb.Route.Sql` — SQL-коннектор (чистый ADO.NET)
- **Scheme:** `sql:`, `sql-stored:`
- **Зависимости:** `System.Data.Common` (BCL, 0 внешних)
- **Статус:** 📋 Спецификация готова → [docs/SQL_CONNECTOR_ROADMAP.md](docs/SQL_CONNECTOR_ROADMAP.md)
- **Компоненты:** `SqlComponent`, `SqlStoredComponent`
- **SqlIdempotentRepository** — `IIdempotentRepository` на raw ADO.NET таблице (автономно, без redb.Core)
- **Не зависит от redb.Core** — чистый ADO.NET через `DbProviderFactory`

### 8. `redb.Route.Core` — мост к redb.Core (опциональный)
- **Зависимости:** `redb.Route` + `redb.Core`
- **Статус:** 📋 Спроектировано → [docs/SQL_CONNECTOR_ROADMAP.md](docs/SQL_CONNECTOR_ROADMAP.md)
- **Extension methods** по паттерну `lt.DAL\RouteExtensions.cs` — доступ к `IRedbService` из route pipeline
- **RedbIdempotentRepository** — `IIdempotentRepository` через redb.Core EAV (типизированный объект `IdempotentEntryProps`, без raw SQL)

---

## Порядок реализации

### Фаза 1 — Транспорт (✅ Завершена)

| # | Пакет | Зависимости | Статус |
|---|---|---|---|
| 1 | `redb.Route.Kafka` | Confluent.Kafka | ✅ Готово |
| 2 | `redb.Route.RabbitMQ` | RabbitMQ.Client | ✅ Готово |
| 3 | `redb.Route.Redis` | StackExchange.Redis | ✅ Готово |
| 4 | `redb.Route.Amqp` | AMQPNetLite.Core | ✅ Готово |
| 5 | `redb.Route.Http` | 0 (BCL + ASP.NET) | ✅ Готово |
| 6 | `redb.Route.Tcp` | 0 (BCL) | ✅ Готово |
| 7 | `redb.Route.WebSocket` | 0 (BCL) | ✅ Готово |
| 8 | `redb.Route.Grpc` | Grpc.Net.Client | ✅ Готово |
| 9 | `redb.Route.File` | 0 (BCL) | ✅ Готово |

### Фаза 2 — Интеграции (✅ Основное завершено)

| # | Пакет | Зависимости | Статус |
|---|---|---|---|
| 1 | `redb.Route.Mail` | MailKit | ✅ Готово |
| 2 | `redb.Route.Quartz` | Quartz.NET | ✅ Готово |
| 3 | `redb.Route.Sftp` | SSH.NET | ✅ Готово |
| 4 | `redb.Route.IbmMq` | IBMMQDotnetClient | ✅ Готово |
| 5 | `redb.Route.Sql` | System.Data.Common (ADO.NET) | 🟡 Спецификация готова |
| 6 | `redb.Route.Core` | redb.Route + redb.Core (мост) | 🟡 После Sql |
| 7 | `redb.Route.MqttNet` | MQTTnet | 🟡 IoT |
| 8 | `redb.Route.SignalR` | Microsoft.AspNetCore.SignalR | 🟡 Realtime |

### Фаза 3 — Cloud & Enterprise (Позже)

| # | Пакет | Зависимости | Приоритет |
|---|---|---|---|
| 1 | `redb.Route.AzureServiceBus` | Azure.Messaging.ServiceBus | 🔵 Cloud |
| 2 | `redb.Route.AwsSqs` | AWSSDK.SQS | 🔵 Cloud |
| 3 | `redb.Route.GooglePubSub` | Google.Cloud.PubSub.V1 | 🔵 Cloud |
| 4 | `redb.Route.Nats` | NATS.Net | 🔵 Cloud-native |

## Общие паттерны для всех коннекторов

- Все наследуют `ComponentBase` → `EndpointBase<TOptions>` → `IProducer` / `IConsumer`
- URI-параметры биндятся через `EndpointOptions.BindFromUri()`
- ConnectionFactory через registry (`context.GetFromRegistry<T>()`)
- Транзакции через `ITransactedAction` (где применимо)
- Unit + Integration тесты Per TFM (net8.0 / net9.0 / net10.0)
- `InternalsVisibleTo` для тестовых проектов
