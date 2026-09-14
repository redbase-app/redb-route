-- Flat tables of the demo, next to the redb tables in the same SQL Server database.
-- Business entities (partners, products, messages, requests, responses) are redb objects.
-- These tables hold what is volume rather than model: tens of thousands of serial numbers per
-- request, the yearly allocation ledger the quota is checked against, and the outbox.
-- Every statement is idempotent: the module runs this script on each start.
-- GENERATE_SERIES, used by the allocation, needs SQL Server 2022 or later.

IF OBJECT_ID(N'dbo.serial_counters', N'U') IS NULL
    CREATE TABLE dbo.serial_counters (
        gtin        varchar(14) NOT NULL PRIMARY KEY,
        next_value  bigint      NOT NULL
    );

IF OBJECT_ID(N'dbo.serial_allocations', N'U') IS NULL
    CREATE TABLE dbo.serial_allocations (
        id                  bigint IDENTITY(1, 1) PRIMARY KEY,
        gtin                varchar(14)   NOT NULL,
        partner_code        nvarchar(64)  NOT NULL,
        request_id          nvarchar(64)  NOT NULL,
        request_object_id   bigint        NOT NULL,
        first_value         bigint        NOT NULL,
        quantity            int           NOT NULL,
        allocation_year     int           NOT NULL,
        allocated_at        datetime2     NOT NULL CONSTRAINT DF_serial_allocations_allocated_at DEFAULT SYSUTCDATETIME()
    );

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_serial_allocations_gtin_year')
    CREATE INDEX IX_serial_allocations_gtin_year
        ON dbo.serial_allocations (gtin, allocation_year) INCLUDE (quantity);

IF OBJECT_ID(N'dbo.serial_numbers', N'U') IS NULL
    CREATE TABLE dbo.serial_numbers (
        gtin            varchar(14) NOT NULL,
        serial_value    bigint      NOT NULL,
        allocation_id   bigint      NOT NULL,
        state           varchar(16) NOT NULL CONSTRAINT DF_serial_numbers_state DEFAULT 'Allocated',
        CONSTRAINT PK_serial_numbers PRIMARY KEY (gtin, serial_value)
    );

IF OBJECT_ID(N'dbo.outbox', N'U') IS NULL
    CREATE TABLE dbo.outbox (
        id                  bigint IDENTITY(1, 1) PRIMARY KEY,
        partner_code        nvarchar(64)    NOT NULL,
        response_object_id  bigint          NOT NULL,
        created_at          datetime2       NOT NULL CONSTRAINT DF_outbox_created_at DEFAULT SYSUTCDATETIME(),
        sent_at             datetime2       NULL,
        attempts            int             NOT NULL CONSTRAINT DF_outbox_attempts DEFAULT 0,
        last_error          nvarchar(2000)  NULL
    );

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_outbox_pending')
    CREATE INDEX IX_outbox_pending ON dbo.outbox (id) WHERE sent_at IS NULL;
