using Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Polly;
using Polly.Retry;
using PollyEFCore;

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions()
    {
        OnRetry = (args) =>
        {
            Console.WriteLine("------------------------ RETRY ------------------------");
            return new ValueTask();
        },
        MaxRetryAttempts = 10,
        Delay = TimeSpan.FromMilliseconds(100),
        BackoffType = DelayBackoffType.Exponential,
        ShouldHandle = new PredicateBuilder()
            .Handle<Exception>(IsTransient),
    })
    .AddTimeout(TimeSpan.FromSeconds(30))
    .Build();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddScoped<SimulatedConnectionFaultInterceptor>();
builder.Services.AddScoped<SimulatedTransientErrorInterceptor>();

builder.Services.AddDbContext<FooContext>((sp, ef) =>
{
    IInterceptor[] interceptors =
    [
        // sp.GetRequiredService<SimulatedConnectionFaultInterceptor>(),
        // sp.GetRequiredService<SimulatedTransientErrorInterceptor>(),
    ];

    ef.UseNpgsql(connectionString);
    ef.AddPollyResilience(pipeline);
    ef.AddInterceptors(interceptors);
});

builder.Services
    .AddHealthChecks()
    // Doesnt work:
    .AddDbContextCheck<FooContext>();
    // Works:
    // .AddDbContextCheck<FooContext>(
    //     customTestQuery: async (db, ct) =>
    //     {
    //         return await db.Database.ExecuteWithResilienceAsync(
    //             pipeline,
    //             async token =>
    //             {
    //                 await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
    //                 return true;
    //             },
    //             ct);
    //     });

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet(
    "/", 
    () => "Hello World!");

// This works:
// app.MapGet("/items",
//     async (FooContext db) =>
//         await db.Database.ExecuteWithResilienceAsync(
//             pipeline,
//             ct => db.Items.ToListAsync(ct)));

// This doesnt work:
app.MapGet("/items",
    async (FooContext db) =>
        await db.Items.ToListAsync());

app.MapPost("/items",
    async (FooContext db, Item item) =>
    {
        item.Id = Guid.NewGuid();
        await db.Items.AddAsync(item);
        await db.SaveChangesAsync();
        return Results.Created($"/items/{item.Id}", item);
    });
    
app.Run();

// Unwraps EF/provider wrappers such as InvalidOperationException and DbUpdateException.
static bool IsTransient(Exception exception)
    => exception switch
    {
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        _ => exception.InnerException is not null && IsTransient(exception.InnerException),
    };
