using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LastTime.Api.Features.Auth;

[ApiController]
[Route("api/auth")]
public class AuthController(LastTimeDbContext db) : ControllerBase
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

    private ObjectResult EmailTaken() =>
        Problem(title: "Email already registered.", statusCode: StatusCodes.Status409Conflict);
}
