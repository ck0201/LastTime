using System.Globalization;
using System.Security.Cryptography;
using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// One-time codes for email verification and password reset. Each user has at most one current code per purpose;
/// issuing a new one replaces the old. Only a hash is stored, and a code can be used once.
/// </summary>
public static class Otp
{
    public const string EmailVerification = "EmailVerification";
    public const string PasswordReset = "PasswordReset";

    /// <summary>
    /// Not a code the user types: the opaque token handed out after a correct PasswordReset code, which the password
    /// change will present. It lives in the same table (one row per user, single use, short lived).
    /// </summary>
    public const string PasswordResetToken = "PasswordResetToken";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int MaxFailedAttempts = 5;

    // Same mechanism as passwords: salted PBKDF2 from the ASP.NET Core shared framework.
    private static readonly PasswordHasher<User> Hasher = new();

    /// <summary>A random 6-digit code, "000000" to "999999".</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates a new code for the user and purpose, replacing any existing one (its attempts start again from 0), and
    /// returns the plaintext code so the caller can email it. The code must never be logged or returned over HTTP.
    /// </summary>
    public static async Task<string> IssueAsync(
        LastTimeDbContext db, TimeProvider time, User user, string purpose, CancellationToken ct = default)
    {
        if (purpose != EmailVerification && purpose != PasswordReset)
            throw new ArgumentException($"Unknown OTP purpose '{purpose}'.", nameof(purpose));

        var code = GenerateCode();
        await StoreAsync(db, time, user, purpose, code, ct);
        return code;
    }

    /// <summary>
    /// Creates the opaque token returned once a PasswordReset code was verified (a random 256-bit value, never
    /// derivable from anything the client knows), replacing any earlier one. Redeem it with <see cref="VerifyAsync"/>
    /// and <see cref="PasswordResetToken"/>. Like codes, it is stored only as a hash.
    /// </summary>
    public static async Task<string> IssueResetTokenAsync(
        LastTimeDbContext db, TimeProvider time, User user, CancellationToken ct = default)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await StoreAsync(db, time, user, PasswordResetToken, token, ct);
        return token;
    }

    private static async Task StoreAsync(
        LastTimeDbContext db, TimeProvider time, User user, string purpose, string code, CancellationToken ct)
    {
        var hash = Hasher.HashPassword(user, code);
        var now = time.GetUtcNow();
        var expiresAt = now + Lifetime;

        // One atomic statement, so replacing the old code (or two requests at once) cannot race.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO otp_codes ("Id", "UserId", "Purpose", "CodeHash", "CreatedAt", "ExpiresAt", "FailedAttempts")
            VALUES ({Guid.NewGuid()}, {user.Id}, {purpose}, {hash}, {now}, {expiresAt}, 0)
            ON CONFLICT ("UserId", "Purpose") DO UPDATE SET
                "CodeHash" = EXCLUDED."CodeHash",
                "CreatedAt" = EXCLUDED."CreatedAt",
                "ExpiresAt" = EXCLUDED."ExpiresAt",
                "FailedAttempts" = 0
            """, ct);
    }

    /// <summary>
    /// True when <paramref name="code"/> (or a <see cref="PasswordResetToken"/>) is the user's current, unexpired value for the purpose and fewer than
    /// <see cref="MaxFailedAttempts"/> wrong guesses were made. A correct code is consumed (deleted), and for
    /// <see cref="EmailVerification"/> it also sets <see cref="User.EmailVerifiedAt"/>.
    /// </summary>
    public static async Task<bool> VerifyAsync(
        LastTimeDbContext db, TimeProvider time, User user, string purpose, string code, CancellationToken ct = default)
    {
        RequirePurpose(purpose);
        var now = time.GetUtcNow();

        var otp = await db.OtpCodes.AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == user.Id && o.Purpose == purpose, ct);
        if (otp is null) return false;

        // Spend one attempt first. The expiry and attempt-limit checks are part of this same UPDATE, so even
        // simultaneous guesses cannot get more than MaxFailedAttempts tries. A correct code deletes the row below,
        // so the spent attempt only matters for wrong guesses. CodeHash pins this exact code, not a newer one.
        var spent = await db.OtpCodes
            .Where(o => o.Id == otp.Id && o.CodeHash == otp.CodeHash && o.ExpiresAt > now && o.FailedAttempts < MaxFailedAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FailedAttempts, o => o.FailedAttempts + 1), ct);
        if (spent == 0) return false;

        if (Hasher.VerifyHashedPassword(user, otp.CodeHash, code.Trim()) == PasswordVerificationResult.Failed)
            return false;

        // Consume it. Deleting must affect exactly one row: if two requests both had the right code, only one wins.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var consumed = await db.OtpCodes
            .Where(o => o.Id == otp.Id && o.CodeHash == otp.CodeHash)
            .ExecuteDeleteAsync(ct);
        if (consumed != 1) return false; // already used; the transaction rolls back

        if (purpose == EmailVerification)
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, now), ct);
            user.EmailVerifiedAt = now;
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static void RequirePurpose(string purpose)
    {
        if (purpose != EmailVerification && purpose != PasswordReset && purpose != PasswordResetToken)
            throw new ArgumentException($"Unknown OTP purpose '{purpose}'.", nameof(purpose));
    }
}
