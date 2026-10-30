using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Infrastructure;

public class BillingOptions
{
    public string? KeyId { get; set; }
    public string? KeySecret { get; set; }
    public string? WebhookSecret { get; set; }
    public long PlusPricePaise { get; set; } = 19900;
    public long ProPricePaise { get; set; } = 49900;
    public long PriceFor(string plan) => plan == Domain.Plans.Pro ? ProPricePaise : PlusPricePaise;
    public int ProDays { get; set; } = 30;
    public bool Enabled => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(KeySecret);
}

public interface IPaymentGateway
{
    Task<string> CreateOrderAsync(long amountPaise, string receipt, CancellationToken ct);
}

public class RazorpayGateway(HttpClient http, BillingOptions options) : IPaymentGateway
{
    public async Task<string> CreateOrderAsync(long amountPaise, string receipt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders")
        {
            Content = JsonContent.Create(new { amount = amountPaise, currency = "INR", receipt }),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.KeyId}:{options.KeySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()!;
    }
}

public static class RazorpaySignature
{
    public static string Compute(string payload, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    public static bool IsValid(string payload, string? signature, string? secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Compute(payload, secret)),
            Encoding.UTF8.GetBytes(signature));
    }
}
