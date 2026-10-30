using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tests;

[Collection("api")]
public class AccessTests(ApiFactory factory)
{
    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<(HttpClient Client, string Email, Guid Id)> SignUpAsync()
    {
        var c = NewClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        var r = await c.PostAsJsonAsync("/api/auth/register", new { email, name = "Test User", password = "correct-horse", acceptTerms = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (c, email, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var c = NewClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { email = ApiFactory.SuperAdminEmail, password = ApiFactory.SuperAdminPassword });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    private async Task<(HttpClient Client, Guid Id)> NewAdminAsync(HttpClient root)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var created = await (await root.PostAsJsonAsync("/api/admin/staff", new { email, name = "An Admin", role = "admin" })).Content.ReadFromJsonAsync<JsonElement>();
        var c = NewClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new { email, password = created.GetProperty("temporaryPassword").GetString() });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        return (c, created.GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient c, string level) =>
        c.PostAsJsonAsync("/api/sessions", new { category = "hr", level, mode = "text", voiceConsent = false });

    [Fact]
    public async Task Super_admin_is_created_from_config_and_needs_a_new_password()
    {
        var c = await SuperAdminAsync();
        var me = await c.GetFromJsonAsync<JsonElement>("/api/me/");
        Assert.Equal("super_admin", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Signing_up_cannot_grant_a_staff_role()
    {
        var c = NewClient();
        var r = await c.PostAsJsonAsync("/api/auth/register", new { email = ApiFactory.SuperAdminEmail, name = "Impostor", password = "correct-horse", acceptTerms = true });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        var (user, _, _) = await SignUpAsync();
        Assert.Equal("candidate", (await user.GetFromJsonAsync<JsonElement>("/api/me/")).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Role_permissions_are_enforced_on_every_admin_route()
    {
        var (user, _, userId) = await SignUpAsync();
        var (admin, _) = await NewAdminAsync(await SuperAdminAsync());

        foreach (var path in new[] { "/api/admin/metrics", "/api/admin/users", "/api/admin/audit", "/api/admin/payments" })
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("/api/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/admin/users/{userId}/role", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/admin/users/{userId}/plan", new { plan = "pro", days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/admin/staff", new { email = "x@example.com", name = "X", role = "admin" })).StatusCode);
    }

    [Fact]
    public async Task Admin_can_manage_candidates_but_not_staff()
    {
        var root = await SuperAdminAsync();
        var (admin, adminId) = await NewAdminAsync(root);
        var (_, _, otherAdminId) = await SignUpAsync();
        await root.PostAsJsonAsync($"/api/admin/users/{otherAdminId}/role", new { role = "admin" });

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/admin/users/{otherAdminId}/status", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/admin/users/{otherAdminId}/reset-password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/users/{adminId}/status", new { disabled = true })).StatusCode);

        var (user, email, userId) = await SignUpAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/users/{userId}/status", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/me/")).StatusCode);
        var relogin = await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" });
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/users/{userId}/status", new { disabled = false })).StatusCode);

        var reset = await (await admin.PostAsync($"/api/admin/users/{userId}/reset-password", null)).Content.ReadFromJsonAsync<JsonElement>();
        var temp = reset.GetProperty("temporaryPassword").GetString();
        Assert.Equal(HttpStatusCode.OK, (await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = temp })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" })).StatusCode);

        var audit = await (await root.GetAsync("/api/admin/audit")).Content.ReadAsStringAsync();
        Assert.Contains("user.disable", audit);
        Assert.Contains("user.reset_password", audit);
        Assert.DoesNotContain(temp!, audit);
    }

    [Fact]
    public async Task Super_admin_cannot_lock_themselves_out()
    {
        var root = await SuperAdminAsync();
        var id = (await root.GetFromJsonAsync<JsonElement>("/api/me/")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await root.PostAsJsonAsync($"/api/admin/users/{id}/role", new { role = "candidate" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await root.PostAsJsonAsync($"/api/admin/users/{id}/status", new { disabled = true })).StatusCode);
    }

    [Fact]
    public async Task A_role_change_ends_the_users_existing_session()
    {
        var root = await SuperAdminAsync();
        var (user, _, id) = await SignUpAsync();
        await root.PostAsJsonAsync($"/api/admin/users/{id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/me/")).StatusCode);
    }

    [Fact]
    public async Task Changing_password_keeps_this_device_and_signs_out_others()
    {
        var (first, email, _) = await SignUpAsync();
        var second = NewClient();
        await second.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" });

        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync("/api/me/password", new { currentPassword = "wrong", newPassword = "new-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsJsonAsync("/api/me/password", new { currentPassword = "correct-horse", newPassword = "new-password-1" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/me/")).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var (_, email, _) = await SignUpAsync();
        var c = NewClient();
        for (int i = 0; i < 10; i++)
            await c.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
        var r = await c.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Theory]
    [InlineData("free", "1", true)]
    [InlineData("free", "2", true)]
    [InlineData("free", "3", false)]
    [InlineData("plus", "4", true)]
    [InlineData("plus", "5", false)]
    [InlineData("pro", "5", true)]
    public async Task Plans_unlock_the_right_levels(string plan, string level, bool allowed)
    {
        var root = await SuperAdminAsync();
        var (c, _, id) = await SignUpAsync();
        if (plan != "free") await root.PostAsJsonAsync($"/api/admin/users/{id}/plan", new { plan, days = 30 });
        var r = await StartAsync(c, level);
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.PaymentRequired, r.StatusCode);
    }

    [Fact]
    public async Task Every_level_has_enough_questions_and_level_one_gives_hints()
    {
        var root = await SuperAdminAsync();
        var (c, _, id) = await SignUpAsync();
        await root.PostAsJsonAsync($"/api/admin/users/{id}/plan", new { plan = "pro", days = 30 });

        foreach (var category in new[] { "hr", "behavioural", "technical" })
        foreach (var level in new[] { "1", "5" })
        {
            var created = await (await c.PostAsJsonAsync("/api/sessions", new { category, level, mode = "text", voiceConsent = false })).Content.ReadFromJsonAsync<JsonElement>();
            var body = await c.GetFromJsonAsync<JsonElement>($"/api/sessions/{created.GetProperty("id").GetGuid()}");
            int hints = body.GetProperty("hints").EnumerateObject().Count();
            if (level == "1") Assert.True(hints >= 3, $"{category} level 1 had {hints} hints");
            else Assert.Equal(0, hints);
        }
    }

    [Fact]
    public async Task Paying_activates_the_plan_and_early_renewal_adds_time()
    {
        var (c, _, _) = await SignUpAsync();
        DateTime Renew(JsonElement e) => e.GetProperty("renewsAt").GetDateTime();

        async Task<JsonElement> PayAsync()
        {
            var order = await (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "plus" })).Content.ReadFromJsonAsync<JsonElement>();
            var orderId = order.GetProperty("orderId").GetString()!;
            Assert.Equal(19900, order.GetProperty("amountPaise").GetInt64());
            var signature = RazorpaySignature.Compute($"{orderId}|pay_1", ApiFactory.RazorpaySecret);
            return await (await c.PostAsJsonAsync("/api/billing/verify", new { orderId, paymentId = "pay_1", signature })).Content.ReadFromJsonAsync<JsonElement>();
        }

        var first = Renew(await PayAsync());
        var second = Renew(await PayAsync());
        Assert.True((second - first).TotalDays > 29, "A second payment should extend the first.");
        Assert.Equal("plus", (await c.GetFromJsonAsync<JsonElement>("/api/progress")).GetProperty("plan").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "free" })).StatusCode);
    }
}
