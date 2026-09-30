# Last Time API: build with the .NET 9 SDK, run on the ASP.NET Core 9 runtime.
# Build context is the REPOSITORY ROOT:
#   docker build -f src/LastTime.Api/Dockerfile -t lasttime-api .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# SDK pin (global.json) and package sources (nuget.config: nuget.org only)
COPY global.json nuget.config ./

# Restore first so the package download layer is cached until the .csproj changes
COPY src/LastTime.Api/LastTime.Api.csproj src/LastTime.Api/
RUN dotnet restore src/LastTime.Api/LastTime.Api.csproj

COPY src/LastTime.Api/ src/LastTime.Api/
# Drop any bin/obj copied from a developer machine; they hold host-specific restore output
RUN rm -rf src/LastTime.Api/bin src/LastTime.Api/obj

RUN dotnet publish src/LastTime.Api/LastTime.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Run as the non-root user that ships with the base image
USER $APP_UID

# Documentation only; the real port comes from $PORT at runtime.
EXPOSE 8080

# Render provides PORT at runtime; fall back to 8080 for local/container use.
# Shell form is needed so ${PORT} is expanded when the container starts, not at build time.
# The database connection string is NOT baked in: provide ConnectionStrings__Default as an environment variable.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet LastTime.Api.dll"]
