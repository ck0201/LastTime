using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LastTime.Api.Tests;

// ---------------------------------------------------------------- doubles for the provider boundary

/// <summary>Stands in for the network: records every request Resend would have received, and answers as told.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri? Uri, string? Authorization, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string Text => Json.GetProperty("text").GetString()!;
        public string Html => Json.GetProperty("html").GetString()!;
        public string Subject => Json.GetProperty("subject").GetString()!;
        public string From => Json.GetProperty("from").GetString()!;
        public string[] To => Json.GetProperty("to").EnumerateArray().Select(e => e.GetString()!).ToArray();
        public string Code => Regex.Match(Text, @"\b\d{6}\b").Value;
    }

    private readonly List<Request> _requests = [];
    public IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToArray(); } }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "email-id" }) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_requests) _requests.Add(new Request(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
        return await Respond(request, ct);
    }
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    // Same base address as the real registration, so the sender's relative "emails" path resolves the same way.
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.resend.com/") };
}

public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _entries = [];
    public IReadOnlyList<string> Entries { get { lock (_entries) return _entries.ToArray(); } }
    public string All => string.Join("\n", Entries);
    public IReadOnlyList<LogLevel> Levels { get { lock (_levels) return _levels.ToArray(); } }
    private readonly List<LogLevel> _levels = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        lock (_levels) _levels.Add(logLevel);
    }
}

public sealed class CountingSender : IOtpSender
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------- content, sender and failure handling

public class ResendOtpSenderTests
{
    private const string ApiKey = "test-api-key-should-never-appear-in-logs";
    private const string Code = "482913";

    private readonly RecordingHandler _handler = new();
    private readonly ListLogger<ResendOtpSender> _log = new();

    private static readonly User Recipient = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Email = "recipient@example.com",
        PasswordHash = "stored-password-hash-value",
    };

    private ResendOtpSender Sender(string senderName = "LastTime", string senderEmail = "onboarding@resend.dev") =>
        new(new StubHttpClientFactory(_handler),
            Options.Create(new ResendOptions { ApiKey = ApiKey, SenderEmail = senderEmail, SenderName = senderName }),
            _log);

    [Fact]
    public async Task VerificationEmail_GoesToTheUser_WithTheCode_AndSaysWhatItIsFor()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.Uri!.ToString());
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        Assert.Equal(["recipient@example.com"], request.To);
        Assert.Contains("verification", request.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verify your email address", request.Text);
        Assert.Contains("LastTime", request.Text);
        Assert.Contains(Code, request.Text);
        Assert.Contains(Code, request.Html);
    }

    [Fact]
    public async Task PasswordResetEmail_GoesToTheUser_WithTheCode_AndSaysWhatItIsFor()
    {
        await Sender().SendAsync(Recipient, Otp.PasswordReset, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(["recipient@example.com"], request.To);
        Assert.Contains("password reset", request.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reset the password", request.Text);
        Assert.Contains("LastTime", request.Text);
        Assert.Contains(Code, request.Text);
        Assert.Contains(Code, request.Html);
        Assert.DoesNotContain("verify your email", request.Text);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task Emails_StateTheExpiry_FromTheOtpLifetime(string purpose)
    {
        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        var expected = $"{(int)Otp.Lifetime.TotalMinutes} minutes";
        Assert.Contains(expected, request.Text);
        Assert.Contains(expected, request.Html);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task Emails_ContainNoAccountDetails_AndNoResetToken(string purpose)
    {
        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        foreach (var body in new[] { request.Text, request.Html, request.Subject })
        {
            Assert.DoesNotContain(Recipient.PasswordHash, body);
            Assert.DoesNotContain(Recipient.Email, body);
            Assert.DoesNotContain(Recipient.Id.ToString(), body);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, body);
        }
    }

    [Fact]
    public async Task TheConfiguredSenderIsUsed()
    {
        await Sender("LastTime", "onboarding@resend.dev").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("Acme Support", "support@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("", "plain@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Equal(
            ["LastTime <onboarding@resend.dev>", "Acme Support <support@acme.example>", "plain@acme.example"],
            _handler.Requests.Select(r => r.From));
    }

    [Fact]
    public async Task WhenResendRejectsTheEmail_NothingIsThrown_AndTheLogHasNoSecrets()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new
            {
                name = "validation_error",
                message = "You can only send testing emails to your own email address (owner@example.com).",
            }),
        });

        await Sender().SendAsync(Recipient, Otp.PasswordReset, Code, CancellationToken.None); // must not throw

        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.Contains("403", _log.All);
        Assert.Contains("validation_error", _log.All);
        foreach (var secret in new[] { Code, ApiKey, "owner@example.com", Recipient.Email, Recipient.PasswordHash })
            Assert.DoesNotContain(secret, _log.All);
    }

    [Fact]
    public async Task WhenTheNetworkFails_NothingIsThrown_AndTheLogHasNoSecrets()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");

        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.Contains("connection refused", _log.All);
        foreach (var secret in new[] { Code, ApiKey, Recipient.Email })
            Assert.DoesNotContain(secret, _log.All);
    }

    [Fact]
    public async Task WhenTheRequestTimesOut_NothingIsThrown()
    {
        _handler.Respond = (_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Contains(LogLevel.Error, _log.Levels);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ThatIsNotSwallowed()
    {
        _handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var cts = new CancellationTokenSource();
        var sending = Sender().SendAsync(Recipient, Otp.EmailVerification, Code, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    [Fact]
    public async Task ASuccessfulSend_LogsNothing()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task AnUnknownPurpose_SendsNothing()
    {
        await Sender().SendAsync(Recipient, "Whatever", Code, CancellationToken.None);

        Assert.Empty(_handler.Requests);
        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.DoesNotContain(Code, _log.All);
    }
}

// ---------------------------------------------------------------- daily limit

public class DailyLimitedOtpSenderTests
{
    private static readonly User Recipient = new() { Id = Guid.NewGuid(), Email = "recipient@example.com", PasswordHash = "x" };

    private readonly CountingSender _inner = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ListLogger<DailyLimitedOtpSender> _log = new();

    private DailyLimitedOtpSender Limited(int limit) => new(_inner, limit, _time, _log);

    [Fact]
    public async Task StopsCallingTheProvider_OnceTheLimitIsReached()
    {
        var sender = Limited(2);

        for (var i = 0; i < 5; i++) await sender.SendAsync(Recipient, Otp.EmailVerification, "123456", CancellationToken.None);

        Assert.Equal(2, _inner.Calls);
        Assert.Contains(LogLevel.Warning, _log.Levels);
        Assert.DoesNotContain("123456", _log.All);
    }

    [Fact]
    public async Task TheCountStartsAgainOnTheNextUtcDay()
    {
        _time.Set(new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero));
        var sender = Limited(1);

        await sender.SendAsync(Recipient, Otp.PasswordReset, "123456", CancellationToken.None);
        await sender.SendAsync(Recipient, Otp.PasswordReset, "123456", CancellationToken.None);
        Assert.Equal(1, _inner.Calls);

        _time.Advance(TimeSpan.FromHours(2)); // 01:00 UTC the next day
        await sender.SendAsync(Recipient, Otp.PasswordReset, "123456", CancellationToken.None);
        Assert.Equal(2, _inner.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task AZeroOrNegativeLimit_SendsNothing(int limit)
    {
        await Limited(limit).SendAsync(Recipient, Otp.EmailVerification, "123456", CancellationToken.None);
        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task SimultaneousSends_NeverExceedTheLimit()
    {
        var sender = Limited(10);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            Task.Run(() => sender.SendAsync(Recipient, Otp.EmailVerification, "123456", CancellationToken.None))));

        Assert.Equal(10, _inner.Calls);
    }
}

// ---------------------------------------------------------------- the real wiring, through the real endpoints

[Collection(ApiCollection.Name)]
public class OtpDeliveryWiringTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly RecordingHandler _provider = new();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The real delivery registration, with Resend's HTTP replaced by a recording fake. No real email can be sent.</summary>
    private WebApplicationFactory<Program> Host(bool configured = true, int? dailyLimit = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Resend:ApiKey"] = configured ? "test-api-key" : "",
            ["Resend:SenderEmail"] = configured ? "onboarding@resend.dev" : "",
            ["Resend:SenderName"] = "LastTime",
            ["Email:DailyLimit"] = (dailyLimit ?? 100).ToString(),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOtpSender>();
                services.RemoveAll<ResendOtpSender>();
                services.AddOtpDelivery(configuration); // the production registration, fed only the settings above
                services.AddHttpClient(ResendOtpSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _provider);
            }));
    }

    private static async Task RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Password123!" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host, string email)
    {
        var client = host.CreateClient();
        await RegisterAsync(client, email);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123!" });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ForgotPassword_EmailsTheResetCode_ThatThenWorks()
    {
        await using var host = Host();
        var client = host.CreateClient();
        await RegisterAsync(client, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        var email = Assert.Single(_provider.Requests);
        Assert.Equal(["user@example.com"], email.To);
        Assert.Equal("LastTime <onboarding@resend.dev>", email.From);
        Assert.Equal("Bearer test-api-key", email.Authorization);
        Assert.Contains("reset the password", email.Text);

        // The emailed code is the real one, and only the code is emailed (the reset token never is).
        var verify = await client.PostAsJsonAsync("/api/auth/verify-password-reset", new { email = "user@example.com", code = email.Code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var token = (await verify.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        Assert.Single(_provider.Requests); // verifying sent nothing more
        Assert.DoesNotContain(token, email.Body);
    }

    [Fact]
    public async Task ResendVerification_EmailsTheVerificationCode_ThatThenWorks()
    {
        await using var host = Host();
        var client = await SignedInAsync(host, "user@example.com");

        var response = await client.PostAsync("/api/auth/resend-verification", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var email = Assert.Single(_provider.Requests);
        Assert.Equal(["user@example.com"], email.To);
        Assert.Contains("verify your email address", email.Text);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { code = email.Code });
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
    }

    [Fact]
    public async Task WhenResendFails_TheApiAnswersExactlyAsBefore_WithoutProviderDetails()
    {
        _provider.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { name = "validation_error", message = "provider internals" }),
        });
        await using var host = Host();
        var client = host.CreateClient();
        await RegisterAsync(client, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("", body);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task WhenTheNetworkFails_TheApiStillAnswersAccepted()
    {
        _provider.Respond = (_, _) => throw new HttpRequestException("no route to host");
        await using var host = Host();
        var client = await SignedInAsync(host, "user@example.com");

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/resend-verification", null)).StatusCode);
    }

    [Fact]
    public async Task TheDailyLimit_StopsFurtherProviderCalls_WithoutChangingTheResponses()
    {
        await using var host = Host(dailyLimit: 1);
        var client = host.CreateClient();
        await RegisterAsync(client, "first@example.com");
        await RegisterAsync(client, "second@example.com");

        var first = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "first@example.com" });
        var second = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var only = Assert.Single(_provider.Requests); // the second email never reached Resend
        Assert.Equal(["first@example.com"], only.To);
    }

    [Fact]
    public async Task TheDailyLimitSetting_IsRead_FromEmailDailyLimit()
    {
        await using var host = Host(dailyLimit: 7);
        Assert.Equal(7, host.Services.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit);
    }

    [Fact]
    public async Task WithoutResendSettings_NothingIsSent_AndTheApiStillWorks()
    {
        await using var host = Host(configured: false);
        var client = host.CreateClient();
        await RegisterAsync(client, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(_provider.Requests);
        Assert.IsType<NotConfiguredOtpSender>(host.Services.GetRequiredService<IOtpSender>());
    }

    [Fact]
    public async Task WithResendSettings_TheLimitedResendSenderIsUsed()
    {
        await using var host = Host();
        Assert.IsType<DailyLimitedOtpSender>(host.Services.GetRequiredService<IOtpSender>());
    }
}
