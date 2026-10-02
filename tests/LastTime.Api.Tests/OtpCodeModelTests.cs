using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

/// <summary>Schema checks for the otp_codes table and users.EmailVerifiedAt. No OTP behavior exists yet.</summary>
[Collection(ApiCollection.Name)]
public class OtpCodeModelTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private LastTimeDbContext NewDb() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LastTimeDbContext>();

    private static OtpCode Code(string purpose) => new()
    {
        Purpose = purpose,
        CodeHash = "hash",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    private static User NewUser(string email) => new() { Email = email, PasswordHash = "hash" };

    [Fact]
    public async Task OtpCode_BelongsToUser_WithDefaults()
    {
        var db = NewDb();
        var user = NewUser("a@example.com");
        user.OtpCodes.Add(Code("EmailVerification"));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var loaded = await NewDb().OtpCodes.Include(c => c.User).SingleAsync();
        Assert.Equal(user.Id, loaded.UserId);
        Assert.Equal("a@example.com", loaded.User.Email);
        Assert.Equal("EmailVerification", loaded.Purpose);
        Assert.Equal(0, loaded.FailedAttempts);
        Assert.NotEqual(default, loaded.CreatedAt);
        Assert.True(loaded.ExpiresAt > loaded.CreatedAt);
    }

    [Fact]
    public async Task OtpCode_WithoutExistingUser_IsRejected()
    {
        var db = NewDb();
        var code = Code("PasswordReset");
        code.UserId = Guid.NewGuid();
        db.OtpCodes.Add(code);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task OnlyOneCode_PerUserAndPurpose()
    {
        var db = NewDb();
        var user = NewUser("b@example.com");
        user.OtpCodes.Add(Code("PasswordReset"));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var other = NewDb();
        var duplicate = Code("PasswordReset");
        duplicate.UserId = user.Id;
        other.OtpCodes.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task SameUser_CanHaveOneCode_ForEachPurpose()
    {
        var db = NewDb();
        var user = NewUser("c@example.com");
        user.OtpCodes.Add(Code("EmailVerification"));
        user.OtpCodes.Add(Code("PasswordReset"));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(2, await NewDb().OtpCodes.CountAsync());
    }

    [Fact]
    public async Task SamePurpose_IsAllowed_ForDifferentUsers()
    {
        var db = NewDb();
        var first = NewUser("d1@example.com");
        first.OtpCodes.Add(Code("PasswordReset"));
        var second = NewUser("d2@example.com");
        second.OtpCodes.Add(Code("PasswordReset"));
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();

        Assert.Equal(2, await NewDb().OtpCodes.CountAsync());
    }

    [Fact]
    public async Task DeletingUser_DeletesTheirCodes_ButNotOtherUsersCodes()
    {
        var db = NewDb();
        var gone = NewUser("e1@example.com");
        gone.OtpCodes.Add(Code("EmailVerification"));
        var kept = NewUser("e2@example.com");
        kept.OtpCodes.Add(Code("EmailVerification"));
        db.Users.AddRange(gone, kept);
        await db.SaveChangesAsync();

        var other = NewDb();
        other.Users.Remove(await other.Users.SingleAsync(u => u.Email == "e1@example.com"));
        await other.SaveChangesAsync();

        var remaining = await NewDb().OtpCodes.SingleAsync();
        Assert.Equal(kept.Id, remaining.UserId);
    }

    [Fact]
    public async Task Purpose_LongerThan32Characters_IsRejected()
    {
        var db = NewDb();
        var user = NewUser("f@example.com");
        user.OtpCodes.Add(Code(new string('x', 33)));
        db.Users.Add(user);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task EmailVerifiedAt_IsNull_ForNewUsers_AndCanBeSet()
    {
        var db = NewDb();
        db.Users.Add(NewUser("g@example.com"));
        await db.SaveChangesAsync();
        Assert.Null((await NewDb().Users.SingleAsync()).EmailVerifiedAt);

        var verifiedAt = DateTimeOffset.UtcNow;
        var other = NewDb();
        (await other.Users.SingleAsync()).EmailVerifiedAt = verifiedAt;
        await other.SaveChangesAsync();
        Assert.Equal(verifiedAt.ToUnixTimeMilliseconds(), (await NewDb().Users.SingleAsync()).EmailVerifiedAt!.Value.ToUnixTimeMilliseconds());
    }
}
