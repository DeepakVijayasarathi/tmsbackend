using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Ai;
using Tms.Api.Modules.Analytics;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Classroom;
using Tms.Api.Modules.Commerce;
using Tms.Api.Modules.Commerce.Providers;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Gamification;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;
using Tms.Api.Modules.Notifications;
using Tms.Api.Modules.Referrals;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<TmsDbContext>(options =>
{
    var cs = config["Database:ConnectionString"];
    if (config["Database:Provider"]?.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        options.UseSqlite(cs);
    else
        options.UseNpgsql(cs);
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = config["Auth:Issuer"],
        ValidAudience = config["Auth:Issuer"],
        IssuerSigningKey = TokenService.SigningKey(config),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
});
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config["App:PublicWebUrl"] ?? "http://localhost:5180").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("Auth:RequestsPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("interest", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<CourseAuthoring>();
builder.Services.AddScoped<MilestoneService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<CertificateService>();
builder.Services.AddSingleton<JitsiTokenService>();
builder.Services.AddScoped<CommissionService>();
builder.Services.AddScoped<CommerceService>();
builder.Services.AddSingleton<SandboxPaymentProvider>();
builder.Services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<SandboxPaymentProvider>());
builder.Services.AddSingleton<IPaymentProvider, RazorpayPaymentProvider>();
builder.Services.AddSingleton<PaymentProviderRegistry>();
builder.Services.AddSingleton<IContentRetriever, LexicalRetriever>();
// Gemini by default (GEMINI_API_KEY); set Ai:Provider=claude to use Anthropic instead.
if (config["Ai:Provider"]?.Equals("claude", StringComparison.OrdinalIgnoreCase) == true)
    builder.Services.AddSingleton<ILlmClient, ClaudeClient>();
else
    builder.Services.AddSingleton<ILlmClient, GeminiClient>();
builder.Services.AddScoped<ReferralRelease>();
builder.Services.AddScoped<CareerService>();
builder.Services.AddScoped<RecommendationService>();
builder.Services.AddScoped<InterestChatService>();
builder.Services.AddScoped<GamificationService>();
builder.Services.AddScoped<ReminderAgent>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddHostedService<CommissionMaturityWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        AppException e => (e.Status, e.Message),
        BadHttpRequestException e => (StatusCodes.Status400BadRequest, "The request was not valid. " + e.Message),
        _ => (StatusCodes.Status500InternalServerError, "Something went wrong on our side."),
    };
    ctx.Response.StatusCode = status;
    await Results.Problem(title: title, statusCode: status).ExecuteAsync(ctx);
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var api = app.MapGroup("/api/v1");
api.MapGet("/health", () => new { status = "ok" }).WithTags("System");
api.MapIdentity();
api.MapPartners();
api.MapCatalog();
api.MapCourseInterest();
api.MapSyllabus();
api.MapLearning();
api.MapClassroom();
api.MapAssessments();
api.MapCertificates();
api.MapCommerce();
api.MapReferrals();
api.MapCommissions();
api.MapAnalytics();
api.MapAi();
api.MapSyllabusAssistant();
api.MapCareers();
api.MapInterests();
api.MapGamification();
api.MapPractice();
api.MapViva();
api.MapNotifications();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
    if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    if (config.GetValue("App:SeedDemoData", false)) await DemoSeed.RunAsync(db);
}

app.Run();

public partial class Program;
