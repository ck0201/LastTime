using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LastTime.Api.Data;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LastTime.Api.Tests;

/// <summary>The login token's email_verified claim, which the Angular app reads.</summary>
[Collection(ApiCollection.Name)]
public class EmailVerifiedClaimTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Email = "user@example.com";
    private const string Password = "Password123!";

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> RegisterAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new { email = Email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!.Id;
    }

    private async Task<string> LoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private async Task SetVerifiedAsync(Guid id, DateTimeOffset? verifiedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, verifiedAt));
    }

    /// <summary>The token's payload exactly as the browser sees it (base64url JSON).</summary>
    private static JsonElement PayloadOf(string token)
    {
        var payload = token.Split('.')[1];
        return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload)).RootElement.Clone();
    }

    [Fact]
    public async Task VerifiedUser_GetsEmailVerifiedTrue_AsAJsonBoolean()
    {
        var id = await RegisterAsync();
        await SetVerifiedAsync(id, DateTimeOffset.UtcNow);

        var payload = PayloadOf(await LoginAsync());

        Assert.Equal(JsonValueKind.True, payload.GetProperty("email_verified").ValueKind); // a boolean, not the text "true"
    }

    [Fact]
    public async Task UnverifiedUser_GetsEmailVerifiedFalse_AsAJsonBoolean()
    {
        await RegisterAsync();

        var payload = PayloadOf(await LoginAsync());

        Assert.Equal(JsonValueKind.False, payload.GetProperty("email_verified").ValueKind);
    }

    [Fact]
    public async Task TheClaim_ComesFromTheUsersCurrentStateAtLogin()
    {
        var id = await RegisterAsync();
        var before = await LoginAsync();
        Assert.Equal(JsonValueKind.False, PayloadOf(before).GetProperty("email_verified").ValueKind);

        await SetVerifiedAsync(id, DateTimeOffset.UtcNow); // verified in the meantime
        var after = await LoginAsync();

        Assert.Equal(JsonValueKind.True, PayloadOf(after).GetProperty("email_verified").ValueKind);
        // a token is a snapshot: the one issued earlier is not rewritten
        Assert.Equal(JsonValueKind.False, PayloadOf(before).GetProperty("email_verified").ValueKind);

        await SetVerifiedAsync(id, null); // and back again
        Assert.Equal(JsonValueKind.False, PayloadOf(await LoginAsync()).GetProperty("email_verified").ValueKind);
    }

    [Fact]
    public async Task ExistingClaims_AreUnchanged_AndNothingElseWasAdded()
    {
        var id = await RegisterAsync();
        var token = await LoginAsync();

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        Assert.Equal(id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(Email, jwt.GetClaim("email").Value);
        Assert.Equal(ApiFactory.JwtIssuer, jwt.Issuer);
        Assert.Contains(ApiFactory.JwtAudience, jwt.Audiences);
        Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalMinutes, 59, 60.5); // still 60 minutes

        var names = PayloadOf(token).EnumerateObject().Select(p => p.Name).ToHashSet();
        names.ExceptWith(["iss", "aud", "exp", "nbf", "iat"]); // the standard registered claims
        Assert.Equal(new HashSet<string> { "sub", "email", "email_verified" }, names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheToken_StillWorksWithTheAuthenticationMiddleware(bool verified)
    {
        var id = await RegisterAsync();
        if (verified) await SetVerifiedAsync(id, DateTimeOffset.UtcNow);
        var token = await LoginAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoginOtherwiseBehavesAsBefore()
    {
        await RegisterAsync();

        var ok = await _client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(["token"], body.RootElement.EnumerateObject().Select(p => p.Name)); // the response body did not change

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = "Wrong-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = Password })).StatusCode);
    }
}

/// <summary>Retry-After is readable by browser JavaScript on cross-origin responses; the rest of CORS is unchanged.</summary>
[Collection(ApiCollection.Name)]
public class RetryAfterCorsTests(ApiFactory factory) : IAsyncLifetime
{
    private const string AllowedOrigin = "https://app.example";

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> HostAllowing(string origins) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins", origins));

    private static HttpRequestMessage From(string origin, HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task AResponseToAnAllowedOrigin_ExposesRetryAfter_AndOnlyThat()
    {
        await using var host = HostAllowing(AllowedOrigin);
        var client = host.CreateClient();

        var response = await client.SendAsync(From(AllowedOrigin, HttpMethod.Post, "/api/auth/login", JsonContent.Create(new { email = "a@example.com", password = "x" })));

        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("Retry-After", Header(response, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task ARealCrossOrigin429_CarriesRetryAfter_AndExposesItToTheBrowser()
    {
        await using var host = HostAllowing(AllowedOrigin);
        var client = host.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { email = "user@example.com", password = "Password123!" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "user@example.com", password = "Password123!" });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        HttpRequestMessage Resend()
        {
            var request = From(AllowedOrigin, HttpMethod.Post, "/api/auth/resend-verification");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Resend())).StatusCode);
        var cooling = await client.SendAsync(Resend()); // straight away again: within the 60-second cooldown

        Assert.Equal(HttpStatusCode.TooManyRequests, cooling.StatusCode);
        Assert.NotNull(cooling.Headers.RetryAfter); // the header exists...
        Assert.Contains("Retry-After", Header(cooling, "Access-Control-Expose-Headers")); // ...and the browser may read it
        Assert.Equal(AllowedOrigin, Header(cooling, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task APreflight_IsUnchanged_SameMethodsAndHeaders_AndNoCredentials()
    {
        await using var host = HostAllowing(AllowedOrigin);
        var client = host.CreateClient();
        var preflight = From(AllowedOrigin, HttpMethod.Options, "/api/auth/resend-verification");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("GET,POST,PUT,DELETE", Header(response, "Access-Control-Allow-Methods"));
        Assert.Equal("authorization,content-type", Header(response, "Access-Control-Allow-Headers"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials")); // credentials are still not allowed
    }

    [Fact]
    public async Task ADisallowedOrigin_GetsNoCorsHeadersAtAll()
    {
        await using var host = HostAllowing(AllowedOrigin);
        var client = host.CreateClient();

        var response = await client.SendAsync(From("https://evil.example", HttpMethod.Post, "/api/auth/login", JsonContent.Create(new { email = "a@example.com", password = "x" })));

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task WithNoOriginsConfigured_NoCrossOriginAccessIsGranted_AsBefore()
    {
        var client = factory.CreateClient(); // the standard test host configures no origins

        var response = await client.SendAsync(From(AllowedOrigin, HttpMethod.Post, "/api/auth/login", JsonContent.Create(new { email = "a@example.com", password = "x" })));

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Expose-Headers"));
    }
}
