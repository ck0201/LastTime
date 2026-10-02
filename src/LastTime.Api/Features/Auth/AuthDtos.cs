using System.ComponentModel.DataAnnotations;
using LastTime.Api.Domain;

namespace LastTime.Api.Features.Auth;

/// <summary>The password rule, shared by registration and password reset so the two cannot drift apart.</summary>
public static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
}

public record RegisterRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)] string Password);

public record RegisterResponse(Guid Id, string Email, DateTimeOffset CreatedAt)
{
    public static RegisterResponse From(User u) => new(u.Id, u.Email, u.CreatedAt);
}

public record LoginRequest(
    [Required, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password);

public record LoginResponse(string Token);

/// <summary>Six digits, with surrounding spaces tolerated (the API trims them).</summary>
public record VerifyEmailRequest(
    [Required, RegularExpression(@"^\s*\d{6}\s*$")] string Code);

public record ForgotPasswordRequest(
    [Required, EmailAddress, StringLength(320)] string Email);

public record VerifyPasswordResetRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, RegularExpression(@"^\s*\d{6}\s*$")] string Code);

/// <summary>An opaque, short-lived value the password change will present. It says nothing about who it belongs to.</summary>
public record PasswordResetTokenResponse(string ResetToken);

public record ResetPasswordRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(256)] string ResetToken,
    [Required, StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)] string NewPassword);
