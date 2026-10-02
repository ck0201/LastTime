using System.Net;
using LastTime.Api.Domain;

namespace LastTime.Api.Features.Auth;

/// <summary>The wording of the one-time code emails. Provider-neutral; it only ever contains the code, never an account detail.</summary>
public static class OtpEmail
{
    public sealed record Message(string Subject, string Text, string Html);

    public static Message For(string purpose, string code)
    {
        var minutes = (int)Otp.Lifetime.TotalMinutes;
        return purpose switch
        {
            Otp.EmailVerification => Build(
                "Your LastTime verification code",
                "Use this code to verify your email address for your LastTime account.",
                code, minutes,
                "If you didn't create a LastTime account, you can safely ignore this email."),
            Otp.PasswordReset => Build(
                "Your LastTime password reset code",
                "Use this code to reset the password of your LastTime account.",
                code, minutes,
                "If you didn't ask to reset your password, you can ignore this email. Your password has not been changed."),
            _ => throw new ArgumentException($"No email is defined for purpose '{purpose}'.", nameof(purpose)),
        };
    }

    private static Message Build(string subject, string intro, string code, int minutes, string footer)
    {
        var expiry = $"This code expires in {minutes} minutes and can be used only once.";

        var text = $"Hi,\n\n{intro}\n\n    {code}\n\n{expiry}\n\n{footer}\n\n- LastTime\n";

        var html =
            $"<p>Hi,</p><p>{WebUtility.HtmlEncode(intro)}</p>" +
            $"<p style=\"font-size:28px;font-weight:bold;letter-spacing:4px\">{WebUtility.HtmlEncode(code)}</p>" +
            $"<p>{WebUtility.HtmlEncode(expiry)}</p><p>{WebUtility.HtmlEncode(footer)}</p><p>- LastTime</p>";

        return new Message(subject, text, html);
    }
}
