using System.ComponentModel.DataAnnotations;
using LastTime.Api.Domain;

namespace LastTime.Api.Features.Auth;

public record RegisterRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password);

public record RegisterResponse(Guid Id, string Email, DateTimeOffset CreatedAt)
{
    public static RegisterResponse From(User u) => new(u.Id, u.Email, u.CreatedAt);
}
