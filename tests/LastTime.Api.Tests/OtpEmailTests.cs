using System.Net;
using System.Net.Http.Json;
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

/// <summary>Stands in for the network: records every request Brevo would have received, and answers as told.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri? Uri, string? ApiKey, string? Accept, string? ContentType, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string Text => Json.GetProperty("textContent").GetString()!;
        public string Html => Json.GetProperty("htmlContent").GetString()!;
        public string Subject => Json.GetProperty("subject").GetString()!;
        public string? SenderName => Json.GetProperty("sender").TryGetProperty("name", out var n) ? n.GetString() : null;
        public string SenderEmail => Json.GetProperty("sender").GetProperty("email").GetString()!;
        public string[] To => Json.GetProperty("to").EnumerateArray().Select(e => e.GetProperty("email").GetString()!).ToArray();
        public string Code => Regex.Match(Text, @"\b\d{6}\b").Value;
    }

    private readonly List<Request> _requests = [];
    public IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToArray(); } }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Accepted());

    /// <summary>What Brevo answers when it queued the email.</summary>
    public static HttpResponseMessage Accepted() => new(HttpStatusCode.Created)
    {
        Content = JsonContent.Create(new { messageId = "<202610040000.1@smtp-relay.mailin.fr>" }),
    };

    /// <summary>What Brevo answers when it refuses the request.</summary>
    public static HttpResponseMessage Refused(HttpStatusCode status, string code, string message) => new(status)
    {
        Content = JsonContent.Create(new { code, message }),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var apiKey = request.Headers.TryGetValues("api-key", out var keys) ? string.Join(",", keys) : null;
        var accept = request.Headers.Accept.Count > 0 ? string.Join(",", request.Headers.Accept) : null;
        lock (_requests) _requests.Add(new Request(request.Method, request.RequestUri, apiKey, accept, request.Content?.Headers.ContentType?.MediaType, body));
        return await Respond(request, ct);
    }
}

/// <summary>Used by the standard test host: anything that tries to reach the real email provider fails loudly.</summary>
public sealed class RefuseRealEmailHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException("Tests must never send real email.");
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    // Same base address as the real registration, so the sender's relative "smtp/email" path resolves the same way.
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.brevo.com/v3/") };
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

// ---------------------------------------------------------------- settings

public class BrevoOptionsTests
{
    [Fact]
    public void BindsFromTheBrevoSection_WithTheDocumentedKeys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Brevo:ApiKey"] = "xkeysib-test",
            ["Brevo:SenderEmail"] = "from@example.test",
            ["Brevo:SenderName"] = "LastTime",
        }).Build();

        var options = configuration.GetSection(BrevoOptions.SectionName).Get<BrevoOptions>()!;

        Assert.Equal(("xkeysib-test", "from@example.test", "LastTime"), (options.ApiKey, options.SenderEmail, options.SenderName));
    }

    [Theory]
    [InlineData("k", "s", true)]
    [InlineData("", "s", false)]
    [InlineData("k", "", false)]
    [InlineData("  ", "s", false)]
    [InlineData("k", "  ", false)]
    public void IsConfigured_NeedsAnApiKeyAndASenderEmail(string apiKey, string senderEmail, bool expected)
    {
        Assert.Equal(expected, new BrevoOptions { ApiKey = apiKey, SenderEmail = senderEmail }.IsConfigured);
    }

    [Fact]
    public void TheSenderNameIsOptional()
    {
        Assert.True(new BrevoOptions { ApiKey = "k", SenderEmail = "s@example.test", SenderName = "" }.IsConfigured);
    }
}

// ---------------------------------------------------------------- the request, content and failure handling

public class BrevoOtpSenderTests
{
    private const string ApiKey = "xkeysib-test-key-should-never-appear-in-logs";
    private const string Code = "482913";

    private readonly RecordingHandler _handler = new();
    private readonly ListLogger<BrevoOtpSender> _log = new();

    private static readonly User Recipient = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Email = "recipient@example.com",
        PasswordHash = "stored-password-hash-value",
    };

    private BrevoOtpSender Sender(string senderName = "LastTime", string senderEmail = "lasttime.sender@example.test") =>
        new(new StubHttpClientFactory(_handler),
            Options.Create(new BrevoOptions { ApiKey = ApiKey, SenderEmail = senderEmail, SenderName = senderName }),
            _log);

    [Fact]
    public async Task TheRequest_MatchesTheBrevoApiShape()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.Uri!.ToString());
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("application/json", request.ContentType);

        var json = request.Json;
        Assert.Equal(
            new[] { "sender", "to", "subject", "textContent", "htmlContent" }.Order(),
            json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["email", "name"], json.GetProperty("sender").EnumerateObject().Select(p => p.Name).Order());
        var to = Assert.Single(json.GetProperty("to").EnumerateArray());
        Assert.Equal(["email"], to.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task VerificationEmail_GoesToTheUser_WithTheCode_AndSaysWhatItIsFor()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(["recipient@example.com"], request.To);
        Assert.Equal("Your LastTime verification code", request.Subject);
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
        Assert.Equal("Your LastTime password reset code", request.Subject);
        Assert.Contains("reset the password", request.Text);
        Assert.Contains(Code, request.Text);
        Assert.DoesNotContain("verify your email", request.Text);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task TheSubjectsAndBodies_AreExactlyTheExistingTemplates(string purpose)
    {
        var expected = OtpEmail.For(purpose, Code);

        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(expected.Subject, request.Subject);
        Assert.Equal(expected.Text, request.Text);
        Assert.Equal(expected.Html, request.Html);
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
    public async Task Emails_ContainNoAccountDetails_NoResetToken_AndNoApiKey(string purpose)
    {
        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        foreach (var body in new[] { request.Text, request.Html, request.Subject })
        {
            Assert.DoesNotContain(Recipient.PasswordHash, body);
            Assert.DoesNotContain(Recipient.Email, body);
            Assert.DoesNotContain(Recipient.Id.ToString(), body);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(ApiKey, request.Body); // the key travels in a header only
    }

    [Fact]
    public async Task TheConfiguredSenderIsUsed_AndTheNameIsLeftOutWhenNoneIsConfigured()
    {
        await Sender("LastTime", "lasttime.sender@example.test").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("Acme Support", "support@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("", "plain@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Equal(
            [("LastTime", "lasttime.sender@example.test"), ("Acme Support", "support@acme.example"), (null, "plain@acme.example")],
            _handler.Requests.Select(r => (r.SenderName, r.SenderEmail)));
    }

    [Fact]
    public async Task WhenBrevoRejectsTheEmail_NothingIsThrown_AndTheLogHasNoSecrets()
    {
        _handler.Respond = (_, _) => Task.FromResult(RecordingHandler.Refused(
            HttpStatusCode.Unauthorized, "unauthorized", "Key not found for owner@example.com"));

        await Sender().SendAsync(Recipient, Otp.PasswordReset, Code, CancellationToken.None); // must not throw

        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.Contains("401", _log.All);
        Assert.Contains("unauthorized", _log.All);
        foreach (var secret in new[] { Code, ApiKey, "owner@example.com", Recipient.Email, Recipient.PasswordHash })
            Assert.DoesNotContain(secret, _log.All);
        Assert.DoesNotContain("Key not found", _log.All); // the provider's message text is not logged
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyRefusal_IsLoggedWithItsStatus_WithoutThrowing(HttpStatusCode status)
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)); // no readable body at all

        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Contains(((int)status).ToString(), _log.All);
        Assert.Contains("unknown", _log.All);
        Assert.DoesNotContain(Code, _log.All);
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

    /// <summary>The real delivery registration, with Brevo's HTTP replaced by a recording fake. No real email can be sent.</summary>
    private WebApplicationFactory<Program> Host(bool configured = true, int? dailyLimit = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Brevo:ApiKey"] = configured ? "xkeysib-test" : "",
            ["Brevo:SenderEmail"] = configured ? "lasttime.sender@example.test" : "",
            ["Brevo:SenderName"] = "LastTime",
            ["Email:DailyLimit"] = (dailyLimit ?? 100).ToString(),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOtpSender>();
                services.RemoveAll<BrevoOtpSender>();
                services.AddOtpDelivery(configuration); // the production registration, fed only the settings above
                services.AddHttpClient(BrevoOtpSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _provider);
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
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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
        Assert.Equal(("LastTime", "lasttime.sender@example.test"), (email.SenderName, email.SenderEmail));
        Assert.Equal("xkeysib-test", email.ApiKey);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", email.Uri!.ToString());
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
    public async Task WhenBrevoFails_TheApiAnswersExactlyAsBefore_WithoutProviderDetails()
    {
        _provider.Respond = (_, _) => Task.FromResult(
            RecordingHandler.Refused(HttpStatusCode.Forbidden, "permission_denied", "provider internals"));
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
        var only = Assert.Single(_provider.Requests); // the second email never reached Brevo
        Assert.Equal(["first@example.com"], only.To);
    }

    [Fact]
    public async Task TheDailyLimitSetting_IsRead_FromEmailDailyLimit()
    {
        await using var host = Host(dailyLimit: 7);
        Assert.Equal(7, host.Services.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit);
    }

    [Fact]
    public async Task WithoutBrevoSettings_NothingIsSent_AndTheApiStillStarts_UsingTheNotConfiguredSender()
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
    public async Task WithBrevoSettings_TheBrevoSenderIsUsed_BehindTheDailyLimit()
    {
        await using var host = Host();
        Assert.IsType<DailyLimitedOtpSender>(host.Services.GetRequiredService<IOtpSender>());
        Assert.NotNull(host.Services.GetRequiredService<BrevoOtpSender>());
    }

    [Fact]
    public async Task TheStandardTestHost_CanNeverSendRealEmail()
    {
        // ApiFactory swaps the sender for a capturing one and makes Brevo's HTTP client refuse to run.
        Assert.IsType<CapturingOtpSender>(factory.Services.GetRequiredService<IOtpSender>());
        using var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(BrevoOtpSender.HttpClientName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.PostAsync("smtp/email", new StringContent("{}")));
        Assert.False(factory.Services.GetRequiredService<IOptions<BrevoOptions>>().Value.IsConfigured);
    }
}
