using System.Text;
using LastTime.Api.Data;
using LastTime.Api.Features.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddDbContext<LastTimeDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
// Browser origins allowed to call the API (e.g. the hosted frontend), read from "Cors:AllowedOrigins".
// On Render set the Cors__AllowedOrigins environment variable, comma-separated for several origins.
// A JSON array in appsettings or indexed variables (Cors__AllowedOrigins__0) work too. None configured = no cross-origin access.
const string CorsPolicyName = "Frontend";
var allowedOrigins = (builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Append(builder.Configuration["Cors:AllowedOrigins"] ?? "")
    .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .Select(o => o.TrimEnd('/'))
    .Distinct()
    .ToArray();

builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
    .WithOrigins(allowedOrigins)
    .WithMethods("GET", "POST", "PUT", "DELETE") // OPTIONS preflight is answered by the CORS middleware
    .AllowAnyHeader()
    .WithExposedHeaders("Retry-After"))); // lets the browser app read how long to wait after a 429
// JWT settings come from the "Jwt" section. The secret is supplied per environment (user-secrets locally,
// the Jwt__Secret environment variable on Render) and must never be committed.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Secret.Length < JwtOptions.MinSecretLength)
    throw new InvalidOperationException(
        $"Jwt:Secret must be configured and at least {JwtOptions.MinSecretLength} characters long.");
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub" and "email" as issued
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
    });
builder.Services.AddAuthorization();
// One-time codes: the clock (replaceable in tests) and where issued codes go (Brevo, behind a daily limit).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOtpDelivery(builder.Configuration);
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();

app.UseCors(CorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();


// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
