using LastTime.Api.Data;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    // Test-only JWT settings; not a real secret.
    public const string JwtSecret = "test-only-secret-key-for-integration-tests-0123456789";
    public const string JwtIssuer = "last-time-tests";
    public const string JwtAudience = "last-time-tests-audience";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:Secret", JwtSecret);
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("Jwt:ExpirationMinutes", "60");
        // Makes the test-only protected endpoint (TestProtectedController) available.
        builder.ConfigureTestServices(s => s.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly));
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LastTimeDbContext>().Database.MigrateAsync();
    }

    /// <summary>Registers a user through the real API, logs in, and returns a client sending that user's JWT.</summary>
    public async Task<(HttpClient Client, Guid UserId)> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Password123!" });
        register.EnsureSuccessStatusCode();
        var user = (await register.Content.ReadFromJsonAsync<RegisterResponse>())!;

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123!" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, user.Id);
    }

    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE users, otp_codes, items, history_entries RESTART IDENTITY CASCADE");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
