namespace LastTime.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the user proved they own the email address; null until then.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public List<Item> Items { get; set; } = [];
    public List<OtpCode> OtpCodes { get; set; } = [];
}
