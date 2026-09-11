using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PromoEngine.Api.Endpoints;
using PromoEngine.Api.Middleware;
using PromoEngine.Api.Services;
using PromoEngine.Infrastructure.Catalog;
using PromoEngine.Infrastructure.Provisioning;
using PromoEngine.Infrastructure.Security;
using PromoEngine.Infrastructure.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<TenancyOptions>(builder.Configuration.GetSection(TenancyOptions.SectionName));
builder.Services.Configure<LocalProvisioningOptions>(builder.Configuration.GetSection(LocalProvisioningOptions.SectionName));
builder.Services.Configure<AwsProvisioningOptions>(builder.Configuration.GetSection(AwsProvisioningOptions.SectionName));
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.Configure<ItemUploadOptions>(builder.Configuration.GetSection(ItemUploadOptions.SectionName));
builder.Services.Configure<ExternalPromotionApiOptions>(builder.Configuration.GetSection(ExternalPromotionApiOptions.SectionName));

// ---------------------------------------------------------------------------
// Control plane database
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Catalog")
        ?? "Host=localhost;Port=5432;Database=PromoEngineCatalog;Username=postgres;Password=postgres",
        npgsql => npgsql.EnableRetryOnFailure()));

// ---------------------------------------------------------------------------
// Tenancy and provisioning
// ---------------------------------------------------------------------------
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
builder.Services.AddScoped<TenantSchemaInitializer>();

// The provisioner is chosen by configuration. Local creates databases on a local
// PostgreSQL server; Aws creates them on RDS for PostgreSQL. Both produce the same
// schema.
var provisionerName = builder.Configuration.GetValue<string>("Tenancy:Provisioner") ?? "Local";
if (provisionerName.Equals("Aws", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<ITenantProvisioner, AwsRdsTenantProvisioner>();
}
else
{
    builder.Services.AddScoped<ITenantProvisioner, LocalPostgresTenantProvisioner>();
}

builder.Services.AddSingleton<TenantProvisioningQueue>();
builder.Services.AddSingleton<ITenantProvisioningQueue>(sp => sp.GetRequiredService<TenantProvisioningQueue>());
builder.Services.AddHostedService<TenantProvisioningWorker>();
// Brings tenant databases created before a model change in line with it on start
// up. The tenant schema is EnsureCreated rather than migrated, so without this a
// newly added table - OfferProducts, say - never appears on an existing tenant.
builder.Services.AddHostedService<TenantSchemaSyncService>();

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<OfferValidator>();
builder.Services.AddSingleton<OfferProductImporter>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<TenantOnboardingService>();
builder.Services.AddScoped<TenantAccessService>();
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<ApplicablePromotionService>();
builder.Services.AddScoped<TenantSchemaReconciler>();
// ---------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Web
// ---------------------------------------------------------------------------
const string CorsPolicy = "PromoEngineClient";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:4200"];

builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "PromoEngine API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "PromoEngine API";
    });
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapSubscriptionEndpoints();
app.MapPromotionEndpoints();
app.MapExternalPromotionEndpoints();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    provisioner = provisionerName,
    utc = DateTimeOffset.UtcNow
}))
.WithTags("Diagnostics")
.AllowAnonymous();

// Create the catalog database and seed the subscription plans on start up.
await CatalogSeeder.SeedAsync(app.Services);

app.Run();
