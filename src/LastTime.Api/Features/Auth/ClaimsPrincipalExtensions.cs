using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LastTime.Api.Features.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's id, taken from the JWT "sub" claim. Only call on [Authorize]d endpoints.</summary>
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated principal has no 'sub' claim."));
}
