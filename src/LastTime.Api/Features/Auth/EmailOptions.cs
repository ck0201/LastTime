namespace LastTime.Api.Features.Auth;

/// <summary>
/// Bound from the "Smtp" section. Gmail by default (smtp.gmail.com, port 587, STARTTLS). The username and password
/// (a Gmail app password) come from user-secrets / environment variables, never from source control.
/// </summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && Port > 0 &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(SenderEmail);
}

/// <summary>Bound from the "Email" section; applies to whichever provider sends the mail.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Most emails sent per UTC day by this instance. 0 (or less) sends none.</summary>
    public int DailyLimit { get; set; } = 100;
}
