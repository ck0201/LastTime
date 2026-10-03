using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LastTime.Api.Tests;

// ---------------------------------------------------------------- doubles for the delivery boundary

/// <summary>Stands in for SMTP: records every message that would have been sent, and behaves as told. Nothing leaves the process.</summary>
public sealed class RecordingTransport
{
    public sealed record Sent(SmtpOptions Options, MimeMessage Message)
    {
        public string[] To => Message.To.Mailboxes.Select(m => m.Address).ToArray();
        public MailboxAddress From => Message.From.Mailboxes.Single();
        public string Subject => Message.Subject ?? "";
        public string Text => Message.TextBody ?? "";
        public string Html => Message.HtmlBody ?? "";
        public string Code => Regex.Match(Text, @"\b\d{6}\b").Value;
    }

    private readonly List<Sent> _sent = [];
    public IReadOnlyList<Sent> Messages { get { lock (_sent) return _sent.ToArray(); } }

    /// <summary>What happens on each send; the default succeeds.</summary>
    public Func<CancellationToken, Task> Behavior { get; set; } = _ => Task.CompletedTask;

    public SmtpTransport AsTransport => SendAsync;

    private async Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        // a copy of the options, as they were at the moment of sending
        var snapshot = new SmtpOptions
        {
            Host = options.Host, Port = options.Port, Username = options.Username,
            Password = options.Password, SenderEmail = options.SenderEmail, SenderName = options.SenderName,
        };
        lock (_sent) _sent.Add(new Sent(snapshot, message));
        await Behavior(ct);
    }
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

/// <summary>
/// A tiny local SMTP server that does NOT offer STARTTLS or AUTH, and records every line the client sends. It lets a
/// test run the real MailKit transport against something safe: no real server, no network beyond loopback.
/// </summary>
public sealed class PlainSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<string> _lines = [];
    private readonly Task _loop;

    public PlainSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<string> LinesReceived { get { lock (_lines) return _lines.ToArray(); } }

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, new ASCIIEncoding()) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 localhost test ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_lines) _lines.Add(line);
                var verb = line.Split(' ')[0].ToUpperInvariant();
                if (verb is "EHLO") { await writer.WriteLineAsync("250-localhost"); await writer.WriteLineAsync("250-AUTH PLAIN LOGIN"); await writer.WriteLineAsync("250 8BITMIME"); }
                else if (verb is "HELO") await writer.WriteLineAsync("250 localhost");
                else if (verb is "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 ok");
            }
        }
        catch (Exception)
        {
            // the client hung up; that is the expected end
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await Task.WhenAny(_loop, Task.Delay(2000));
    }
}

// ---------------------------------------------------------------- settings

public class SmtpOptionsTests
{
    [Fact]
    public void DefaultsToGmailOnPort587()
    {
        var options = new SmtpOptions();
        Assert.Equal("smtp.gmail.com", options.Host);
        Assert.Equal(587, options.Port);
    }

    [Fact]
    public void BindsFromTheSmtpSection_WithTheDocumentedKeys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.test",
            ["Smtp:Port"] = "2525",
            ["Smtp:Username"] = "user@example.test",
            ["Smtp:Password"] = "p4ss",
            ["Smtp:SenderEmail"] = "from@example.test",
            ["Smtp:SenderName"] = "LastTime",
        }).Build();

        var options = configuration.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>()!;

        Assert.Equal(("mail.example.test", 2525, "user@example.test", "p4ss", "from@example.test", "LastTime"),
            (options.Host, options.Port, options.Username, options.Password, options.SenderEmail, options.SenderName));
    }

    [Theory]
    [InlineData("u", "p", "s", true)]
    [InlineData("", "p", "s", false)]
    [InlineData("u", "", "s", false)]
    [InlineData("u", "p", "", false)]
    [InlineData("  ", "p", "s", false)]
    public void IsConfigured_NeedsUsernamePasswordAndSenderEmail(string username, string password, string sender, bool expected)
    {
        var options = new SmtpOptions { Username = username, Password = password, SenderEmail = sender };
        Assert.Equal(expected, options.IsConfigured);
    }
}

// ---------------------------------------------------------------- content, sender and failure handling

public class SmtpOtpSenderTests
{
    private const string Password = "gmail-app-password-should-never-appear";
    private const string Code = "482913";

    private readonly RecordingTransport _transport = new();
    private readonly ListLogger<SmtpOtpSender> _log = new();

    private static readonly User Recipient = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Email = "recipient@example.com",
        PasswordHash = "stored-password-hash-value",
    };

    private SmtpOtpSender Sender(string senderName = "LastTime", string senderEmail = "lasttime.sender@example.test") =>
        new(_transport.AsTransport,
            Options.Create(new SmtpOptions { Username = "lasttime.sender@example.test", Password = Password, SenderEmail = senderEmail, SenderName = senderName }),
            _log);

    [Fact]
    public async Task VerificationEmail_GoesToTheUser_WithTheCode_AndSaysWhatItIsFor()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        var sent = Assert.Single(_transport.Messages);
        Assert.Equal(["recipient@example.com"], sent.To);
        Assert.Equal("Your LastTime verification code", sent.Subject);
        Assert.Contains("verify your email address", sent.Text);
        Assert.Contains("LastTime", sent.Text);
        Assert.Contains(Code, sent.Text);
        Assert.Contains(Code, sent.Html);
    }

    [Fact]
    public async Task PasswordResetEmail_GoesToTheUser_WithTheCode_AndSaysWhatItIsFor()
    {
        await Sender().SendAsync(Recipient, Otp.PasswordReset, Code, CancellationToken.None);

        var sent = Assert.Single(_transport.Messages);
        Assert.Equal(["recipient@example.com"], sent.To);
        Assert.Equal("Your LastTime password reset code", sent.Subject);
        Assert.Contains("reset the password", sent.Text);
        Assert.Contains(Code, sent.Text);
        Assert.Contains(Code, sent.Html);
        Assert.DoesNotContain("verify your email", sent.Text);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task TheSubjectsAndBodies_AreExactlyTheExistingTemplates(string purpose)
    {
        var expected = OtpEmail.For(purpose, Code);

        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var sent = Assert.Single(_transport.Messages);
        Assert.Equal(expected.Subject, sent.Subject);
        Assert.Equal(expected.Text, sent.Text.Replace("\r\n", "\n"));
        Assert.Equal(expected.Html, sent.Html);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task Emails_StateTheExpiry_FromTheOtpLifetime(string purpose)
    {
        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var sent = Assert.Single(_transport.Messages);
        var expected = $"{(int)Otp.Lifetime.TotalMinutes} minutes";
        Assert.Contains(expected, sent.Text);
        Assert.Contains(expected, sent.Html);
    }

    [Theory]
    [InlineData(Otp.EmailVerification)]
    [InlineData(Otp.PasswordReset)]
    public async Task Emails_ContainNoAccountDetails_NoResetToken_AndNoCredentials(string purpose)
    {
        await Sender().SendAsync(Recipient, purpose, Code, CancellationToken.None);

        var sent = Assert.Single(_transport.Messages);
        foreach (var body in new[] { sent.Text, sent.Html, sent.Subject })
        {
            Assert.DoesNotContain(Recipient.PasswordHash, body);
            Assert.DoesNotContain(Recipient.Email, body);
            Assert.DoesNotContain(Recipient.Id.ToString(), body);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Password, body);
        }
        Assert.DoesNotContain(Password, sent.Message.ToString()); // not in any header either
    }

    [Fact]
    public async Task TheConfiguredSenderIsUsed()
    {
        await Sender("LastTime", "lasttime.sender@example.test").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("Acme Support", "support@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);
        await Sender("", "plain@acme.example").SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Equal(
            [("LastTime", "lasttime.sender@example.test"), ("Acme Support", "support@acme.example"), ("", "plain@acme.example")],
            _transport.Messages.Select(m => (m.From.Name, m.From.Address)));
    }

    [Fact]
    public async Task TheTransportReceivesTheConfiguredConnectionSettings()
    {
        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        var options = Assert.Single(_transport.Messages).Options;
        Assert.Equal("smtp.gmail.com", options.Host);
        Assert.Equal(587, options.Port);
        Assert.Equal("lasttime.sender@example.test", options.Username);
        Assert.Equal(Password, options.Password); // handed to the transport only, never logged or put in the message
    }

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task WhenDeliveryFails_NothingIsThrown_AndTheLogHasNoSecrets(Exception failure)
    {
        _transport.Behavior = _ => throw failure;

        await Sender().SendAsync(Recipient, Otp.PasswordReset, Code, CancellationToken.None); // must not throw

        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.Contains(failure.GetType().Name, _log.All);
        foreach (var secret in new[] { Code, Password, "lasttime.sender@example.test", Recipient.Email, Recipient.PasswordHash })
            Assert.DoesNotContain(secret, _log.All);
        Assert.DoesNotContain("echoed", _log.All); // server reply text is not logged
    }

    public static TheoryData<Exception> DeliveryFailures() => new()
    {
        new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user recipient@example.com echoed by the server"),
        new MailKit.Security.AuthenticationException("535 5.7.8 Username and Password not accepted echoed"),
        new IOException("connection reset echoed"),
        new SocketException((int)SocketError.ConnectionRefused),
        new TimeoutException("echoed"),
        new TaskCanceledException("echoed (a timeout, not the caller)"),
    };

    [Fact]
    public async Task ASmtpRejection_LogsItsStatusCode_ButNotItsText()
    {
        _transport.Behavior = _ => throw new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "554 secret reply text");

        await Sender().SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None);

        Assert.Contains("TransactionFailed", _log.All);
        Assert.DoesNotContain("secret reply text", _log.All);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ThatIsNotSwallowed()
    {
        _transport.Behavior = async ct => await Task.Delay(Timeout.Infinite, ct);
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

        Assert.Empty(_transport.Messages);
        Assert.Contains(LogLevel.Error, _log.Levels);
        Assert.DoesNotContain(Code, _log.All);
    }

    // ---- the real MailKit transport, against a local server that offers no STARTTLS

    [Fact]
    public async Task TheRealTransport_RefusesToSendCredentialsWithoutStartTls()
    {
        await using var server = new PlainSmtpServer();
        var options = new SmtpOptions { Host = "127.0.0.1", Port = server.Port, Username = "lasttime.sender@example.test", Password = Password, SenderEmail = "lasttime.sender@example.test" };
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("LastTime", options.SenderEmail));
        message.To.Add(MailboxAddress.Parse("recipient@example.com"));
        message.Subject = "x";
        message.Body = new TextPart("plain") { Text = Code };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAnyAsync<Exception>(() => MailKitSmtpTransport.SendAsync(options, message, cts.Token));

        var seen = string.Join("\n", server.LinesReceived);
        Assert.Contains("EHLO", seen); // it did connect and greet the server...
        Assert.DoesNotContain("AUTH", seen, StringComparison.OrdinalIgnoreCase); // ...but never tried to log in
        Assert.DoesNotContain("MAIL FROM", seen, StringComparison.OrdinalIgnoreCase); // ...and never sent anything
        Assert.DoesNotContain(Code, seen);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes(Password)), seen); // the password was never on the wire
    }

    [Fact]
    public async Task TheSenderWithTheRealTransport_SwallowsThatFailure_AndLogsNoSecrets()
    {
        await using var server = new PlainSmtpServer();
        var sender = new SmtpOtpSender(
            MailKitSmtpTransport.SendAsync,
            Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = server.Port, Username = "lasttime.sender@example.test", Password = Password, SenderEmail = "lasttime.sender@example.test", SenderName = "LastTime" }),
            _log);

        await sender.SendAsync(Recipient, Otp.EmailVerification, Code, CancellationToken.None); // must not throw

        Assert.Contains(LogLevel.Error, _log.Levels);
        foreach (var secret in new[] { Code, Password, Recipient.Email })
            Assert.DoesNotContain(secret, _log.All);
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
    private readonly RecordingTransport _smtp = new();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The real delivery registration, with the SMTP transport replaced by a recorder. No real email can be sent.</summary>
    private WebApplicationFactory<Program> Host(bool configured = true, int? dailyLimit = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.gmail.com",
            ["Smtp:Port"] = "587",
            ["Smtp:Username"] = configured ? "lasttime.sender@example.test" : "",
            ["Smtp:Password"] = configured ? "test-app-password" : "",
            ["Smtp:SenderEmail"] = configured ? "lasttime.sender@example.test" : "",
            ["Smtp:SenderName"] = "LastTime",
            ["Email:DailyLimit"] = (dailyLimit ?? 100).ToString(),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOtpSender>();
                services.RemoveAll<SmtpOtpSender>();
                services.AddOtpDelivery(configuration); // the production registration, fed only the settings above
                services.RemoveAll<SmtpTransport>();
                services.AddSingleton<SmtpTransport>(_smtp.AsTransport);
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
        var email = Assert.Single(_smtp.Messages);
        Assert.Equal(["user@example.com"], email.To);
        Assert.Equal(("LastTime", "lasttime.sender@example.test"), (email.From.Name, email.From.Address));
        Assert.Equal(("smtp.gmail.com", 587), (email.Options.Host, email.Options.Port));
        Assert.Contains("reset the password", email.Text);

        // The emailed code is the real one, and only the code is emailed (the reset token never is).
        var verify = await client.PostAsJsonAsync("/api/auth/verify-password-reset", new { email = "user@example.com", code = email.Code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var token = (await verify.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        Assert.Single(_smtp.Messages); // verifying sent nothing more
        Assert.DoesNotContain(token, email.Text);
    }

    [Fact]
    public async Task ResendVerification_EmailsTheVerificationCode_ThatThenWorks()
    {
        await using var host = Host();
        var client = await SignedInAsync(host, "user@example.com");

        var response = await client.PostAsync("/api/auth/resend-verification", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var email = Assert.Single(_smtp.Messages);
        Assert.Equal(["user@example.com"], email.To);
        Assert.Contains("verify your email address", email.Text);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { code = email.Code });
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
    }

    [Fact]
    public async Task WhenDeliveryFails_TheApiAnswersExactlyAsBefore_WithoutProviderDetails()
    {
        _smtp.Behavior = _ => throw new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "provider internals");
        await using var host = Host();
        var client = host.CreateClient();
        await RegisterAsync(client, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("", body);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_smtp.Messages);
    }

    [Fact]
    public async Task WhenTheNetworkFails_TheApiStillAnswersAccepted()
    {
        _smtp.Behavior = _ => throw new IOException("no route to host");
        await using var host = Host();
        var client = await SignedInAsync(host, "user@example.com");

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/resend-verification", null)).StatusCode);
    }

    [Fact]
    public async Task TheDailyLimit_StopsFurtherSends_WithoutChangingTheResponses()
    {
        await using var host = Host(dailyLimit: 1);
        var client = host.CreateClient();
        await RegisterAsync(client, "first@example.com");
        await RegisterAsync(client, "second@example.com");

        var first = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "first@example.com" });
        var second = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var only = Assert.Single(_smtp.Messages); // the second email never reached SMTP
        Assert.Equal(["first@example.com"], only.To);
    }

    [Fact]
    public async Task TheDailyLimitSetting_IsRead_FromEmailDailyLimit()
    {
        await using var host = Host(dailyLimit: 7);
        Assert.Equal(7, host.Services.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit);
    }

    [Fact]
    public async Task WithoutSmtpSettings_NothingIsSent_AndTheApiStillStarts_UsingTheNotConfiguredSender()
    {
        await using var host = Host(configured: false);
        var client = host.CreateClient();
        await RegisterAsync(client, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(_smtp.Messages);
        Assert.IsType<NotConfiguredOtpSender>(host.Services.GetRequiredService<IOtpSender>());
    }

    [Fact]
    public async Task WithSmtpSettings_TheSmtpSenderIsUsed_BehindTheDailyLimit()
    {
        await using var host = Host();
        Assert.IsType<DailyLimitedOtpSender>(host.Services.GetRequiredService<IOtpSender>());
        Assert.NotNull(host.Services.GetRequiredService<SmtpOtpSender>());
    }

    [Fact]
    public async Task TheStandardTestHost_CanNeverSendRealEmail()
    {
        // ApiFactory swaps the sender for a capturing one and the transport for one that refuses to run.
        Assert.IsType<CapturingOtpSender>(factory.Services.GetRequiredService<IOtpSender>());
        var transport = factory.Services.GetRequiredService<SmtpTransport>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport(new SmtpOptions(), new MimeMessage(), CancellationToken.None));
        Assert.False(factory.Services.GetRequiredService<IOptions<SmtpOptions>>().Value.IsConfigured);
    }
}
