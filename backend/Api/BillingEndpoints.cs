using System.Security.Claims;
using System.Text.Json;
using Domain;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record CheckoutRequest(string Plan);
public record VerifyPaymentRequest(string OrderId, string PaymentId, string Signature);

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/billing");

        g.MapPost("/checkout", async (CheckoutRequest r, ClaimsPrincipal p, AppDbContext db, IPaymentGateway gateway, BillingOptions options, CancellationToken ct) =>
        {
            if (!options.Enabled) return Http.Problem(503, "Payments are not configured.");
            if (r.Plan is not (Plans.Plus or Plans.Pro)) return Http.Problem(400, "Please choose a valid plan.");
            var amount = options.PriceFor(r.Plan);
            var subscription = new Subscription { UserId = p.UserId(), Plan = r.Plan };
            var orderId = await gateway.CreateOrderAsync(amount, subscription.Id.ToString("N")[..32], ct);
            subscription.Payments.Add(new Payment { AmountPaise = amount, ProviderRef = orderId });
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { orderId, keyId = options.KeyId, amountPaise = amount });
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
            return Results.Ok(new { plan = payment.Subscription!.Plan, renewsAt = payment.Subscription!.RenewsAt });
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
        // Renewing early adds to the time already paid for rather than overwriting it.
        var now = DateTime.UtcNow;
        var paidUntil = await db.Subscriptions
            .Where(s => s.UserId == subscription.UserId && s.Id != subscription.Id && s.Plan == subscription.Plan && s.Status == SubscriptionStatus.Active && s.RenewsAt > now)
            .MaxAsync(s => s.RenewsAt);
        subscription.RenewsAt = (paidUntil ?? now).AddDays(options.ProDays);
        await db.SaveChangesAsync();
    }
}
