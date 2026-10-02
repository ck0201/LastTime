using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LastTime.Api.Domain;
using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// Emails one-time codes through Resend's HTTP API (one POST to /emails). Never throws for provider problems and never
/// logs the code or the API key: a failure is logged and the caller carries on, so API responses do not change.
/// </summary>
public sealed class ResendOtpSender(
    IHttpClientFactory httpClients, IOptions<ResendOptions> options, ILogger<ResendOtpSender> logger) : IOtpSender
{
    public const string HttpClientName = "resend";

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

        var resend = options.Value;
        var from = string.IsNullOrWhiteSpace(resend.SenderName) ? resend.SenderEmail : $"{resend.SenderName} <{resend.SenderEmail}>";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(new
                {
                    from,
                    to = new[] { user.Email },
                    subject = message.Subject,
                    text = message.Text,
                    html = message.Html,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resend.ApiKey);

            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Resend did not accept the {Purpose} email for user {UserId} (HTTP {StatusCode}, error {ErrorName}).",
                    purpose, user.Id, (int)response.StatusCode, await ReadErrorNameAsync(response, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (for example the request was aborted); that is not a provider failure
        }
        catch (Exception ex)
        {
            // Timeouts and network errors end up here too. The exception never contains the code or the key.
            logger.LogError(ex, "Sending the {Purpose} email to user {UserId} through Resend failed.", purpose, user.Id);
        }
    }

    /// <summary>Resend answers errors with {"name": "...", "message": "..."}. Only the short name is logged, never the message.</summary>
    private static async Task<string> ReadErrorNameAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? "unknown"
                : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
