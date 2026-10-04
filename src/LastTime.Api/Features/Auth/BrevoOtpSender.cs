using System.Net.Http.Json;
using System.Text.Json;
using LastTime.Api.Domain;
using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// Emails one-time codes through Brevo's HTTPS API (one POST to /v3/smtp/email). Never throws for provider problems
/// and never logs the code or the API key: a failure is logged and the caller carries on, so API responses do not
/// change.
/// </summary>
public sealed class BrevoOtpSender(
    IHttpClientFactory httpClients, IOptions<BrevoOptions> options, ILogger<BrevoOtpSender> logger) : IOtpSender
{
    public const string HttpClientName = "brevo";

    public async Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        OtpEmail.Message message;
        try
        {
            message = OtpEmail.For(purpose, code);
        }
        catch (ArgumentException)
        {
            logger.LogError("No email is defined for OTP purpose {Purpose}; nothing was sent.", purpose);
            return;
        }

        var brevo = options.Value;
        var sender = new Dictionary<string, string> { ["email"] = brevo.SenderEmail };
        if (!string.IsNullOrWhiteSpace(brevo.SenderName)) sender["name"] = brevo.SenderName;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email")
            {
                Content = JsonContent.Create(new
                {
                    sender,
                    to = new[] { new { email = user.Email } },
                    subject = message.Subject,
                    textContent = message.Text,
                    htmlContent = message.Html,
                }),
            };
            request.Headers.Add("api-key", brevo.ApiKey); // a header, so the key is never part of a logged body
            request.Headers.Add("accept", "application/json");

            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Brevo did not accept the {Purpose} email for user {UserId} (HTTP {StatusCode}, error {ErrorCode}).",
                    purpose, user.Id, (int)response.StatusCode, await ReadErrorCodeAsync(response, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (for example the request was aborted); that is not a provider failure
        }
        catch (Exception ex)
        {
            // Timeouts and network errors end up here too. The exception never contains the code or the key.
            logger.LogError(ex, "Sending the {Purpose} email to user {UserId} through Brevo failed.", purpose, user.Id);
        }
    }

    /// <summary>Brevo answers errors with {"code": "...", "message": "..."}. Only the short code is logged, never the message.</summary>
    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() ?? "unknown"
                : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
