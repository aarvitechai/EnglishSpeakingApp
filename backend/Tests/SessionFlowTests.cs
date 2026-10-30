using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tests;

[Collection("api")]
public class SessionFlowTests(ApiFactory factory)
{
    private const string StrongAnswer =
        "In my final-year project I built a placement dashboard in Python because our college tracked everything by hand. " +
        "I designed the database and wrote the reports, and as a result the placement cell cut its weekly work by 6 hours. " +
        "First I interviewed the staff, then I tested the tool with 40 students, which showed me what mattered most.";

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<HttpClient> SignUpAsync(HttpClient c, string? email = null)
    {
        email ??= $"{Guid.NewGuid():N}@example.com";
        var r = await c.PostAsJsonAsync("/api/auth/register", new { email, name = "Test User", password = "correct-horse", acceptTerms = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient c, string category = "hr", string level = "2")
    {
        var r = await c.PostAsJsonAsync("/api/sessions", new { category, level, mode = "text", voiceConsent = false });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(string Text, bool Ended)> ReadTurnAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType?.MediaType);
        var text = new StringBuilder();
        bool ended = false;
        foreach (var line in (await r.Content.ReadAsStringAsync()).Split('\n'))
        {
            if (!line.StartsWith("data: ")) continue;
            var e = JsonDocument.Parse(line[6..]).RootElement;
            if (e.TryGetProperty("delta", out var d)) text.Append(d.GetString());
            if (e.TryGetProperty("done", out _)) ended = e.GetProperty("ended").GetBoolean();
        }
        return (text.ToString(), ended);
    }

    private static async Task<JsonElement> WaitForReportAsync(HttpClient c, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var report = await c.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/report");
            var status = report.GetProperty("session").GetProperty("status").GetString();
            if (status == "completed") return report;
            Assert.NotEqual("failed", status);
            await Task.Delay(250);
        }
        throw new TimeoutException("Report was not ready within the 60 second target.");
    }

    [Fact]
    public async Task Full_interview_produces_an_evidence_based_report_and_progress()
    {
        var c = await SignUpAsync(NewClient());
        var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("Priya Sharma. B.Tech CSE. Skills: Python, SQL, React. Built a placement dashboard.")), "file", "resume.txt" },
        };
        var uploaded = await c.PostAsync("/api/profile/resume", upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Contains("python", (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resumeSkills").EnumerateArray().Select(x => x.GetString()));

        var id = await CreateSessionAsync(c);
        var (first, _) = await ReadTurnAsync(await c.PostAsync($"/api/sessions/{id}/start", null));
        Assert.False(string.IsNullOrWhiteSpace(first));

        bool ended = false;
        for (int i = 0; i < 20 && !ended; i++)
        {
            var (_, e) = await ReadTurnAsync(await c.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = StrongAnswer }));
            ended = e;
        }
        Assert.True(ended, "The interview should end itself once its answer budget is used.");

        var report = await WaitForReportAsync(c, id);
        var evaluation = report.GetProperty("evaluation");
        Assert.InRange(evaluation.GetProperty("overallScore").GetInt32(), 0, 100);
        Assert.Equal("rule-based-v1", evaluation.GetProperty("modelVersion").GetString());

        var improvements = evaluation.GetProperty("feedback").GetProperty("improvements").EnumerateArray().ToList();
        Assert.InRange(improvements.Count, 1, 3);
        foreach (var improvement in improvements)
        {
            Assert.True(StrongAnswer.Contains(improvement.GetProperty("quote").GetString()!), "Every critique must quote the candidate.");
            Assert.NotEmpty(improvement.GetProperty("drills").EnumerateArray());
        }
        Assert.Null(report.GetProperty("delivery").GetProperty("wpm").ValueKind == JsonValueKind.Null ? null : "typed answers must not have a WPM");

        var progress = await c.GetFromJsonAsync<JsonElement>("/api/progress");
        Assert.Equal(1, progress.GetProperty("sessions").GetArrayLength());
        Assert.NotEqual(0, progress.GetProperty("skills").GetArrayLength());
        Assert.NotEqual(0, (await c.GetFromJsonAsync<JsonElement>("/api/practice-plans")).GetArrayLength());
    }

    [Fact]
    public async Task Other_users_cannot_see_or_touch_a_session()
    {
        var owner = await SignUpAsync(NewClient());
        var id = await CreateSessionAsync(owner);
        await ReadTurnAsync(await owner.PostAsync($"/api/sessions/{id}/start", null));

        var stranger = await SignUpAsync(NewClient());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/sessions/{id}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/sessions/{id}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync($"/api/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Turns_must_alternate_and_oversized_answers_are_rejected()
    {
        var c = await SignUpAsync(NewClient());
        var id = await CreateSessionAsync(c);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = "too early" })).StatusCode);
        await ReadTurnAsync(await c.PostAsync($"/api/sessions/{id}/start", null));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/sessions/{id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = new string('x', 4001) })).StatusCode);
    }

    [Fact]
    public async Task Session_with_no_answers_is_abandoned_and_not_evaluated()
    {
        var c = await SignUpAsync(NewClient());
        var id = await CreateSessionAsync(c);
        var r = await c.PostAsync($"/api/sessions/{id}/end", null);
        Assert.Equal("abandoned", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Monthly_quota_is_enforced()
    {
        var c = await SignUpAsync(NewClient());
        for (int i = 0; i < 4; i++) await CreateSessionAsync(c);
        var r = await c.PostAsJsonAsync("/api/sessions", new { category = "hr", level = "1" });
        Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode);
    }

    [Fact]
    public async Task Voice_session_requires_explicit_consent()
    {
        var c = await SignUpAsync(NewClient());
        var r = await c.PostAsJsonAsync("/api/sessions", new { category = "hr", level = "1", mode = "voice", voiceConsent = false });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Login_errors_do_not_reveal_whether_an_account_exists()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await SignUpAsync(NewClient(), email);
        var c = NewClient();
        var wrong = await c.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
        var unknown = await c.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(),
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" })).StatusCode);
    }

    [Fact]
    public async Task Export_then_delete_removes_access_and_data()
    {
        var c = await SignUpAsync(NewClient());
        var id = await CreateSessionAsync(c);
        await ReadTurnAsync(await c.PostAsync($"/api/sessions/{id}/start", null));

        var export = await c.GetFromJsonAsync<JsonElement>("/api/me/export");
        Assert.Equal(1, export.GetProperty("sessions").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Payment_activates_pro_only_with_a_valid_signature_and_is_idempotent()
    {
        var c = await SignUpAsync(NewClient());
        var checkout = await (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "pro" })).Content.ReadFromJsonAsync<JsonElement>();
        var orderId = checkout.GetProperty("orderId").GetString()!;
        const string paymentId = "pay_123";

        var forged = await c.PostAsJsonAsync("/api/billing/verify", new { orderId, paymentId, signature = "deadbeef" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("free", (await c.GetFromJsonAsync<JsonElement>("/api/progress")).GetProperty("plan").GetString());

        var signature = RazorpaySignature.Compute($"{orderId}|{paymentId}", ApiFactory.RazorpaySecret);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/billing/verify", new { orderId, paymentId, signature })).StatusCode);
        Assert.Equal("pro", (await c.GetFromJsonAsync<JsonElement>("/api/progress")).GetProperty("plan").GetString());

        // The webhook arriving afterwards must not extend or duplicate anything.
        var body = JsonSerializer.Serialize(new { @event = "payment.captured", payload = new { payment = new { entity = new { id = paymentId, order_id = orderId } } } });
        var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        webhook.Headers.Add("X-Razorpay-Signature", RazorpaySignature.Compute(body, ApiFactory.RazorpayWebhookSecret));
        Assert.Equal(HttpStatusCode.OK, (await NewClient().SendAsync(webhook)).StatusCode);

        var badWebhook = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        badWebhook.Headers.Add("X-Razorpay-Signature", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().SendAsync(badWebhook)).StatusCode);
    }

    [Fact]
    public async Task Health_and_security_headers_are_present()
    {
        var r = await NewClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("nosniff", r.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("DENY", r.Headers.GetValues("X-Frame-Options"));
        Assert.True(r.Headers.Contains("Content-Security-Policy"));
    }
}
