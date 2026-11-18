using Hangfire;
using Hangfire.MemoryStorage;
using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Profile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                  "http://localhost:5173",
                  "http://localhost:5174",
                  "https://leads.prospergenics.com",
                  "http://leads.prospergenics.com")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// EF Core + SQLite
builder.Services.AddDbContext<LeadManagerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<LeadManagerDbContext>()
.AddDefaultTokenProviders();

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(options =>
{
    // "Smart" forwards to API key when the X-Api-Key header is present, otherwise to JWT.
    // [Authorize] therefore accepts both, while an explicit [Authorize(AuthenticationSchemes=
    // Bearer)] (ApiKeysController) stays JWT only. Do NOT widen DefaultPolicy instead: ASP.NET
    // merges the default policy into attribute policies, which would let API keys through there.
    options.DefaultAuthenticateScheme = ApiKeyDefaults.SmartScheme;
    options.DefaultChallengeScheme = ApiKeyDefaults.SmartScheme;
})
.AddPolicyScheme(ApiKeyDefaults.SmartScheme, "JWT or API key", options =>
{
    // A Bearer token always wins, so a valid login is never broken by a stale X-Api-Key header.
    options.ForwardDefaultSelector = context =>
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var hasBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var hasKey = !string.IsNullOrWhiteSpace(context.Request.Headers[ApiKeyDefaults.HeaderName].ToString());
        return !hasBearer && hasKey ? ApiKeyDefaults.Scheme : JwtBearerDefaults.AuthenticationScheme;
    };
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
    // Support SignalR token from query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// API keys for agents and other Jengo tools (X-Api-Key). The key acts as the user that
// created it, so per-user ownership rules apply unchanged. See the "Smart" scheme above.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddScoped<CrmParentAccess>();

// MCP server (/mcp): agents and other Jengo tools. The access token is one of the user's API keys.
builder.Services.AddSingleton(new JengoComponents.McpConnector.McpConnectorOptions
{
    ServerName = "jengo-clients",
    ServerVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
    Realm = "JengoClients",
});
builder.Services.AddScoped<JengoComponents.McpConnector.IMcpTokenValidator, LeadManager.Api.Mcp.ApiKeyTokenValidator>();

// JWT service
builder.Services.AddScoped<JwtService>();

// Sales platform: intake + estimation + minox-poc connector + script + offerte
builder.Services.AddHttpClient<EstimationService>();
builder.Services.AddHttpClient<MinoxPocConnectorService>();
builder.Services.AddHttpClient<ScriptGeneratorService>();
builder.Services.AddScoped<EstimationService>();
builder.Services.AddScoped<MinoxPocConnectorService>();
builder.Services.AddScoped<ScriptGeneratorService>();
builder.Services.AddScoped<OfferteService>();

// All HTTP-calling services are typed clients on IHttpClientFactory so they
// share pooled handlers. Constructing HttpClients per service instance (and
// per lead in the enrichment loop) leaked sockets until the process hit the
// commit limit — the 42.5 GB incident of 2026-08-27 (#844).
Func<HttpMessageHandler> insecureCrawlerHandler = () => new HttpClientHandler
{
    AllowAutoRedirect = true,
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
// Crawler responses are read fully into memory (GetStringAsync / ReadAsStringAsync),
// so an uncapped response — a 50 MB webshop sitemap, a huge page — balloons the
// process. 5 MB is far above any legitimate page/sitemap we strip to 10k chars.
const long CrawlerMaxResponseBytes = 5 * 1024 * 1024;
Action<HttpClient> crawlerDefaults = c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.MaxResponseContentBufferSize = CrawlerMaxResponseBytes;
    c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");
};

// Search service
builder.Services.AddHttpClient<SearchService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.MaxResponseContentBufferSize = CrawlerMaxResponseBytes;
    c.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
    c.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
}).ConfigurePrimaryHttpMessageHandler(insecureCrawlerHandler);

// Profile + Smart Search services
builder.Services.AddHttpClient<CompanyProfileService>();
builder.Services.AddHttpClient<GptLeadGeneratorService>();
builder.Services.AddScoped<SmartSearchService>();
// Named client for CompanyProfileService's WordPress REST probe
builder.Services.AddHttpClient("wp-probe", c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.MaxResponseContentBufferSize = CrawlerMaxResponseBytes;
    c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");
});

// Enrichment services
builder.Services.AddHttpClient<UrlNormalizerService>(crawlerDefaults)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Services.AddHttpClient<SitemapService>(crawlerDefaults).ConfigurePrimaryHttpMessageHandler(insecureCrawlerHandler);
builder.Services.AddHttpClient<PageFetcherService>(crawlerDefaults).ConfigurePrimaryHttpMessageHandler(insecureCrawlerHandler);
builder.Services.AddHttpClient<ScraperService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.MaxResponseContentBufferSize = CrawlerMaxResponseBytes;
    c.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(insecureCrawlerHandler);
builder.Services.AddHttpClient<EmbeddingService>();
builder.Services.AddHttpClient<RagEnrichmentService>();
builder.Services.AddHttpClient<NameExtractorService>();
builder.Services.AddHttpClient<OutreachEmailService>();
builder.Services.AddHttpClient<KvkEnrichmentService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<GooglePlacesEnrichmentService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<TextInputEnrichmentService>();
builder.Services.AddHttpClient<AiSalesApproachService>();
builder.Services.AddScoped<SalesScoreService>();
builder.Services.AddScoped<DocumentParserService>();
// Singleton so its internal MemoryCache and 30/min rate limiter are shared
// across all leads instead of being recreated (and reset) per lead.
builder.Services.AddSingleton<WebSearchEnrichmentService>();
builder.Services.AddSingleton<EnrichmentChannel>();
builder.Services.AddHostedService<EnrichmentBackgroundService>();

// Hangfire
builder.Services.AddHangfire(config => config.UseMemoryStorage());
builder.Services.AddHangfireServer(opts => { opts.WorkerCount = 10; });
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<StaleLeadJobService>();
builder.Services.AddScoped<HangfireEnrichmentJob>();

// Controllers
builder.Services.AddControllers();

// SignalR (built-in to ASP.NET Core 8)
builder.Services.AddSignalR();

// Swagger/OpenAPI — dev only
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LeadManager API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: 'Bearer {token}'",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// EPPlus license (non-commercial)
OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;

var app = builder.Build();

// Run migrations and seed admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LeadManagerDbContext>();
    db.Database.Migrate();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    // Ensure roles exist (also seeded via migrations, but guard here too)
    foreach (var role in new[] { "Admin", "User" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    // Admin accounts come from configuration only (Seed:Admins). No password lives in the code.
    await AdminSeeder.SeedAsync(userManager, builder.Configuration,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AdminSeeder"));
}

// Dev middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "LeadManager API v1"));
}

if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// An API key only reaches endpoints marked [AllowApiKey] (the CRM). Anywhere else it gets 403,
// whatever the user behind the key is allowed to do. Anonymous endpoints (health) stay open.
app.Use(async (context, next) =>
{
    if (context.User.HasClaim("auth_method", "api_key"))
    {
        var metadata = context.GetEndpoint()?.Metadata;
        var requiresAuth = metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() != null;
        var allowed = metadata?.GetMetadata<AllowApiKeyAttribute>() != null
                      || metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null
                      || !requiresAuth; // open endpoints (health, static files) stay open
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("API keys can only be used on the CRM endpoints.");
            return;
        }
    }
    await next();
});

// IIS only proxies /api/* to this process, but the dashboard is still reachable
// on the server itself; restrict it to local requests explicitly.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter() }
});

RecurringJob.AddOrUpdate<HangfireEnrichmentJob>(
    "enrichment-sweep",
    j => j.RunAsync(),
    "*/5 * * * *");

RecurringJob.AddOrUpdate<StaleLeadJobService>(
    "daily-notifications",
    j => j.RunDailyNotificationsAsync(),
    Cron.Daily());

app.MapControllers();
app.MapHub<EnrichmentHub>("/hubs/enrichment");

// Health endpoint
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = appVersion, timestamp = DateTime.UtcNow }))
   .WithName("Health");

app.Run();
