namespace LastTime.Api.Features.Auth;

/// <summary>Bound from the "Jwt" configuration section. The secret is never stored in source control.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinSecretLength = 32;

    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 60;
}
