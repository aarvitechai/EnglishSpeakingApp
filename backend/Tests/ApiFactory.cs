using Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

/// <summary>Runs the real app, including Hangfire, against a throwaway database on the dev SQL Server.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string SuperAdminEmail = "root@example.com";
    public const string SuperAdminPassword = "bootstrap-password-for-tests";
    public const string RazorpaySecret = "test_key_secret";
    public const string RazorpayWebhookSecret = "test_webhook_secret";

    private static readonly string Connection =
        Environment.GetEnvironmentVariable("TEST_SQL_CONNECTION")
        ?? @"Server=DESKTOP-2ERCDGB\MSSQLSERVER01;Database=InterviewAI_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    public Action<IServiceCollection>? Overrides { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", Connection);
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("RateLimits:AuthPerMinute", "1000");
        builder.UseSetting("RateLimits:AiPerMinute", "1000");
        builder.UseSetting("Quotas:FreePerMonth", "4");
        builder.UseSetting("Billing:KeyId", "rzp_test");
        builder.UseSetting("Billing:KeySecret", RazorpaySecret);
        builder.UseSetting("Billing:WebhookSecret", RazorpayWebhookSecret);
        builder.UseSetting("SuperAdmin:Email", SuperAdminEmail);
        builder.UseSetting("SuperAdmin:Password", SuperAdminPassword);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IPaymentGateway, FakeGateway>();
            Overrides?.Invoke(s);
        });
    }

    private class FakeGateway : IPaymentGateway
    {
        public Task<string> CreateOrderAsync(long amountPaise, string receipt, CancellationToken ct) =>
            Task.FromResult("order_" + Guid.NewGuid().ToString("N"));
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
