namespace LastTime.Api.Domain;

/// <summary>
/// A user's current one-time code for one purpose. There is at most one row per (UserId, Purpose):
/// issuing a new code replaces the old one. The code itself is never stored, only <see cref="CodeHash"/>.
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>"EmailVerification" or "PasswordReset".</summary>
    public string Purpose { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
}
