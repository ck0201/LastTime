using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthApiTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> RegisterAsync(string? email, string? password) =>
        _client.PostAsJsonAsync("/api/auth/register", new { email, password });

    private async Task<List<User>> UsersAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LastTimeDbContext>().Users.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Register_Valid_Returns201_WithoutSecrets()
    {
        var response = await RegisterAsync("user@example.com", "Password123!");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password123!", body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);

        var dto = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal("user@example.com", dto.Email);
    }

    [Fact]
    public async Task Register_PersistsUser_Active_WithHashedPassword()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await RegisterAsync("  User@Example.com ", "Password123!");

        var user = Assert.Single(await UsersAsync());
        Assert.Equal("user@example.com", user.Email);
        Assert.True(user.IsActive);
        Assert.True(user.CreatedAt > before);
        Assert.NotEqual("Password123!", user.PasswordHash);
        Assert.DoesNotContain("Password123!", user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, "Password123!"));
        Assert.Equal(PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, "wrong-password"));
    }

    [Fact]
    public async Task Register_DoesNotCreateItems()
    {
        await RegisterAsync("user@example.com", "Password123!");

        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LastTimeDbContext>().Items.ToListAsync());
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("USER@Example.COM")]
    public async Task Register_DuplicateEmail_AnyCasing_Returns409(string secondEmail)
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync("user@example.com", "Password123!")).StatusCode);

        var response = await RegisterAsync(secondEmail, "Another123!");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await UsersAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_MissingEmail_Returns400(string? email)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(email, "Password123!")).StatusCode);
        Assert.Empty(await UsersAsync());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at.example.com")]
    [InlineData("@example.com")]
    public async Task Register_InvalidEmail_Returns400(string email)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(email, "Password123!")).StatusCode);
        Assert.Empty(await UsersAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("        ")]
    public async Task Register_MissingPassword_Returns400(string? password)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync("user@example.com", password)).StatusCode);
        Assert.Empty(await UsersAsync());
    }

    [Fact]
    public async Task Register_PasswordBelowMinimumLength_Returns400_AndMinimumIsAccepted()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync("user@example.com", "1234567")).StatusCode);
        Assert.Empty(await UsersAsync());

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync("user@example.com", "12345678")).StatusCode);
    }
}
