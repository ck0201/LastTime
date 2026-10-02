using System.Text.RegularExpressions;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class OtpTests(ApiFactory factory) : IAsyncLifetime
{
    /// <summary>A clock the tests move by hand, so expiry never depends on the wall clock.</summary>
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly FakeTime _time = new();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private LastTimeDbContext NewDb() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LastTimeDbContext>();

    private async Task<User> NewUserAsync(string email = "user@example.com")
    {
        var db = NewDb();
        var user = new User { Email = email, PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private Task<string> IssueAsync(User user, string purpose = Otp.EmailVerification) =>
        Otp.IssueAsync(NewDb(), _time, user, purpose);

    private Task<bool> VerifyAsync(User user, string code, string purpose = Otp.EmailVerification) =>
        Otp.VerifyAsync(NewDb(), _time, user, purpose, code);

    private async Task<OtpCode?> RowAsync(User user, string purpose = Otp.EmailVerification) =>
        await NewDb().OtpCodes.AsNoTracking().SingleOrDefaultAsync(o => o.UserId == user.Id && o.Purpose == purpose);

    private async Task<DateTimeOffset?> VerifiedAtAsync(User user) =>
        (await NewDb().Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).EmailVerifiedAt;

    /// <summary>Any code that is not the real one.</summary>
    private static string Wrong(string code) => code == "000000" ? "000001" : "000000";

    // ---- generation

    [Fact]
    public void GenerateCode_IsAlwaysExactlySixDigits()
    {
        for (var i = 0; i < 2000; i++) Assert.Matches(new Regex(@"^\d{6}$"), Otp.GenerateCode());
    }

    [Fact]
    public void GenerateCode_IsNotPredictable()
    {
        // 500 draws from 1,000,000 values: a fixed or low-entropy generator would repeat a lot; a good one almost never does.
        var codes = Enumerable.Range(0, 500).Select(_ => Otp.GenerateCode()).ToHashSet();
        Assert.True(codes.Count > 490, $"only {codes.Count} distinct codes in 500 draws");
    }

    // ---- storage and hashing

    [Fact]
    public async Task Issue_StoresUserPurposeAndExpiryFromTheTimeProvider()
    {
        var user = await NewUserAsync();
        await IssueAsync(user, Otp.PasswordReset);

        var row = (await RowAsync(user, Otp.PasswordReset))!;
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal("PasswordReset", row.Purpose);
        Assert.Equal(_time.Now, row.CreatedAt);
        Assert.Equal(_time.Now.AddMinutes(10), row.ExpiresAt);
        Assert.Equal(0, row.FailedAttempts);
    }

    [Fact]
    public async Task Issue_NeverStoresThePlaintextCode_AndTheHashVerifiesWithPasswordHasher()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        var row = (await RowAsync(user))!;
        Assert.NotEqual(code, row.CodeHash);
        Assert.True(row.CodeHash.Length > 40, "expected a PasswordHasher-style hash, not a short value");
        var hasher = new PasswordHasher<User>();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, row.CodeHash, code));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, row.CodeHash, Wrong(code)));
    }

    [Fact]
    public async Task Issue_ReturnsASixDigitCode()
    {
        var user = await NewUserAsync();
        Assert.Matches(new Regex(@"^\d{6}$"), await IssueAsync(user));
    }

    [Fact]
    public async Task Issue_WithAnUnknownPurpose_Throws()
    {
        var user = await NewUserAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Otp.IssueAsync(NewDb(), _time, user, "Whatever"));
        await Assert.ThrowsAsync<ArgumentException>(() => Otp.VerifyAsync(NewDb(), _time, user, "Whatever", "123456"));
    }

    // ---- replacement

    [Fact]
    public async Task IssuingAgain_ReplacesTheOldCode_KeepingOneRow()
    {
        var user = await NewUserAsync();
        var first = await IssueAsync(user);
        _time.Now = _time.Now.AddMinutes(3);
        var second = await IssueAsync(user);

        Assert.Equal(1, await NewDb().OtpCodes.CountAsync());
        var row = (await RowAsync(user))!;
        Assert.Equal(_time.Now, row.CreatedAt);
        Assert.Equal(_time.Now.AddMinutes(10), row.ExpiresAt);

        if (first != second) Assert.False(await VerifyAsync(user, first), "the old code must be invalid");
        Assert.True(await VerifyAsync(user, second));
    }

    [Fact]
    public async Task IssuingAgain_ResetsFailedAttempts()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);
        await VerifyAsync(user, Wrong(code));
        await VerifyAsync(user, Wrong(code));
        Assert.Equal(2, (await RowAsync(user))!.FailedAttempts);

        await IssueAsync(user);

        Assert.Equal(0, (await RowAsync(user))!.FailedAttempts);
    }

    [Fact]
    public async Task EachPurposeHasItsOwnCode()
    {
        var user = await NewUserAsync();
        var verification = await IssueAsync(user, Otp.EmailVerification);
        var reset = await IssueAsync(user, Otp.PasswordReset);

        Assert.Equal(2, await NewDb().OtpCodes.CountAsync());
        if (verification != reset)
        {
            Assert.False(await VerifyAsync(user, verification, Otp.PasswordReset)); // a code only works for its own purpose
            Assert.False(await VerifyAsync(user, reset, Otp.EmailVerification));
        }
        Assert.True(await VerifyAsync(user, verification, Otp.EmailVerification));
        Assert.True(await VerifyAsync(user, reset, Otp.PasswordReset));
    }

    // ---- verification

    [Fact]
    public async Task CorrectCode_Succeeds_AndIsConsumed()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        Assert.True(await VerifyAsync(user, code));
        Assert.Null(await RowAsync(user));
    }

    [Fact]
    public async Task SameCode_CannotBeUsedTwice()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        Assert.True(await VerifyAsync(user, code));
        Assert.False(await VerifyAsync(user, code));
    }

    [Fact]
    public async Task NoCode_IsInvalid()
    {
        var user = await NewUserAsync();
        Assert.False(await VerifyAsync(user, "123456"));
    }

    [Fact]
    public async Task CodeOfAnotherUser_IsInvalid()
    {
        var alice = await NewUserAsync("alice@example.com");
        var bob = await NewUserAsync("bob@example.com");
        var code = await IssueAsync(alice);

        Assert.False(await VerifyAsync(bob, code));
        Assert.NotNull(await RowAsync(alice)); // untouched
    }

    [Fact]
    public async Task CodeWithSurroundingSpaces_IsAccepted()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);
        Assert.True(await VerifyAsync(user, $"  {code} "));
    }

    [Fact]
    public async Task WrongCode_IsInvalid_AndCountsAnAttempt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        Assert.False(await VerifyAsync(user, Wrong(code)));
        Assert.Equal(1, (await RowAsync(user))!.FailedAttempts);
        Assert.False(await VerifyAsync(user, Wrong(code)));
        Assert.Equal(2, (await RowAsync(user))!.FailedAttempts);
        Assert.NotNull(await RowAsync(user)); // a wrong guess never consumes the code
    }

    // ---- expiry (deterministic through TimeProvider)

    [Fact]
    public async Task Code_IsValidJustBeforeTenMinutes_AndInvalidAtTenMinutes()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        _time.Now = _time.Now.AddMinutes(10).AddSeconds(-1);
        // Not consumed here: a wrong guess only proves the row is still live (it counts an attempt).
        Assert.False(await VerifyAsync(user, Wrong(code)));
        Assert.Equal(1, (await RowAsync(user))!.FailedAttempts);

        _time.Now = _time.Now.AddSeconds(1); // exactly 10 minutes after issuing
        Assert.False(await VerifyAsync(user, code));
        Assert.NotNull(await RowAsync(user)); // expired rows are not removed by a check
    }

    [Fact]
    public async Task CorrectCode_WorksJustBeforeExpiry()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        _time.Now = _time.Now.AddMinutes(9).AddSeconds(59);

        Assert.True(await VerifyAsync(user, code));
    }

    [Fact]
    public async Task ExpiredCode_IsInvalid_AndDoesNotCountAnAttempt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);
        _time.Now = _time.Now.AddMinutes(11);

        Assert.False(await VerifyAsync(user, code));
        Assert.Equal(0, (await RowAsync(user))!.FailedAttempts);
        Assert.Null(await VerifiedAtAsync(user));
    }

    [Fact]
    public async Task IssuingAfterExpiry_GivesAFreshValidCode()
    {
        var user = await NewUserAsync();
        await IssueAsync(user);
        _time.Now = _time.Now.AddMinutes(30);
        var fresh = await IssueAsync(user);

        Assert.True(await VerifyAsync(user, fresh));
    }

    // ---- attempt limit

    [Fact]
    public async Task AfterFiveFailedAttempts_EvenTheCorrectCodeIsInvalid()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        for (var i = 0; i < Otp.MaxFailedAttempts; i++) Assert.False(await VerifyAsync(user, Wrong(code)));
        Assert.Equal(5, (await RowAsync(user))!.FailedAttempts);

        Assert.False(await VerifyAsync(user, code));
        Assert.Equal(5, (await RowAsync(user))!.FailedAttempts); // the count stops at the limit
        Assert.NotNull(await RowAsync(user));
    }

    [Fact]
    public async Task CorrectCode_StillWorksOnTheFifthAttempt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        for (var i = 0; i < Otp.MaxFailedAttempts - 1; i++) Assert.False(await VerifyAsync(user, Wrong(code)));

        Assert.True(await VerifyAsync(user, code));
    }

    [Fact]
    public async Task ANewCode_AfterLockout_Works()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);
        for (var i = 0; i < Otp.MaxFailedAttempts; i++) await VerifyAsync(user, Wrong(code));

        var fresh = await IssueAsync(user);

        Assert.True(await VerifyAsync(user, fresh));
    }

    // ---- EmailVerifiedAt

    [Fact]
    public async Task EmailVerification_Success_SetsEmailVerifiedAt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user, Otp.EmailVerification);
        _time.Now = _time.Now.AddMinutes(2);

        Assert.True(await VerifyAsync(user, code, Otp.EmailVerification));

        Assert.Equal(_time.Now, await VerifiedAtAsync(user));
    }

    [Fact]
    public async Task EmailVerification_WrongCode_DoesNotSetEmailVerifiedAt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user, Otp.EmailVerification);

        await VerifyAsync(user, Wrong(code), Otp.EmailVerification);

        Assert.Null(await VerifiedAtAsync(user));
    }

    [Fact]
    public async Task PasswordReset_Success_DoesNotSetEmailVerifiedAt()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user, Otp.PasswordReset);

        Assert.True(await VerifyAsync(user, code, Otp.PasswordReset));

        Assert.Null(await VerifiedAtAsync(user));
    }

    [Fact]
    public async Task PasswordReset_Success_LeavesAnExistingEmailVerifiedAtUntouched()
    {
        var user = await NewUserAsync();
        var verified = await IssueAsync(user, Otp.EmailVerification);
        Assert.True(await VerifyAsync(user, verified, Otp.EmailVerification));
        var verifiedAt = _time.Now;

        _time.Now = _time.Now.AddDays(1);
        var reset = await IssueAsync(user, Otp.PasswordReset);
        Assert.True(await VerifyAsync(user, reset, Otp.PasswordReset));

        Assert.Equal(verifiedAt, await VerifiedAtAsync(user));
    }

    // ---- concurrency

    [Fact]
    public async Task SimultaneousCorrectAttempts_ConsumeTheCodeOnlyOnce()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => VerifyAsync(user, code))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(await RowAsync(user));
        Assert.NotNull(await VerifiedAtAsync(user));
    }

    [Fact]
    public async Task SimultaneousWrongGuesses_CannotExceedTheAttemptLimit()
    {
        var user = await NewUserAsync();
        var code = await IssueAsync(user);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => VerifyAsync(user, Wrong(code)))));

        Assert.DoesNotContain(true, results);
        Assert.Equal(Otp.MaxFailedAttempts, (await RowAsync(user))!.FailedAttempts); // exactly 5 attempts were allowed
        Assert.False(await VerifyAsync(user, code)); // and the real code is now locked out
    }

    [Fact]
    public async Task SimultaneousIssues_LeaveOneValidCode()
    {
        var user = await NewUserAsync();

        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => IssueAsync(user))));

        Assert.Equal(1, await NewDb().OtpCodes.CountAsync());
        // Only the last write survives: exactly one of the issued codes matches the stored hash, and that one works.
        var row = (await RowAsync(user))!;
        var hasher = new PasswordHasher<User>();
        var survivors = codes.Distinct()
            .Where(c => hasher.VerifyHashedPassword(user, row.CodeHash, c) == PasswordVerificationResult.Success)
            .ToList();
        var survivor = Assert.Single(survivors);

        Assert.True(await VerifyAsync(user, survivor));
        Assert.False(await VerifyAsync(user, survivor)); // and it is single use
    }
}
