using LastTime.Api.Data;
using Microsoft.EntityFrameworkCore;

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
    .AllowAnyHeader()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();

app.UseCors(CorsPolicyName);

app.UseAuthorization();

app.MapControllers();

app.Run();


// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
