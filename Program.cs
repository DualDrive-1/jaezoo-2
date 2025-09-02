using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Services;
using JaeZoo.Server.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.IO.Compression;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ---------- DB ----------
var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=jaezoo.db";
var isPg = conn.Contains("Host=", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

if (isPg) builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));
else builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));

// ---------- JWT ----------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt");
var key = jwt.GetValue<string>("Key") ?? "fallback_key_change_me";
var issuer = jwt.GetValue<string>("Issuer") ?? "JaeZoo";
var audience = jwt.GetValue<string>("Audience") ?? "JaeZooClient";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        };

        // Токен для SignalR из query (?access_token=...) на /hubs/chat
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        o.SuppressModelStateInvalidFilter = false;
    });

var redis = Environment.GetEnvironmentVariable("REDIS_URL");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSignalR().AddStackExchangeRedis(redis);
}
else
{
    builder.Services.AddSignalR();
}

// presence-трекер в памяти
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();

// ---------- Производительность ----------
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
// ✅ правильные типы опций провайдеров:
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddResponseCaching(options =>
{
    options.MaximumBodySize = 1024 * 1024;     // до 1 МБ
    options.SizeLimit = 100 * 1024 * 1024;     // общий лимит 100 МБ
});

// ---------- Rate limiting ----------
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "JaeZoo API", Version = "v1" });
});

var app = builder.Build();

// ---------- Миграция БД ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// ---------- wwwroot/avatars ----------
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "avatars"));

// ---------- Проксирование ----------
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// app.UseHttpsRedirection(); // На Render HTTPS терминируется до контейнера

// ---------- Пайплайн ----------
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "public,max-age=86400"; // 24h
    }
});

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseResponseCompression();
app.UseResponseCaching();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<LastSeenMiddleware>();

app.UseExceptionHandler("/error");
app.MapGet("/error", () => Results.Problem(
    title: "Unexpected error",
    statusCode: StatusCodes.Status500InternalServerError
));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
