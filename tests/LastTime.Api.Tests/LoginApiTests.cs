using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using LastTime.Api.Data;
using LastTime.Api.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class LoginApiTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Email = "user@example.com";
    private const string Password = "Password123!";

    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<RegisterResponse> RegisterAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new { email = Email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    private Task<HttpResponseMessage> LoginAsync(string? email, string? password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<string> LoginTokenAsync()
    {
        var response = await LoginAsync(Email, Password);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static string MakeToken(Guid userId, DateTime expires, string secret = ApiFactory.JwtSecret,
        string audience = ApiFactory.JwtAudience, string issuer = ApiFactory.JwtIssuer) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", userId.ToString())]),
            Issuer = issuer,
            Audience = audience,
            NotBefore = expires.AddHours(-2),
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });

    private Task<HttpResponseMessage> WhoAmIAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/whoami");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200_WithJwtAndNoSecrets()
    {
        await RegisterAsync();

        var response = await LoginAsync("  USER@example.com ", Password); // normalized like registration

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public async Task Login_Token_ContainsUserIdAndEmail_AndExpiresInAbout60Minutes()
    {
        var user = await RegisterAsync();

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(await LoginTokenAsync());

        Assert.Equal(user.Id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(Email, jwt.GetClaim("email").Value);
        Assert.Equal(ApiFactory.JwtIssuer, jwt.Issuer);
        Assert.Contains(ApiFactory.JwtAudience, jwt.Audiences);
        var lifetime = jwt.ValidTo - DateTime.UtcNow;
        Assert.InRange(lifetime.TotalMinutes, 59, 60.5);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        await RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email, "WrongPassword1!")).StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401_WithSameBodyAsWrongPassword()
    {
        await RegisterAsync();

        var unknown = await LoginAsync("nobody@example.com", Password);
        var wrong = await LoginAsync(Email, "WrongPassword1!");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_InactiveUser_Returns401()
    {
        await RegisterAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email, Password)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Login_MissingEmail_Returns400(string? email) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await LoginAsync(email, Password)).StatusCode);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Login_MissingPassword_Returns400(string? password) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await LoginAsync(Email, password)).StatusCode);

    [Fact]
    public async Task ProtectedEndpoint_AcceptsValidLoginToken()
    {
        var user = await RegisterAsync();

        var response = await WhoAmIAsync(await LoginTokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(user.Id.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProtectedEndpoint_RejectsMissingMalformedAndExpiredTokens()
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync("not.a.jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await WhoAmIAsync(MakeToken(id, DateTime.UtcNow.AddMinutes(-5)))).StatusCode); // expired

        // Sanity check: the helper itself produces an accepted token when everything is valid.
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(MakeToken(id, DateTime.UtcNow.AddMinutes(30)))).StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_RejectsWrongSignatureIssuerOrAudience()
    {
        var id = Guid.NewGuid();
        var exp = DateTime.UtcNow.AddMinutes(30);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await WhoAmIAsync(MakeToken(id, exp, secret: "a-different-secret-key-that-is-long-enough-123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(MakeToken(id, exp, issuer: "someone-else"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(MakeToken(id, exp, audience: "other-app"))).StatusCode);
    }
}
