using LastTime.Api.Data;
using LastTime.Api.Domain;
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
public class AuthController(LastTimeDbContext db, IOptions<JwtOptions> jwtOptions) : ControllerBase
{
    // PBKDF2 with a random salt and a versioned format, from the ASP.NET Core shared framework.
    private static readonly PasswordHasher<User> Hasher = new();

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
