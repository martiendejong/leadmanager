using Hangfire;
using Hangfire.MemoryStorage;
using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Profile;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

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
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
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

builder.Services.AddAuthorization();

// JWT service
builder.Services.AddScoped<JwtService>();

// Search service
builder.Services.AddScoped<SearchService>();

// Profile + Smart Search services
builder.Services.AddScoped<CompanyProfileService>();
builder.Services.AddScoped<GptLeadGeneratorService>();
builder.Services.AddScoped<SmartSearchService>();

// Enrichment services
builder.Services.AddScoped<KvkEnrichmentService>();
builder.Services.AddScoped<GooglePlacesEnrichmentService>();
builder.Services.AddScoped<SalesScoreService>();
builder.Services.AddScoped<DocumentParserService>();
builder.Services.AddScoped<TextInputEnrichmentService>();
builder.Services.AddScoped<AiSalesApproachService>();
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

    // Seed admin user
    const string adminEmail = "info@prospergenics.com";
    const string adminPassword = "SpaceElevator1tam!";
    var adminUser = await userManager.FindByEmailAsync(adminEmail);
    if (adminUser == null)
    {
        adminUser = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            FirstName = "Admin",
            LastName = "User",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            EmailConfirmed = true
        };
        var result = await userManager.CreateAsync(adminUser, adminPassword);
        if (result.Succeeded)
            await userManager.AddToRoleAsync(adminUser, "Admin");
    }
    else
    {
        // Ensure password is correct and user has Admin role
        var token = await userManager.GeneratePasswordResetTokenAsync(adminUser);
        await userManager.ResetPasswordAsync(adminUser, token, adminPassword);
        if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
            await userManager.AddToRoleAsync(adminUser, "Admin");
    }
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

app.UseHangfireDashboard("/hangfire"); // no auth — dev mode

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
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }))
   .WithName("Health");

app.Run();
