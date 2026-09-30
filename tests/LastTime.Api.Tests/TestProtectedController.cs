using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LastTime.Api.Tests;

/// <summary>Test-only endpoint that requires a valid JWT. Registered by ApiFactory; not part of the real API.</summary>
[ApiController]
[Route("api/test/whoami")]
[Authorize]
public class TestProtectedController : ControllerBase
{
    [HttpGet]
    public string Get() => User.FindFirst("sub")!.Value;
}
