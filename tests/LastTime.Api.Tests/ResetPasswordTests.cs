using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

/// <summary>POST /api/auth/reset-password over real HTTP and Postgres.</summary>
[Collection(ApiCollection.Name)]
public class ResetPasswordTests(ApiFactory factory) : IAsyncLifetime
{
    private const string OldPassword = "Password123!";
    private const string NewPassword = "Brand-new-pass-456";
    private const string GenericTitle = "Invalid or expired reset token.";
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

    private LastTimeDbContext NewDb() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LastTimeDbContext>();

    /// <summary>Registers an account (password "Password123!"). Verified by default, as only verified accounts can reset.</summary>
    private async Task<Guid> AccountAsync(string email, bool verified = true)
    {
        var (_, id) = await factory.CreateAuthenticatedClientAsync(email);
        if (verified) await SetAsync(id, u => u.EmailVerifiedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        return id;
    }

    private async Task SetAsync(Guid id, Action<User> change)
    {
        var db = NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        change(user);
        await db.SaveChangesAsync();
    }

    private async Task<User> UserAsync(Guid id) => await NewDb().Users.AsNoTracking().SingleAsync(u => u.Id == id);

    /// <summary>The whole real flow: forgot-password, read the emailed code, verify it, and return the reset token.</summary>
    private async Task<string> ResetTokenAsync(string email, Guid id)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
        var code = factory.Sender.All.Last(s => s.UserId == id && s.Purpose == Otp.PasswordReset).Code;
        var verify = await _anonymous.PostAsJsonAsync("/api/auth/verify-password-reset", new { email, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        return (await verify.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.ResetToken;
    }

    private Task<HttpResponseMessage> ResetAsync(string email, string token, string password) =>
        _anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email, resetToken = token, newPassword = password });

    private async Task<HttpStatusCode> LoginAsync(string email, string password) =>
        (await _anonymous.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode;

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    // ================= success =================

    [Fact]
    public async Task VerifiedUser_CanReset_TheNewPasswordWorks_AndTheOldOneDoesNot()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);

        var response = await ResetAsync("user@example.com", token, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync()); // nothing returned, in particular not the token
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("user@example.com", OldPassword));
    }

    [Fact]
    public async Task Reset_StoresOnlyAHash_AndDoesNotTouchEmailVerifiedAt()
    {
        var id = await AccountAsync("user@example.com");
        var verifiedAt = (await UserAsync(id)).EmailVerifiedAt;
        var token = await ResetTokenAsync("user@example.com", id);

        await ResetAsync("user@example.com", token, NewPassword);

        var user = await UserAsync(id);
        Assert.NotEqual(NewPassword, user.PasswordHash);
        Assert.DoesNotContain(NewPassword, user.PasswordHash);
        Assert.Equal(verifiedAt, user.EmailVerifiedAt); // unchanged
    }

    [Fact]
    public async Task Reset_NormalizesTheEmail_AndNeedsNoSignIn()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);

        var response = await ResetAsync("  USER@Example.COM ", token, NewPassword); // _anonymous sends no JWT

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", NewPassword));
    }

    [Fact]
    public async Task Reset_ConsumesTheToken_SoItCannotBeUsedAgain()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("user@example.com", token, NewPassword)).StatusCode);
        var again = await ResetAsync("user@example.com", token, "Another-pass-789");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(GenericTitle, await TitleAsync(again));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", NewPassword)); // still the first reset's password
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("user@example.com", "Another-pass-789"));
        Assert.False(await NewDb().OtpCodes.AnyAsync(o => o.UserId == id && o.Purpose == Otp.PasswordResetToken));
    }

    [Fact]
    public async Task ASecondResetNeedsAFreshCodeAndToken()
    {
        var id = await AccountAsync("user@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("user@example.com", await ResetTokenAsync("user@example.com", id), NewPassword)).StatusCode);

        factory.Time.Advance(PastCooldown);
        var second = await ResetAsync("user@example.com", await ResetTokenAsync("user@example.com", id), "Third-password-000");

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", "Third-password-000"));
    }

    // ================= rejections change nothing =================

    [Fact]
    public async Task ExpiredToken_IsRejected_AndThePasswordIsUnchanged()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);
        var before = (await UserAsync(id)).PasswordHash;
        factory.Time.Advance(PastExpiry);

        var response = await ResetAsync("user@example.com", token, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GenericTitle, await TitleAsync(response));
        Assert.Equal(before, (await UserAsync(id)).PasswordHash);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", OldPassword));
    }

    [Fact]
    public async Task InvalidToken_IsRejected_AndThePasswordIsUnchanged()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);
        var before = (await UserAsync(id)).PasswordHash;
        var wrong = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        var response = await ResetAsync("user@example.com", wrong, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GenericTitle, await TitleAsync(response));
        Assert.Equal(before, (await UserAsync(id)).PasswordHash);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", OldPassword));
        // a wrong guess does not burn the real token
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("user@example.com", token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task NoToken_AtAll_IsRejected()
    {
        var id = await AccountAsync("user@example.com");

        var response = await ResetAsync("user@example.com", "x".PadRight(43, 'x'), NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GenericTitle, await TitleAsync(response));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", OldPassword));
        Assert.NotNull(await UserAsync(id));
    }

    [Fact]
    public async Task ASixDigitCode_IsNotAResetToken()
    {
        var id = await AccountAsync("user@example.com");
        await _anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { email = "user@example.com" });
        var code = factory.Sender.All.Last(s => s.UserId == id).Code;

        var response = await ResetAsync("user@example.com", code, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", OldPassword));
    }

    [Fact]
    public async Task AnotherUsersToken_IsRejected()
    {
        var aliceId = await AccountAsync("alice@example.com");
        await AccountAsync("bob@example.com");
        var aliceToken = await ResetTokenAsync("alice@example.com", aliceId);

        var response = await ResetAsync("bob@example.com", aliceToken, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("bob@example.com", OldPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("alice@example.com", OldPassword)); // and Alice's token is still unused
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("alice@example.com", aliceToken, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task UnknownInactiveAndUnverifiedAccounts_AreRejectedGenerically()
    {
        var inactiveId = await AccountAsync("inactive@example.com");
        var unverifiedId = await AccountAsync("unverified@example.com", verified: false);
        var knownId = await AccountAsync("known@example.com");
        var inactiveToken = await ResetTokenAsync("inactive@example.com", inactiveId);
        var unverifiedToken = await ResetTokenAsync("unverified@example.com", unverifiedId); // issuing a token does not need verification
        var knownToken = await ResetTokenAsync("known@example.com", knownId);
        await SetAsync(inactiveId, u => u.IsActive = false);
        var wrongToken = knownToken[..^1] + (knownToken[^1] == 'A' ? 'B' : 'A');

        var unknown = await ResetAsync("nobody@example.com", knownToken, NewPassword);
        var inactive = await ResetAsync("inactive@example.com", inactiveToken, NewPassword);
        var unverified = await ResetAsync("unverified@example.com", unverifiedToken, NewPassword);
        var badToken = await ResetAsync("known@example.com", wrongToken, NewPassword);

        foreach (var response in new[] { unknown, inactive, unverified, badToken })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(GenericTitle, await TitleAsync(response)); // nothing says which condition failed
        }
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("unverified@example.com", OldPassword)); // and nothing changed
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("known@example.com", OldPassword));
    }

    [Fact]
    public async Task AnUnverifiedAccount_CanNotResetEvenWithAGenuineToken_ButCanOnceVerified()
    {
        var id = await AccountAsync("user@example.com", verified: false);
        var token = await ResetTokenAsync("user@example.com", id);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync("user@example.com", token, NewPassword)).StatusCode);
        Assert.Null((await UserAsync(id)).EmailVerifiedAt); // a failed attempt never verifies the email

        await SetAsync(id, u => u.EmailVerifiedAt = DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("user@example.com", token, NewPassword)).StatusCode);
    }

    // ================= password rules (the same as registration) =================

    [Theory]
    [InlineData("")]
    [InlineData("        ")]
    [InlineData("1234567")]
    public async Task TooShortOrBlankNewPassword_IsRejected_WithoutUsingUpTheToken(string password)
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);
        var before = (await UserAsync(id)).PasswordHash;

        var response = await ResetAsync("user@example.com", token, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, (await UserAsync(id)).PasswordHash);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync("user@example.com", token, NewPassword)).StatusCode); // token still good
    }

    [Fact]
    public async Task ATooLongNewPassword_IsRejected()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);

        var response = await ResetAsync("user@example.com", token, new string('a', PasswordRules.MaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", OldPassword));
    }

    [Fact]
    public async Task ThePasswordRules_AreExactlyThoseOfRegistration()
    {
        var candidates = new[]
        {
            "", "        ", "1234567", "12345678", new string('a', PasswordRules.MaxLength), new string('a', PasswordRules.MaxLength + 1),
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            var password = candidates[i];
            var registered = await _anonymous.PostAsJsonAsync("/api/auth/register", new { email = $"reg{i}@example.com", password });

            var id = await AccountAsync($"rst{i}@example.com");
            var reset = await ResetAsync($"rst{i}@example.com", await ResetTokenAsync($"rst{i}@example.com", id), password);

            Assert.Equal(registered.StatusCode == HttpStatusCode.Created, reset.StatusCode == HttpStatusCode.NoContent);
        }
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task MalformedEmail_Returns400(string email)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, "x".PadRight(43, 'x'), NewPassword)).StatusCode);
    }

    [Fact]
    public async Task MissingToken_Returns400()
    {
        await AccountAsync("user@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync("user@example.com", "", NewPassword)).StatusCode);
    }

    // ================= concurrency =================

    [Fact]
    public async Task TwoSimultaneousResets_WithTheSameToken_CannotBothSucceed()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);

        var results = await Task.WhenAll(
            ResetAsync("user@example.com", token, "First-contender-111"),
            ResetAsync("user@example.com", token, "Second-contender-222"));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        var winner = results[0].StatusCode == HttpStatusCode.NoContent ? "First-contender-111" : "Second-contender-222";
        var loser = winner == "First-contender-111" ? "Second-contender-222" : "First-contender-111";
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", winner));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("user@example.com", loser));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("user@example.com", OldPassword));
    }

    [Fact]
    public async Task ManySimultaneousResets_ChangeThePasswordExactlyOnce()
    {
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);
        var passwords = Enumerable.Range(0, 8).Select(i => $"Contender-password-{i}").ToArray();

        var results = await Task.WhenAll(passwords.Select(p => ResetAsync("user@example.com", token, p)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        var winner = passwords[Array.FindIndex(results, r => r.StatusCode == HttpStatusCode.NoContent)];
        Assert.Equal(HttpStatusCode.OK, await LoginAsync("user@example.com", winner));
        foreach (var other in passwords.Where(p => p != winner))
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("user@example.com", other));
    }

    [Fact]
    public async Task IfTheAccountIsDeactivatedMeanwhile_NothingChanges_AndTheTokenIsNotConsumed()
    {
        // Drives the transaction hook directly: the account stops being eligible after the checks but before the update.
        var id = await AccountAsync("user@example.com");
        var token = await ResetTokenAsync("user@example.com", id);
        var user = await UserAsync(id);
        var before = user.PasswordHash;

        var db = NewDb();
        var ok = await Otp.VerifyAsync(db, factory.Time, user, Otp.PasswordResetToken, token, CancellationToken.None, async ct =>
        {
            await SetAsync(id, u => u.IsActive = false); // deactivated by someone else just before the update
            return await db.Users.Where(u => u.Id == id && u.IsActive && u.EmailVerifiedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "SHOULD-NOT-BE-WRITTEN"), ct) == 1;
        });

        Assert.False(ok);
        Assert.Equal(before, (await UserAsync(id)).PasswordHash);
        Assert.True(await NewDb().OtpCodes.AnyAsync(o => o.UserId == id && o.Purpose == Otp.PasswordResetToken)); // rolled back
    }
}
