namespace LastTime.Api.Features.Auth;

/// <summary>Bound from the "Resend" section. The API key comes from user-secrets / environment variables, never from source control.</summary>
public class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiKey { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SenderEmail);
}

/// <summary>Bound from the "Email" section; applies to whichever provider sends the mail.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Most emails sent per UTC day by this instance. 0 (or less) sends none.</summary>
    public int DailyLimit { get; set; } = 100;
}
