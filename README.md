# PollyEFCore automatic interception vs. connection-level failures

This repository is a small reproduction/example project for
[PollyEFCore](https://github.com/Swevo/PollyEFCore). It documents an important
limitation of PollyEFCore's "automatic interception" feature: it retries
individual ADO.NET commands, but it cannot recover a connection that has been
terminated underneath EF Core.

## Summary

`AddPollyResilience(...)` wraps `DbCommand.ExecuteReader/ExecuteNonQuery/ExecuteScalar`
in a Polly v8 resilience pipeline. It does **not** wrap or reset the
`DbConnection`. Therefore, when PostgreSQL terminates the backend connection
(e.g. `pg_terminate_backend`), the first attempt fails with a transient
`NpgsqlException`, but every Polly retry runs the same command against the same
broken `NpgsqlConnection` and immediately fails with:

```
System.InvalidOperationException: Connection is not open
```

Retrying the command can never succeed because nothing closes and reopens the
connection between attempts.

This is not a configuration mistake. The retry has to happen at a level that
includes EF Core's connection open/close lifecycle — i.e. around the whole
operation, not around a single command.

## Environment used for the investigation

- .NET 11 (`net11.0`)
- Microsoft.EntityFrameworkCore.Relational `10.0.14`
- Npgsql.EntityFrameworkCore.PostgreSQL `10.0.3`
- PollyEFCore `1.0.3`
- PostgreSQL container exposed on `localhost:5432`

## Reproduction

1. Start the `Host` application.
2. Call `GET /items` to put a physical connection in the Npgsql pool.
3. Terminate the PostgreSQL backend from inside the container:

   ```sql
   SELECT pid, pg_terminate_backend(pid) AS terminated
   FROM pg_stat_activity
   WHERE client_addr = '172.17.0.1';
   ```

4. Call `GET /items` again.

### Observed result

The console shows the retry callback:

```
------------------------ RETRY ------------------------
```

Immediately after the retry, the request fails with:

```
System.InvalidOperationException: Connection is not open
   at Npgsql.NpgsqlCommand.CheckAndGetConnection()
   at Npgsql.NpgsqlCommand.ExecuteReader(...)
   at PollyEFCore.ResilienceDbCommandInterceptor.ReaderExecutingAsync(...)
```

The health check (`GET /health`) fails for the same reason when it uses the
default `AddDbContextCheck<FooContext>()`.

## Root cause

### What PollyEFCore actually intercepts

`AddPollyResilience` registers a `DbCommandInterceptor`. Its implementation is
essentially:

```csharp
var reader = await _pipeline.ExecuteAsync(
    ct => new ValueTask<DbDataReader>(command.ExecuteReaderAsync(ct)),
    cancellationToken);
```

Only the command execution is inside the Polly pipeline.

### Where that sits in EF Core's execution flow

For a query, EF Core does roughly this:

1. `connection.OpenAsync()` — **outside** the Polly pipeline
2. run command interceptors
3. `command.ExecuteReaderAsync()` — **inside** the Polly retry loop
4. on failure, `CleanupCommandAsync()` closes the connection — **outside**
   the Polly pipeline

### What happens after `pg_terminate_backend`

1. Npgsql hands EF Core a pooled connection whose PostgreSQL backend has just
   been killed. EF Core still considers the `NpgsqlConnection` to be open.
2. The first `ExecuteReaderAsync` fails with the real transient error
   (`NpgsqlException`, e.g. "Exception while reading from stream" or SQLSTATE
   `57P01`).
3. Npgsql marks the connector as broken/closed.
4. Polly catches the transient exception and retries the **same `DbCommand`
   bound to the same broken `NpgsqlConnection`**.
5. `NpgsqlCommand.CheckAndGetConnection()` now throws
   `InvalidOperationException: Connection is not open`.
6. That exception is not transient, so the pipeline stops retrying.

The broken connector is discarded when EF Core closes the connection at the end
of the failed request, which is why a *subsequent* request often succeeds. The
request that hit the dead connection cannot recover, no matter how many retry
attempts are configured.

### Why `ExecuteWithResilienceAsync` works

This version retries the whole EF Core operation:

```csharp
await db.Database.ExecuteWithResilienceAsync(
    pipeline,
    ct => db.Items.ToListAsync(ct));
```

Each retry goes back through EF Core's connection open/close lifecycle. After
the first failure, the broken connector is discarded and the retry gets a fresh
physical connection from the Npgsql pool, so it succeeds.

## Verified behavior matrix

| Configuration | Result after terminating the backend |
| --- | --- |
| Automatic Polly interception only (`AddPollyResilience`) | **500** — `Connection is not open` after the first retry |
| Polly around the whole operation (`ExecuteWithResilienceAsync`) | **200** — operation retried with a fresh connection |
| Npgsql `EnableRetryOnFailure()` only | **200** — EF Core retries the operation and reopens the connection |
| Automatic Polly interception **and** `EnableRetryOnFailure()` | **500** — Polly masks the original transient error |

The last row is important: enabling both retry layers does not help. Polly's
per-command retry turns the original transient `NpgsqlException` into a
non-transient `InvalidOperationException`, so EF Core's outer execution strategy
never retries the operation.

## Recommended fixes

### Option 1 — Keep Polly, retry the whole operation

Use `ExecuteWithResilienceAsync` instead of relying on automatic command
interception:

```csharp
app.MapGet("/items", async (FooContext db, CancellationToken ct) =>
    await db.Database.ExecuteWithResilienceAsync(
        pipeline,
        token => db.Items.ToListAsync(token),
        ct));
```

This is the right choice if you need Polly features such as custom backoff,
circuit breakers, hedging, or per-operation timeouts.

For writes, wrap the whole unit of work (usually `SaveChangesAsync`), and make
sure the operation is safe to retry / idempotent.

### Option 2 — Use EF Core's provider execution strategy

This keeps call sites unchanged:

```csharp
ef.UseNpgsql(connectionString, npgsql =>
    npgsql.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(30),
        errorCodesToAdd: null));
```

Remove `ef.AddPollyResilience(pipeline)` when using this option. The provider's
retrying execution strategy runs at the operation level, which allows EF Core to
reopen the connection on each attempt.

### If you really want both layers

Do not let both layers handle the same connection-level failures. Restrict the
Polly predicate to errors where the connection is still usable, for example
specific `PostgresException` SQLSTATEs, and let EF Core's execution strategy
handle socket/connection failures such as `NpgsqlException` with an inner
`IOException`/`SocketException`.

## Other cases that are not covered by automatic interception

- **Default health check**: `AddDbContextCheck<FooContext>()` executes a query
  through the same command interceptor, so it also fails. Wrapping the health
  check's custom test query with `ExecuteWithResilienceAsync` works.
- **Connection-opening failures**: `SimulatedConnectionFaultInterceptor` throws
  from `ConnectionOpeningAsync`. PollyEFCore does not intercept connection
  opening at all, so that fault is never retried by `AddPollyResilience`.
- **Explicit transactions**: the PollyEFCore README already warns that retrying
  individual commands inside an explicit `DbTransaction` is unsafe. Retrying the
  whole unit of work is the safer model.

## Key takeaway

> "No code changes required" applies to command-level transient failures
> (deadlocks, serialization failures, throttling, etc.). It does not apply to a
> lost or broken connection.

Connection recovery requires retrying at or above EF Core's connection-open
boundary. A `DbCommandInterceptor` sits below that boundary and therefore cannot
repair a dead connection.

## Relevant files

- `src/Host/Program.cs` — current example, configured with automatic
  interception; working variants are present as comments.
- `src/Host/FooContext.cs` — minimal `DbContext`.
- `src/Host/SimulatedConnectionFaultInterceptor.cs` — connection-opening fault
  injector (not retried by automatic interception).
- `src/Host/SimulatedTransientErrorInterceptor.cs` — command-level fault
  injector.
