using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LastTime.Api.Tests;

/// <summary>
/// Proves that forgot-password takes at least a fixed minimum no matter which kind of account was asked about, so the
/// response time cannot tell registered and unregistered emails apart. The test clock is virtual: work takes no time
/// and a delay moves the clock by exactly its length, so every duration below is exact, not a wall-clock measurement.
/// </summary>
[Collection(ApiCollection.Name)]
public class ForgotPasswordTimingTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Minimum = AuthController.ForgotPasswordMinimumDuration;

    private HttpClient _anonymous = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _anonymous = factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers

    private async Task<TimeSpan> TimedAsync(Func<Task> action)
    {
        var started = factory.Time.GetTimestamp();
        await action();
        return factory.Time.GetElapsedTime(started);
    }

    private async Task<(HttpStatusCode Status, string Body, TimeSpan Elapsed)> ForgotAsync(string email, HttpClient? client = null)
    {
        HttpResponseMessage? response = null;
        var elapsed = await TimedAsync(async () =>
            response = await (client ?? _anonymous).PostAsJsonAsync("/api/auth/forgot-password", new { email }));
        return (response!.StatusCode, await response.Content.ReadAsStringAsync(), elapsed);
    }

    private async Task SetInactiveAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
    }

    /// <summary>The action itself, called directly so a test can control its cancellation token.</summary>
    private AuthController NewController(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<LastTimeDbContext>(),
        Options.Create(new JwtOptions()),
        factory.Time,
        factory.Sender);

    // ---- the minimum applies to every path

    [Fact]
    public async Task KnownUnknownInactiveAndCooldownEmails_AllTakeExactlyTheMinimum_AndAnswerTheSame()
    {
        var (_, knownId) = await factory.CreateAuthenticatedClientAsync("known@example.com");
        var (_, inactiveId) = await factory.CreateAuthenticatedClientAsync("inactive@example.com");
        await SetInactiveAsync(inactiveId);
        var (_, cooldownId) = await factory.CreateAuthenticatedClientAsync("cooldown@example.com");
        await ForgotAsync("cooldown@example.com"); // a code was just issued, so the next request is within the cooldown

        var known = await ForgotAsync("known@example.com");
        var unknown = await ForgotAsync("nobody@example.com");
        var inactive = await ForgotAsync("inactive@example.com");
        var cooldown = await ForgotAsync("cooldown@example.com");

        foreach (var result in new[] { known, unknown, inactive, cooldown })
        {
            Assert.Equal(HttpStatusCode.Accepted, result.Status);
            Assert.Equal("", result.Body);
            Assert.Equal(Minimum, result.Elapsed);
        }

        // and the paths really were different underneath
        Assert.Equal(1, factory.Sender.All.Count(s => s.UserId == knownId));
        Assert.DoesNotContain(factory.Sender.All, s => s.UserId == inactiveId);
        Assert.Equal(1, factory.Sender.All.Count(s => s.UserId == cooldownId)); // no second code
    }

    [Fact]
    public async Task ARegisteredAccount_AndAnUnknownOne_CannotBeToldApartByDuration()
    {
        await factory.CreateAuthenticatedClientAsync("known@example.com");
        // Only a registered account reaches the provider, and a real call takes time (here 400 ms of the virtual clock).
        // Without padding that is exactly the difference an attacker could measure.
        factory.Sender.OnSend = () => factory.Time.Advance(TimeSpan.FromMilliseconds(400));

        var known = await ForgotAsync("known@example.com");
        var unknown = await ForgotAsync("nobody@example.com");

        Assert.Equal(known.Elapsed, unknown.Elapsed);
        Assert.Equal(Minimum, unknown.Elapsed);
    }

    // ---- the minimum applies whatever the provider does

    [Fact]
    public async Task WhenDeliveryFails_TheResponseIsStillPadded()
    {
        var smtp = new RecordingTransport
        {
            Behavior = _ => throw new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "rejected"),
        };
        await using var host = WithRealSender(smtp);
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { email = "user@example.com", password = "Password123!" })).StatusCode);

        var result = await ForgotAsync("user@example.com", client);

        Assert.Equal(HttpStatusCode.Accepted, result.Status);
        Assert.Single(smtp.Messages); // SMTP was called, and failed
        Assert.Equal(Minimum, result.Elapsed);
    }

    [Fact]
    public async Task WhenTheMailServerIsUnreachable_TheResponseIsStillPadded()
    {
        var smtp = new RecordingTransport { Behavior = _ => throw new IOException("no route to host") };
        await using var host = WithRealSender(smtp);
        var client = host.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { email = "user@example.com", password = "Password123!" });

        var result = await ForgotAsync("user@example.com", client);

        Assert.Equal(HttpStatusCode.Accepted, result.Status);
        Assert.Equal(Minimum, result.Elapsed);
    }

    [Fact]
    public async Task EvenWhenTheSenderThrowsUnexpectedly_TheWaitStillHappens()
    {
        await factory.CreateAuthenticatedClientAsync("user@example.com");
        factory.Sender.OnSend = () => throw new InvalidOperationException("sender blew up");
        using var scope = factory.Services.CreateScope();

        Exception? thrown = null;
        var elapsed = await TimedAsync(async () =>
        {
            try { await NewController(scope).ForgotPassword(new ForgotPasswordRequest("user@example.com"), CancellationToken.None); }
            catch (Exception ex) { thrown = ex; }
        });

        Assert.IsType<InvalidOperationException>(thrown); // the failure is not hidden...
        Assert.Equal(Minimum, elapsed); // ...but it did not shorten the response either
    }

    // ---- only the remainder is waited

    [Fact]
    public async Task WorkThatTakesPartOfTheMinimum_IsPaddedOnlyForTheRest()
    {
        await factory.CreateAuthenticatedClientAsync("user@example.com");
        factory.Sender.OnSend = () => factory.Time.Advance(TimeSpan.FromMilliseconds(400)); // a quick provider call

        var result = await ForgotAsync("user@example.com");

        Assert.Equal(Minimum, result.Elapsed); // 400 ms of work + 1100 ms of padding
    }

    [Fact]
    public async Task WorkThatAlreadyTakesLongerThanTheMinimum_IsNotDelayedFurther()
    {
        await factory.CreateAuthenticatedClientAsync("user@example.com");
        var slow = Minimum + TimeSpan.FromSeconds(2);
        factory.Sender.OnSend = () => factory.Time.Advance(slow); // a very slow provider call

        var result = await ForgotAsync("user@example.com");

        Assert.Equal(HttpStatusCode.Accepted, result.Status);
        Assert.Equal(slow, result.Elapsed); // no extra waiting on top
    }

    // ---- cancellation is honored

    [Fact]
    public async Task WhenTheCallerCancelsWhileWaiting_CancellationIsNotSwallowed()
    {
        await factory.CreateAuthenticatedClientAsync("user@example.com");
        using var cts = new CancellationTokenSource();
        factory.Sender.OnSend = cts.Cancel; // the caller gives up after the work, before the padding
        using var scope = factory.Services.CreateScope();

        var elapsed = await TimedAsync(async () =>
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NewController(scope).ForgotPassword(new ForgotPasswordRequest("user@example.com"), cts.Token)));

        Assert.Equal(TimeSpan.Zero, elapsed); // it stopped waiting instead of finishing the padding
    }

    [Fact]
    public async Task ACallerThatCancelsBeforeStarting_GetsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var scope = factory.Services.CreateScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewController(scope).ForgotPassword(new ForgotPasswordRequest("user@example.com"), cts.Token));
    }

    // ---- nothing else was padded

    [Fact]
    public async Task OtherAuthEndpoints_AreNotPadded()
    {
        var (client, id) = await factory.CreateAuthenticatedClientAsync("user@example.com");

        var resend = await TimedAsync(() => client.PostAsync("/api/auth/resend-verification", null));
        var code = factory.Sender.All.Last(s => s.UserId == id).Code;
        var verify = await TimedAsync(() => client.PostAsJsonAsync("/api/auth/verify-email", new { code }));
        var login = await TimedAsync(() => _anonymous.PostAsJsonAsync("/api/auth/login", new { email = "user@example.com", password = "Password123!" }));
        var register = await TimedAsync(() => _anonymous.PostAsJsonAsync("/api/auth/register", new { email = "other@example.com", password = "Password123!" }));
        var verifyReset = await TimedAsync(() => _anonymous.PostAsJsonAsync("/api/auth/verify-password-reset", new { email = "user@example.com", code = "123456" }));

        foreach (var elapsed in new[] { resend, verify, login, register, verifyReset })
            Assert.Equal(TimeSpan.Zero, elapsed);
    }

    [Fact]
    public void TheMinimum_IsAFixedFloorLongerThanAnOrdinaryProviderCall()
    {
        Assert.True(Minimum >= TimeSpan.FromSeconds(1), $"minimum is only {Minimum}");
        Assert.True(Minimum <= TimeSpan.FromSeconds(5), $"minimum {Minimum} would make the endpoint feel broken");
    }

    // ---- wiring: the real SMTP sender, with its transport replaced by a recorder

    private WebApplicationFactory<Program> WithRealSender(RecordingTransport smtp) =>
        factory.WithWebHostBuilder(builder =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Smtp:Username"] = "lasttime.sender@example.test",
                ["Smtp:Password"] = "test-app-password",
                ["Smtp:SenderEmail"] = "lasttime.sender@example.test",
                ["Smtp:SenderName"] = "LastTime",
                ["Email:DailyLimit"] = "100",
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOtpSender>();
                services.RemoveAll<SmtpOtpSender>();
                services.AddOtpDelivery(configuration);
                services.RemoveAll<SmtpTransport>();
                services.AddSingleton<SmtpTransport>(smtp.AsTransport);
            });
        });
}
