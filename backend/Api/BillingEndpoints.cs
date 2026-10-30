using System.Security.Claims;
using System.Text.Json;
using Domain;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record VerifyPaymentRequest(string OrderId, string PaymentId, string Signature);

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/billing");

        g.MapPost("/checkout", async (ClaimsPrincipal p, AppDbContext db, IPaymentGateway gateway, BillingOptions options, CancellationToken ct) =>
        {
            if (!options.Enabled) return Http.Problem(503, "Payments are not configured.");
            var subscription = new Subscription { UserId = p.UserId() };
            var orderId = await gateway.CreateOrderAsync(options.ProPricePaise, subscription.Id.ToString("N")[..32], ct);
            subscription.Payments.Add(new Payment { AmountPaise = options.ProPricePaise, ProviderRef = orderId });
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { orderId, keyId = options.KeyId, amountPaise = options.ProPricePaise });
        }).RequireAuthorization().RequireRateLimiting("ai");

        // Called by the browser after checkout. The webhook below covers users who close the tab first.
        g.MapPost("/verify", async (VerifyPaymentRequest r, ClaimsPrincipal p, AppDbContext db, BillingOptions options) =>
        {
            if (!RazorpaySignature.IsValid($"{r.OrderId}|{r.PaymentId}", r.Signature, options.KeySecret))
                return Http.Problem(400, "Payment could not be verified.");
            var owner = p.UserId();
            var payment = await db.Payments.Include(x => x.Subscription).FirstOrDefaultAsync(x => x.ProviderRef == r.OrderId && x.Subscription!.UserId == owner);
            if (payment is null) return Results.NotFound();
            await ActivateAsync(db, payment, r.PaymentId, options);
            return Results.Ok(new { plan = Plans.Pro, renewsAt = payment.Subscription!.RenewsAt });
        }).RequireAuthorization();

        g.MapPost("/webhook", async (HttpContext ctx, AppDbContext db, BillingOptions options) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            // The signature covers the exact raw bytes, so it must be checked before any parsing.
            if (!RazorpaySignature.IsValid(body, ctx.Request.Headers["X-Razorpay-Signature"], options.WebhookSecret))
                return Results.Unauthorized();

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.GetProperty("event").GetString() != "payment.captured") return Results.Ok();
            var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            var orderId = entity.GetProperty("order_id").GetString();
            var payment = await db.Payments.Include(x => x.Subscription).FirstOrDefaultAsync(x => x.ProviderRef == orderId);
            if (payment is not null) await ActivateAsync(db, payment, entity.GetProperty("id").GetString()!, options);
            return Results.Ok();
        });
    }

    /// <summary>Idempotent: browser verification and the webhook may both arrive, in either order.</summary>
    private static async Task ActivateAsync(AppDbContext db, Payment payment, string paymentId, BillingOptions options)
    {
        if (payment.Status == PaymentStatus.Captured) return;
        payment.Status = PaymentStatus.Captured;
        payment.ProviderPaymentId = paymentId;
        var subscription = payment.Subscription!;
        subscription.Status = SubscriptionStatus.Active;
        subscription.RenewsAt = DateTime.UtcNow.AddDays(options.ProDays);
        if (subscription.UserId is { } userId)
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Plan, Plans.Pro));
        await db.SaveChangesAsync();
    }
}
