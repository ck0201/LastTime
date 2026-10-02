using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;
using System.Text;

namespace LastTime.Api.Features.Auth;

[ApiController]
[Route("api/auth")]
public class AuthController(LastTimeDbContext db, IOptions<JwtOptions> jwtOptions, TimeProvider time, IOtpSender otpSender)
    : ControllerBase
{
    // PBKDF2 with a random salt and a versioned format, from the ASP.NET Core shared framework.
    private static readonly PasswordHasher<User> Hasher = new();

    // Used only to spend the same hashing time when an account does not exist (see ForgotPassword / VerifyPasswordReset).
    private static readonly string DummyHash = Hasher.HashPassword(new User(), "000000");

    /// <summary>
    /// Every forgot-password answer takes at least this long. Sending the email (a call to the provider) is by far the
    /// slowest step and only happens for real accounts, so without a floor the response time would reveal which emails
    /// are registered. Keep it comfortably above the provider's usual response time; a call that takes longer than this
    /// is not hidden.
    /// </summary>
    public static readonly TimeSpan ForgotPasswordMinimumDuration = TimeSpan.FromMilliseconds(1500);

    [HttpPost("register")]
    public async Task<ActionResult<RegisterResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        // Emails are stored lowercase so the unique index on Email is case-insensitive in effect.
        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return EmailTaken();

        var user = new User { Email = email, IsActive = true };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two registrations for the same email raced past the check above.
            return EmailTaken();
        }

        return StatusCode(StatusCodes.Status201Created, RegisterResponse.From(user));
    }


    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Same 401 for unknown email, inactive account and wrong password, so nothing about the account is revealed.
        if (user is null || !user.IsActive) return InvalidCredentials();
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return InvalidCredentials();

        return new LoginResponse(CreateToken(user));
    }

    /// <summary>Checks the signed-in user's email verification code.</summary>
    [Authorize]
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken ct)
    {
        var user = await CurrentActiveUserAsync(ct);
        if (user is null) return Unauthorized();

        // One answer for a wrong, expired, used or locked-out code, so the reason is not revealed.
        return await Otp.VerifyAsync(db, time, user, Otp.EmailVerification, request.Code, ct) ? NoContent() : InvalidCode();
    }

    /// <summary>Issues a new email verification code for the signed-in user (60 seconds apart at most).</summary>
    [Authorize]
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        var user = await CurrentActiveUserAsync(ct);
        if (user is null) return Unauthorized();
        if (user.EmailVerifiedAt is not null) return Problem(title: "Email is already verified.", statusCode: StatusCodes.Status409Conflict);

        if (await CooldownRemainingAsync(user, Otp.EmailVerification, ct) is { } wait) return TooManyRequests(wait);

        var code = await Otp.IssueAsync(db, time, user, Otp.EmailVerification, ct);
        await otpSender.SendAsync(user, Otp.EmailVerification, code, ct);
        return Accepted(); // the code was issued and handed to the sender; this does not claim an email arrived
    }

    /// <summary>Issues a password reset code. The answer is identical whether or not the account exists.</summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        try
        {
            var email = NormalizeEmail(request.Email);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

            if (user is { IsActive: true } && await CooldownRemainingAsync(user, Otp.PasswordReset, ct) is null)
            {
                var code = await Otp.IssueAsync(db, time, user, Otp.PasswordReset, ct);
                await otpSender.SendAsync(user, Otp.PasswordReset, code, ct);
            }
            else
            {
                // Unknown, inactive or within the cooldown: say nothing different, and do the same amount of hashing.
                _ = Hasher.HashPassword(new User(), Otp.GenerateCode());
            }
        }
        finally
        {
            // Whatever happened (including a failed provider call), wait out the rest of the minimum duration.
            await PadToMinimumAsync(started, ForgotPasswordMinimumDuration, ct);
        }

        return Accepted();
    }

    /// <summary>
    /// Checks a password reset code. On success it returns an opaque reset token (random, held only as a hash on the
    /// server, single use, 10 minutes) that the password change will present together with the email.
    /// </summary>
    [HttpPost("verify-password-reset")]
    public async Task<ActionResult<PasswordResetTokenResponse>> VerifyPasswordReset(VerifyPasswordResetRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is not { IsActive: true })
        {
            _ = Hasher.VerifyHashedPassword(new User(), DummyHash, request.Code.Trim()); // same cost as a real check
            return InvalidCode();
        }

        if (!await Otp.VerifyAsync(db, time, user, Otp.PasswordReset, request.Code, ct)) return InvalidCode();

        // EmailVerifiedAt is deliberately untouched, and nothing about the user is returned.
        var token = await Otp.IssueResetTokenAsync(db, time, user, ct);
        return new PasswordResetTokenResponse(token);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Waits until at least <paramref name="minimum"/> has passed since <paramref name="startedAt"/> (a TimeProvider
    /// timestamp). No wait when the work already took that long. Cancellation is not swallowed.
    /// </summary>
    private async Task PadToMinimumAsync(long startedAt, TimeSpan minimum, CancellationToken ct)
    {
        var remaining = minimum - time.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, ct);
    }

    private Task<User?> CurrentActiveUserAsync(CancellationToken ct)
    {
        var id = User.GetUserId();
        return db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
    }

    /// <summary>How much longer a new code of this purpose must wait, or null when one may be issued now.</summary>
    private async Task<TimeSpan?> CooldownRemainingAsync(User user, string purpose, CancellationToken ct)
    {
        var createdAt = await db.OtpCodes
            .Where(o => o.UserId == user.Id && o.Purpose == purpose)
            .Select(o => (DateTimeOffset?)o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (createdAt is null) return null;

        var remaining = createdAt.Value + Otp.ResendCooldown - time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private ObjectResult InvalidCode() =>
        Problem(title: "Invalid or expired code.", statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult TooManyRequests(TimeSpan wait)
    {
        Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Problem(title: "Please wait before requesting another code.", statusCode: StatusCodes.Status429TooManyRequests);
    }

    private string CreateToken(User user)
    {
        var jwt = jwtOptions.Value;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
            ]),
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = DateTime.UtcNow.AddMinutes(jwt.ExpirationMinutes),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private UnauthorizedObjectResult InvalidCredentials() =>
        Unauthorized(new ProblemDetails { Title = "Invalid email or password.", Status = StatusCodes.Status401Unauthorized });

    private ObjectResult EmailTaken() =>
        Problem(title: "Email already registered.", statusCode: StatusCodes.Status409Conflict);
}
