using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

public static class OtpDeliveryExtensions
{
    /// <summary>
    /// Registers how one-time codes are delivered: SMTP behind a daily limit. If SMTP is not configured (no username,
    /// password or sender email) codes are not sent and a warning is logged, so a missing setting never stops the API.
    /// </summary>
    public static IServiceCollection AddOtpDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddSingleton<SmtpTransport>(MailKitSmtpTransport.SendAsync);
        services.AddSingleton<SmtpOtpSender>();
        services.AddSingleton<IOtpSender>(sp =>
        {
            if (!sp.GetRequiredService<IOptions<SmtpOptions>>().Value.IsConfigured)
            {
                sp.GetRequiredService<ILogger<NotConfiguredOtpSender>>().LogWarning(
                    "Email delivery is not configured (set Smtp:Username, Smtp:Password and Smtp:SenderEmail): one-time codes will not be sent.");
                return new NotConfiguredOtpSender();
            }

            return new DailyLimitedOtpSender(
                sp.GetRequiredService<SmtpOtpSender>(),
                sp.GetRequiredService<IOptions<EmailOptions>>().Value.DailyLimit,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<DailyLimitedOtpSender>>());
        });

        return services;
    }
}
