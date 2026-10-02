using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

/// <summary>The OTP endpoints over real HTTP. Codes are read from the capturing sender (no email exists yet).</summary>
[Collection(ApiCollection.Name)]
public class OtpEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    private const string InvalidCodeTitle = "Invalid or expired code.";
    private static readonly TimeSpan PastCooldown = TimeSpan.FromSeconds(61);
    private static readonly TimeSpan PastExpiry = TimeSpan.FromMinutes(10);

    private HttpClient _anonymous = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _anonymous = factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers

    private Task<(HttpClient Client, Guid UserId)> SignUpAsync(string email) => factory.CreateAuthenticatedClientAsync(email);

    private static Task<HttpResponseMessage> VerifyEmailAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/api/auth/verify-email", new { code });

    private static Task<HttpResponseMessage> ResendAsync(HttpClient client) =>
        client.PostAsync("/api/auth/resend-verification", null);

    private Task<HttpResponseMessage> ForgotAsync(string email) =>
        _anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { email });

    private Task<HttpResponseMessage> VerifyResetAsync(string email, string code) =>
        _anonymous.PostAsJsonAsync("/api/auth/verify-password-reset", new { email, code });

    private string LastCode(Guid userId, string purpose) =>
        factory.Sender.All.Last(s => s.UserId == userId && s.Purpose == purpose).Code;

    private int SentCount(Guid userId, string purpose) =>
        factory.Sender.All.Count(s => s.UserId == userId && s.Purpose == purpose);

    private LastTimeDbContext NewDb() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LastTimeDbContext>();

    private async Task<OtpCode?> RowAsync(Guid userId, string purpose) =>
        await NewDb().OtpCodes.AsNoTracking().SingleOrDefaultAsync(o => o.UserId == userId && o.Purpose == purpose);

    private async Task<User> UserAsync(Guid userId) => await NewDb().Users.AsNoTracking().SingleAsync(u => u.Id == userId);

    private async Task SetAsync(Guid userId, Action<User> change)
    {
        var db = NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        change(user);
        await db.SaveChangesAsync();
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    private static string Wrong(string code) => code == "000000" ? "000001" : "000000";

    /// <summary>Asks for a verification code through the API and returns it (as the email would have carried it).</summary>
    private async Task<string> RequestVerificationCodeAsync(HttpClient client, Guid userId)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await ResendAsync(client)).StatusCode);
        return LastCode(userId, Otp.EmailVerification);
    }

    private async Task<string> RequestResetCodeAsync(string email, Guid userId)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode);
        return LastCode(userId, Otp.PasswordReset);
    }

    // ================= verify-email =================

    [Fact]
    public async Task VerifyEmail_CorrectCode_Succeeds_AndMarksTheEmailVerified()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var code = await RequestVerificationCodeAsync(client, id);

        var response = await VerifyEmailAsync(client, code);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull((await UserAsync(id)).EmailVerifiedAt);
        Assert.Null(await RowAsync(id, Otp.EmailVerification)); // consumed
    }

    [Fact]
    public async Task VerifyEmail_WrongCode_Returns400_AndDoesNotVerify()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var code = await RequestVerificationCodeAsync(client, id);

        var response = await VerifyEmailAsync(client, Wrong(code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidCodeTitle, await TitleAsync(response));
        Assert.Null((await UserAsync(id)).EmailVerifiedAt);
        Assert.Equal(1, (await RowAsync(id, Otp.EmailVerification))!.FailedAttempts);
    }

    [Fact]
    public async Task VerifyEmail_ExpiredCode_Returns400()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var code = await RequestVerificationCodeAsync(client, id);
        factory.Time.Advance(PastExpiry);

        var response = await VerifyEmailAsync(client, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await UserAsync(id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task VerifyEmail_GivesTheSameAnswer_ForWrongExpiredLockedAndMissingCodes()
    {
        var (client, id) = await SignUpAsync("user@example.com");

        // no code was ever issued
        var none = await VerifyEmailAsync(client, "123456");

        // wrong
        var code = await RequestVerificationCodeAsync(client, id);
        var wrong = await VerifyEmailAsync(client, Wrong(code));

        // locked out: five wrong guesses, then even the right code is refused
        for (var i = 0; i < Otp.MaxFailedAttempts; i++) await VerifyEmailAsync(client, Wrong(code));
        var locked = await VerifyEmailAsync(client, code);

        // expired
        factory.Time.Advance(PastExpiry);
        var expired = await VerifyEmailAsync(client, code);

        foreach (var response in new[] { none, wrong, locked, expired })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidCodeTitle, await TitleAsync(response));
        }
    }

    [Fact]
    public async Task VerifyEmail_CodeCannotBeReused()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var code = await RequestVerificationCodeAsync(client, id);

        Assert.Equal(HttpStatusCode.NoContent, (await VerifyEmailAsync(client, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyEmailAsync(client, code)).StatusCode);
    }

    [Fact]
    public async Task VerifyEmail_Unauthenticated_Returns401()
    {
        var response = await VerifyEmailAsync(_anonymous, "123456");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VerifyEmail_AnotherUsersCode_IsRejected()
    {
        var (alice, aliceId) = await SignUpAsync("alice@example.com");
        var (bob, bobId) = await SignUpAsync("bob@example.com");
        var aliceCode = await RequestVerificationCodeAsync(alice, aliceId);
        var bobCode = await RequestVerificationCodeAsync(bob, bobId);
        if (aliceCode == bobCode) return; // a one-in-a-million coincidence would make this check meaningless

        var response = await VerifyEmailAsync(bob, aliceCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await UserAsync(aliceId)).EmailVerifiedAt);
        Assert.Null((await UserAsync(bobId)).EmailVerifiedAt);
        Assert.NotNull(await RowAsync(aliceId, Otp.EmailVerification)); // Alice's code is untouched
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("")]
    public async Task VerifyEmail_MalformedCode_Returns400_WithoutSpendingAnAttempt(string code)
    {
        var (client, id) = await SignUpAsync("user@example.com");
        await RequestVerificationCodeAsync(client, id);

        var response = await VerifyEmailAsync(client, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await RowAsync(id, Otp.EmailVerification))!.FailedAttempts);
    }

    // ================= resend-verification =================

    [Fact]
    public async Task Resend_UnverifiedUser_IssuesACode_AndHandsItToTheSender()
    {
        var (client, id) = await SignUpAsync("user@example.com");

        var response = await ResendAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var row = (await RowAsync(id, Otp.EmailVerification))!;
        Assert.Equal(0, row.FailedAttempts);
        var sent = Assert.Single(factory.Sender.All);
        Assert.Equal((id, "user@example.com", Otp.EmailVerification), (sent.UserId, sent.Email, sent.Purpose));
        Assert.Matches(new Regex(@"^\d{6}$"), sent.Code);
    }

    [Fact]
    public async Task Resend_NeverPutsTheCodeInTheResponse()
    {
        var (client, id) = await SignUpAsync("user@example.com");

        var response = await ResendAsync(client);
        var code = LastCode(id, Otp.EmailVerification);

        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(code, string.Join(" ", response.Headers.Select(h => string.Join(",", h.Value))));
    }

    [Fact]
    public async Task Resend_ReplacesThePreviousCode()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var first = await RequestVerificationCodeAsync(client, id);
        factory.Time.Advance(PastCooldown);
        var second = await RequestVerificationCodeAsync(client, id);

        Assert.Equal(1, await NewDb().OtpCodes.CountAsync(o => o.UserId == id));
        if (first != second) Assert.Equal(HttpStatusCode.BadRequest, (await VerifyEmailAsync(client, first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyEmailAsync(client, second)).StatusCode);
    }

    [Fact]
    public async Task Resend_VerifiedUser_GetsNoNewCode()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        await SetAsync(id, u => u.EmailVerifiedAt = DateTimeOffset.UtcNow);

        var response = await ResendAsync(client);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await RowAsync(id, Otp.EmailVerification));
        Assert.Empty(factory.Sender.All);
    }

    [Fact]
    public async Task Resend_WithinSixtySeconds_IsRejected_AndLeavesTheCodeAlone()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        await RequestVerificationCodeAsync(client, id);
        var createdAt = (await RowAsync(id, Otp.EmailVerification))!.CreatedAt;
        factory.Time.Advance(TimeSpan.FromSeconds(20));

        var response = await ResendAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retryAfter = response.Headers.RetryAfter!.Delta!.Value;
        Assert.InRange(retryAfter.TotalSeconds, 1, 60);
        Assert.Equal(1, SentCount(id, Otp.EmailVerification)); // nothing new was issued or sent
        Assert.Equal(createdAt, (await RowAsync(id, Otp.EmailVerification))!.CreatedAt);
    }

    [Fact]
    public async Task Resend_IsAllowedAgain_AfterTheCooldown()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        await RequestVerificationCodeAsync(client, id);
        factory.Time.Advance(PastCooldown);

        Assert.Equal(HttpStatusCode.Accepted, (await ResendAsync(client)).StatusCode);
        Assert.Equal(2, SentCount(id, Otp.EmailVerification));
    }

    [Fact]
    public async Task Resend_Unauthenticated_Returns401()
    {
        var response = await ResendAsync(_anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Sender.All);
    }

    [Fact]
    public async Task Resend_ForADeactivatedUser_Returns401_AndIssuesNothing()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        await SetAsync(id, u => u.IsActive = false);

        var response = await ResendAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await RowAsync(id, Otp.EmailVerification));
    }

    // ================= forgot-password =================

    [Fact]
    public async Task ForgotPassword_ExistingActiveUser_GetsAPasswordResetCode_EvenWhenUnverified()
    {
        var (_, id) = await SignUpAsync("user@example.com");

        var response = await ForgotAsync("user@example.com");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(await RowAsync(id, Otp.PasswordReset));
        var sent = Assert.Single(factory.Sender.All);
        Assert.Equal((id, Otp.PasswordReset), (sent.UserId, sent.Purpose));
    }

    [Fact]
    public async Task ForgotPassword_GivesTheSameAnswer_ForKnownUnknownAndInactiveAccounts()
    {
        await SignUpAsync("known@example.com");
        var (_, inactiveId) = await SignUpAsync("inactive@example.com");
        await SetAsync(inactiveId, u => u.IsActive = false);

        var known = await ForgotAsync("known@example.com");
        var unknown = await ForgotAsync("nobody@example.com");
        var inactive = await ForgotAsync("inactive@example.com");

        foreach (var response in new[] { known, unknown, inactive })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
        Assert.Single(factory.Sender.All); // only the known, active account got a code
        Assert.Null(await RowAsync(inactiveId, Otp.PasswordReset));
        Assert.Equal(1, await NewDb().OtpCodes.CountAsync());
    }

    [Theory]
    [InlineData("  USER@Example.COM ")]
    [InlineData("User@example.com")]
    public async Task ForgotPassword_NormalizesTheEmail(string typed)
    {
        var (_, id) = await SignUpAsync("user@example.com");

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(typed)).StatusCode);

        Assert.NotNull(await RowAsync(id, Otp.PasswordReset));
        Assert.Single(factory.Sender.All);
    }

    [Fact]
    public async Task ForgotPassword_NeverPutsTheCodeInTheResponse()
    {
        var (_, id) = await SignUpAsync("user@example.com");

        var response = await ForgotAsync("user@example.com");
        var code = LastCode(id, Otp.PasswordReset);

        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(code, string.Join(" ", response.Headers.Select(h => string.Join(",", h.Value))));
    }

    [Fact]
    public async Task ForgotPassword_WithinSixtySeconds_AnswersTheSame_ButIssuesNothingNew()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var first = await ForgotAsync("user@example.com");
        var createdAt = (await RowAsync(id, Otp.PasswordReset))!.CreatedAt;
        factory.Time.Advance(TimeSpan.FromSeconds(20));

        var second = await ForgotAsync("user@example.com");

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(1, SentCount(id, Otp.PasswordReset));
        Assert.Equal(createdAt, (await RowAsync(id, Otp.PasswordReset))!.CreatedAt);

        factory.Time.Advance(PastCooldown);
        await ForgotAsync("user@example.com");
        Assert.Equal(2, SentCount(id, Otp.PasswordReset));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task ForgotPassword_MalformedEmail_Returns400(string email)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await ForgotAsync(email)).StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_DoesNotAffectEmailVerificationCodes()
    {
        var (client, id) = await SignUpAsync("user@example.com");
        var verification = await RequestVerificationCodeAsync(client, id);

        await ForgotAsync("user@example.com");

        Assert.Equal(2, await NewDb().OtpCodes.CountAsync(o => o.UserId == id));
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyEmailAsync(client, verification)).StatusCode);
    }

    // ================= verify-password-reset =================

    [Fact]
    public async Task VerifyPasswordReset_CorrectCode_ReturnsAnOpaqueToken_AndConsumesTheCode()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);

        var response = await VerifyResetAsync("user@example.com", code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["resetToken"], json.RootElement.EnumerateObject().Select(p => p.Name)); // nothing else is exposed
        var token = json.RootElement.GetProperty("resetToken").GetString()!;
        Assert.Matches(new Regex(@"^[A-Za-z0-9_-]{43}$"), token); // 256 random bits, URL-safe

        Assert.Null(await RowAsync(id, Otp.PasswordReset)); // the six-digit code is spent
        var stored = (await RowAsync(id, Otp.PasswordResetToken))!;
        Assert.NotEqual(token, stored.CodeHash); // only a hash of the token is stored
    }

    [Fact]
    public async Task VerifyPasswordReset_DoesNotExposeTheCodeTheUserOrTheEmail()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);

        var response = await VerifyResetAsync("user@example.com", code);
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join(" ", response.Headers.Select(h => $"{h.Key}:{string.Join(",", h.Value)}"));

        foreach (var secret in new[] { id.ToString(), id.ToString("N"), "user@example.com", "hash" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, headers, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain($"\"{code}\"", body);
    }

    [Fact]
    public async Task VerifyPasswordReset_LeavesEmailVerifiedAtUnchanged()
    {
        var (_, unverifiedId) = await SignUpAsync("unverified@example.com");
        var (_, verifiedId) = await SignUpAsync("verified@example.com");
        var verifiedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await SetAsync(verifiedId, u => u.EmailVerifiedAt = verifiedAt);

        var codeA = await RequestResetCodeAsync("unverified@example.com", unverifiedId);
        var codeB = await RequestResetCodeAsync("verified@example.com", verifiedId);
        Assert.Equal(HttpStatusCode.OK, (await VerifyResetAsync("unverified@example.com", codeA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyResetAsync("verified@example.com", codeB)).StatusCode);

        Assert.Null((await UserAsync(unverifiedId)).EmailVerifiedAt); // still unverified
        Assert.Equal(verifiedAt, (await UserAsync(verifiedId)).EmailVerifiedAt); // untouched
    }

    [Fact]
    public async Task VerifyPasswordReset_WrongExpiredLockedAndUnusedCodes_AreRejectedTheSameWay()
    {
        var (_, id) = await SignUpAsync("user@example.com");

        var none = await VerifyResetAsync("user@example.com", "123456"); // nothing issued yet
        var code = await RequestResetCodeAsync("user@example.com", id);
        var wrong = await VerifyResetAsync("user@example.com", Wrong(code));
        for (var i = 0; i < Otp.MaxFailedAttempts; i++) await VerifyResetAsync("user@example.com", Wrong(code));
        var locked = await VerifyResetAsync("user@example.com", code);
        factory.Time.Advance(PastExpiry);
        var expired = await VerifyResetAsync("user@example.com", code);
        var unknownEmail = await VerifyResetAsync("nobody@example.com", "123456");

        foreach (var response in new[] { none, wrong, locked, expired, unknownEmail })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidCodeTitle, await TitleAsync(response));
        }
        Assert.Null(await RowAsync(id, Otp.PasswordResetToken)); // no token was issued
    }

    [Fact]
    public async Task VerifyPasswordReset_CodeCannotBeReused()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);

        Assert.Equal(HttpStatusCode.OK, (await VerifyResetAsync("user@example.com", code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyResetAsync("user@example.com", code)).StatusCode);
    }

    [Fact]
    public async Task VerifyPasswordReset_AnotherUsersCode_IsRejected()
    {
        var (_, aliceId) = await SignUpAsync("alice@example.com");
        var (_, bobId) = await SignUpAsync("bob@example.com");
        var aliceCode = await RequestResetCodeAsync("alice@example.com", aliceId);
        var bobCode = await RequestResetCodeAsync("bob@example.com", bobId);
        if (aliceCode == bobCode) return; // a one-in-a-million coincidence would make this check meaningless

        var response = await VerifyResetAsync("bob@example.com", aliceCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await RowAsync(bobId, Otp.PasswordResetToken));
        Assert.NotNull(await RowAsync(aliceId, Otp.PasswordReset)); // Alice's code is untouched
    }

    [Fact]
    public async Task VerifyPasswordReset_ForADeactivatedUser_IsRejected()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);
        await SetAsync(id, u => u.IsActive = false);

        var response = await VerifyResetAsync("user@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidCodeTitle, await TitleAsync(response));
    }

    [Fact]
    public async Task VerifyPasswordReset_NormalizesTheEmail_AndNeedsNoSignIn()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);

        var response = await VerifyResetAsync("  USER@Example.COM ", code); // _anonymous sends no JWT

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("12345", "user@example.com")]
    [InlineData("123456", "not-an-email")]
    [InlineData("", "user@example.com")]
    public async Task VerifyPasswordReset_MalformedInput_Returns400(string code, string email)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyResetAsync(email, code)).StatusCode);
    }

    // ---- the reset token is what the password change will consume

    [Fact]
    public async Task ResetToken_CanBeRedeemedOnce_ForItsOwnUserOnly()
    {
        var (_, aliceId) = await SignUpAsync("alice@example.com");
        var (_, bobId) = await SignUpAsync("bob@example.com");
        var code = await RequestResetCodeAsync("alice@example.com", aliceId);
        var token = (await (await VerifyResetAsync("alice@example.com", code)).Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        var alice = await UserAsync(aliceId);
        var bob = await UserAsync(bobId);

        Assert.False(await Otp.VerifyAsync(NewDb(), factory.Time, bob, Otp.PasswordResetToken, token)); // not Bob's
        Assert.False(await Otp.VerifyAsync(NewDb(), factory.Time, alice, Otp.PasswordResetToken, token + "x"));
        Assert.True(await Otp.VerifyAsync(NewDb(), factory.Time, alice, Otp.PasswordResetToken, token));
        Assert.False(await Otp.VerifyAsync(NewDb(), factory.Time, alice, Otp.PasswordResetToken, token)); // single use
    }

    [Fact]
    public async Task ResetToken_ExpiresAfterTenMinutes()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var code = await RequestResetCodeAsync("user@example.com", id);
        var token = (await (await VerifyResetAsync("user@example.com", code)).Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        var user = await UserAsync(id);

        factory.Time.Advance(PastExpiry);

        Assert.False(await Otp.VerifyAsync(NewDb(), factory.Time, user, Otp.PasswordResetToken, token));
    }

    [Fact]
    public async Task ResetToken_ANewerOneReplacesTheOlder()
    {
        var (_, id) = await SignUpAsync("user@example.com");
        var firstCode = await RequestResetCodeAsync("user@example.com", id);
        var first = (await (await VerifyResetAsync("user@example.com", firstCode)).Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        factory.Time.Advance(PastCooldown);
        var secondCode = await RequestResetCodeAsync("user@example.com", id);
        var second = (await (await VerifyResetAsync("user@example.com", secondCode)).Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
        var user = await UserAsync(id);

        Assert.NotEqual(first, second);
        Assert.Equal(1, await NewDb().OtpCodes.CountAsync(o => o.UserId == id && o.Purpose == Otp.PasswordResetToken));
        Assert.False(await Otp.VerifyAsync(NewDb(), factory.Time, user, Otp.PasswordResetToken, first));
        Assert.True(await Otp.VerifyAsync(NewDb(), factory.Time, user, Otp.PasswordResetToken, second));
    }

    [Fact]
    public async Task ExistingLoginAndRegistration_AreUnaffected()
    {
        // No verification is required to sign in, and registration still behaves as before (including its 409).
        var (client, _) = await SignUpAsync("user@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/items")).StatusCode);
        var again = await _anonymous.PostAsJsonAsync("/api/auth/register", new { email = "user@example.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
