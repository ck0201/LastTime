using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

public static class OtpDeliveryExtensions
{
    /// <summary>
    /// Registers how one-time codes are delivered: Brevo behind a daily limit. If Brevo is not configured (no API key
    /// or sender email) codes are not sent and a warning is logged, so a missing setting never stops the API.
    /// </summary>
    public static IServiceCollection AddOtpDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BrevoOptions>(configuration.GetSection(BrevoOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddHttpClient(BrevoOtpSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.brevo.com/v3/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<BrevoOtpSender>();
        services.AddSingleton<IOtpSender>(sp =>
        {
            if (!sp.GetRequiredService<IOptions<BrevoOptions>>().Value.IsConfigured)
            {
                sp.GetRequiredService<ILogger<NotConfiguredOtpSender>>().LogWarning(
                    "Email delivery is not configured (set Brevo:ApiKey and Brevo:SenderEmail): one-time codes will not be sent.");
                return new NotConfiguredOtpSender();
            }

            return new DailyLimitedOtpSender(
                sp.GetRequiredService<BrevoOtpSender>(),
                sp.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<DailyLimitedOtpSender>>());
        });

        return services;
    }
}
