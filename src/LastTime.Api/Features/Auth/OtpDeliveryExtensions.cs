using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

public static class OtpDeliveryExtensions
{
    /// <summary>
    /// Registers how one-time codes are delivered: Resend behind a daily limit. If Resend is not configured
    /// (no API key or sender email) codes are not sent and a warning is logged, so a missing setting never stops the API.
    /// </summary>
    public static IServiceCollection AddOtpDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddHttpClient(ResendOtpSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<ResendOtpSender>();
        services.AddSingleton<IOtpSender>(sp =>
        {
            if (!sp.GetRequiredService<IOptions<ResendOptions>>().Value.IsConfigured)
            {
                sp.GetRequiredService<ILogger<NotConfiguredOtpSender>>().LogWarning(
                    "Email delivery is not configured (set Resend:ApiKey and Resend:SenderEmail): one-time codes will not be sent.");
                return new NotConfiguredOtpSender();
            }

            return new DailyLimitedOtpSender(
                sp.GetRequiredService<ResendOtpSender>(),
                sp.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<DailyLimitedOtpSender>>());
        });

        return services;
    }
}
