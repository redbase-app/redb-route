# Test services for the redb.Route suites

`redb.Route/docker-compose.tests.yml` holds every service the integration and interop suites under
`redb.Route/tests` talk to. This folder holds their configuration files, the Dockerfiles of the small images
built for them, and `gen-certs.sh`.

## Setup, once

```bash
cd redb.Route
bash tests/docker/gen-certs.sh
```

The script needs bash, openssl 3.2 or newer and docker. It writes the test keys, certificates and keystores into
`tests/docker/.generated/` (git-ignored) and creates the folders some interop tests share with their containers.
All of it is throwaway test material; `--force` regenerates it.

Add these names to the hosts file of the machine that runs the tests:

```
127.0.0.1 kafka-k1 kafka-k2 kafka-k3
127.0.0.1 pgdb msdb
```

The Kafka TLS listeners advertise `kafka-k1..3`, the only names the broker certificate carries. Some SQL and Quartz
tests connect to `pgdb` and `msdb` by name.

## Run a suite

Every service has a profile; a plain `docker compose up` starts nothing. Start the profile of a suite, run the suite,
then remove everything. No service keeps data between runs.

```bash
docker compose -f docker-compose.tests.yml --profile kafka up -d --wait
dotnet test tests/redb.Route.Tests.Kafka
docker compose -f docker-compose.tests.yml --profile all --profile pgdb --profile msdb down -v --remove-orphans
```

The first `up` of `firebase`, `grpc`, `soap` and `as2` builds a small image (dependencies baked in, nothing is
installed when a container starts).

## Suites and profiles

| Suite | Profiles | Variables the tests read |
|---|---|---|
| redb.Route.Tests.Amqp | `amqp` | |
| redb.Route.Tests.As2 (interop) | `as2` | `AS2_INTEROP_CERTS=<repo>/redb.Route/tests/docker/.generated/as2/certs`, `REDB_AS2_INTEROP=1` to require the stand |
| redb.Route.Tests.AzureServiceBus | `servicebus` | |
| redb.Route.Tests.Cache | `redis` | |
| redb.Route.Tests.Core | `pgdb`, `msdb` | |
| redb.Route.Tests.Elasticsearch | `elasticsearch` | |
| redb.Route.Tests.Firebase | `firebase` | the tests set `FIRESTORE_EMULATOR_HOST` and `STORAGE_EMULATOR_HOST` themselves |
| redb.Route.Tests.Ftp | `ftp` | |
| redb.Route.Tests.Grpc (interop) | `grpc` | `REDB_GRPC_CERTS=<repo>/redb.Route/tests/docker/.generated/grpc` |
| redb.Route.Tests.IbmMq | `ibmmq` | |
| redb.Route.Tests.Kafka | `kafka` | `REDB_KAFKA_TLS_DIR=<repo>/redb.Route/tests/docker/.generated/kafka` |
| redb.Route.Tests.Ldap | `ldap` | |
| redb.Route.Tests.Llm | `pgdb`, `msdb` | |
| redb.Route.Tests.Mail | `mail` | |
| redb.Route.Tests.MqttNet | `mqtt` | |
| redb.Route.Tests.Quartz | `pgdb` | |
| redb.Route.Tests.RabbitMQ | `rabbitmq`, `rabbitmq-tls` | `REDB_RABBIT_TLS_CERTS=<repo>/redb.Route/tests/docker/.generated/rabbit-tls` |
| redb.Route.Tests.Redis | `redis` | |
| redb.Route.Tests.S3 | `s3` | |
| redb.Route.Tests.Sftp | `sftp` | |
| redb.Route.Tests.SignalR | `redis` | |
| redb.Route.Tests.Soap (interop) | `soap` | `SOAP_INTEROP_DIR=<repo>/redb.Route/tests/docker/.generated/soap` |
| redb.Route.Tests.Sql | `pgdb`, `msdb`, `sql-tier2` (or `firebird`, `mysql`, `mariadb`, `oracle`) | |
| redb.Route.Tests.Sqs | `sqs` | |
| redb.Identity federation tests | `oauth2` | |
| redb.Identity SMTP tests | `mail` | |

`all` starts every service except `pgdb` and `msdb`, which have only their own profiles, so a machine that
already serves Postgres on 5432 and SQL Server on 1433 never starts them. `ui` adds the optional web consoles.
The RabbitMQ, Kafka and Redis suites also start containers of their own through Testcontainers, so the test process
needs access to the Docker socket; the gRPC and SOAP interop tests call `docker exec`.

Interop suites that do not find their service skip those tests (`REDB_AS2_INTEROP=1` turns the AS2 skip into a
failure).

## Host ports

| Port | Service | | Port | Service |
|---|---|---|---|---|
| 21, 21000-21010 | ftp | | 9000, 9001 | s3 |
| 389, 636 | openldap | | 9099, 8086, 4000 | firebase-emulators |
| 1414, 9443 | ibmmq | | 9199 | mock-oauth2 |
| 1433 | msdb | | 9200 | elasticsearch |
| 1521 | oracle | | 9299 | rabbitmq-oauth2 |
| 1883, 5673, 8161 | activemq | | 11883 | mosquitto |
| 2222 | sftp | | 14080, 14081 | openas2 |
| 3025, 3110, 3143, 3465, 3993, 3995, 8080 | greenmail | | 15672 | rabbitmq console |
| 3050 | firebird | | 15674 | rabbitmq-tls console |
| 3306 | mysql | | 18060 | soap-echo |
| 3307 | mariadb | | 18100 | grpc-echo |
| 4443 | fake-gcs | | 18200 | fcm-echo |
| 4566 | localstack | | 29092, 29094, 29096 | kafka-k1..3 plain |
| 5300 | servicebus | | 29192, 29194, 29196 | kafka-k1..3 SASL_SSL |
| 5432 | pgdb | | 29292, 29294, 29296 | kafka-k1..3 mTLS |
| 5671, 5674 | rabbitmq-tls | | 5540, 8389, 8888 | consoles (`ui`) |
| 5672 | rabbitmq | | 6379 | redis |

The tests also open listeners of their own on the host, which the containers reach as `host.docker.internal`:
15081 and 15082 (AS2 MDN), 18110-18159 (gRPC), 18160-18199 (SOAP). Keep them free.

## Test credentials

Fixed throwaway values, the same the tests use: RabbitMQ, ActiveMQ, IBM MQ, LDAP and GreenMail `admin`/`admin`;
S3 `minioadmin`/`minioadmin`; SFTP and FTP `testuser`/`secret`; Postgres `postgres` (trust); SQL Server `sa`/`1`;
Firebird `SYSDBA`/`1`; MySQL and MariaDB `root`/`1`; Oracle `rsql`/`Rsql2026`; Kafka SCRAM `redb`/`redb-secret`.

## Not part of this file

The AS4 interop stand (Holodeck B2B, `redb.Route.Tests.As4` live and reference tests) needs partner keystores that
cannot be generated here, so it is not published; those tests skip without it.
