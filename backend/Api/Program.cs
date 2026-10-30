using System.Security.Claims;
using System.Threading.RateLimiting;
using Api;
using Hangfire;
using Hangfire.SqlServer;
using Infrastructure;
using Infrastructure.Ai;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
var contentRoot = builder.Configuration["Content:Path"] ?? Path.Combine(AppContext.BaseDirectory, "content");

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("InterviewAI");

// AI: Claude when a key is configured, otherwise the labelled offline engine.
var ai = builder.Configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
builder.Services.AddSingleton(ai);
builder.Services.AddSingleton(new PromptLibrary(contentRoot));
builder.Services.AddSingleton<ScriptedInterviewer>();
if (ai.Enabled)
{
    builder.Services.AddSingleton(_ => AnthropicClientFactory.Create(ai));
    builder.Services.AddSingleton<IInterviewerAi, ClaudeInterviewer>();
    builder.Services.AddSingleton<IEvaluatorAi, ClaudeEvaluator>();
}
else
{
    builder.Services.AddSingleton<IInterviewerAi>(sp => sp.GetRequiredService<ScriptedInterviewer>());
    builder.Services.AddSingleton<IEvaluatorAi, RuleBasedEvaluator>();
}

builder.Services.AddScoped<EvaluationService>();
builder.Services.AddScoped<InterviewService>();
builder.Services.AddScoped<EvaluateSessionJob>();
builder.Services.AddScoped(sp => new Seeder(sp.GetRequiredService<AppDbContext>(), contentRoot));

var billing = builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new BillingOptions();
builder.Services.AddSingleton(billing);
builder.Services.AddHttpClient<IPaymentGateway, RazorpayGateway>();

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions { QueuePollInterval = TimeSpan.FromSeconds(1) }));
builder.Services.AddHangfireServer(o => o.WorkerCount = builder.Configuration.GetValue("Hangfire:Workers", 4));

builder.Services.AddSignalR();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "iai_session";
        o.Cookie.HttpOnly = true;
        // Strict SameSite is the CSRF defence: every state-changing call comes from our own origin.
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = async c =>
        {
            // Deleted or disabled accounts, and any change of password or role, end existing sessions at once.
            var db = c.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(c.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var stamp = c.Principal.FindFirstValue(AuthEndpoints.StampClaim);
            var ok = Guid.TryParse(stamp, out var parsed) && await db.Users.AnyAsync(u => u.Id == id && !u.IsDisabled && u.SecurityStamp == parsed);
            if (!ok)
            {
                c.RejectPrincipal();
                await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();

var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
var aiPerMinute = builder.Configuration.GetValue("RateLimits:AiPerMinute", 30);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("ai", c => RateLimitPartition.GetFixedWindowLimiter(
        c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = aiPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = ProfileEndpoints.MaxResumeBytes + 4096);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The reverse proxy is the only thing that can reach this container in production.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddProblemDetails();

var app = builder.Build();

// Must run first: mapping the Hangfire dashboard below initialises its storage, which needs the database to exist.
using (var scope = app.Services.CreateScope())
{
    if (app.Configuration.GetValue("Database:AutoMigrate", false))
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync();
    await AdminBootstrap.RunAsync(scope.ServiceProvider);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/api") ? Serilog.Events.LogEventLevel.Information
    : Serilog.Events.LogEventLevel.Debug);
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.Use((ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "microphone=(self), camera=(self)";
    // Next's static export emits inline bootstrap scripts, hence 'unsafe-inline' for scripts.
    h.ContentSecurityPolicy = "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://accounts.google.com https://checkout.razorpay.com; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com; " +
        "img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self' ws: wss: https://accounts.google.com https://lumberjack.razorpay.com; " +
        "frame-src https://accounts.google.com https://api.razorpay.com https://checkout.razorpay.com; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    return next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
// Explicit so routing runs after static files; otherwise the page fallback below claims every request first.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHub<ReportsHub>("/hubs/reports").RequireAuthorization();
app.MapHangfireDashboard("/ops/jobs", new DashboardOptions { Authorization = [new AdminDashboardFilter()] });

app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapSessionEndpoints();
app.MapProgressEndpoints();
app.MapBillingEndpoints();
app.MapAdminEndpoints();

app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallback(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    var page = app.Environment.WebRootFileProvider.GetFileInfo("404.html");
    if (!page.Exists) return;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(page);
});

await app.RunAsync();

public partial class Program;
