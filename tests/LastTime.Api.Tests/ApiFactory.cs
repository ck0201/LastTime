using LastTime.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

/// <summary>
/// Runs the real API against a dedicated "lasttime_test" database on the project's Docker Postgres
/// (start it with `docker compose up -d`). Never touches the dev "lasttime" database.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LASTTIME_TEST_CONNECTION")
        ?? "Host=127.0.0.1;Port=5433;Database=lasttime_test;Username=lasttime;Password=lasttime_dev_password";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LastTimeDbContext>().Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE users, items, history_entries RESTART IDENTITY CASCADE");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
