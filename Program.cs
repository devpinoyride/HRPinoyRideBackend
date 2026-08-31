using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Middleware;
using PinoyRideHrApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Render provides the listening port through the PORT environment variable.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out var parsedPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

var config = builder.Configuration;
var allowedOrigin = config["ALLOWED_ORIGIN"];

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddCors(o =>
{
    o.AddPolicy("Frontend", p =>
    {
        if (string.IsNullOrWhiteSpace(allowedOrigin) || allowedOrigin == "*")
        {
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
        else
        {
            p.WithOrigins(allowedOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
             .AllowAnyHeader()
             .AllowAnyMethod()
             .AllowCredentials();
        }
    });
});

var signingKey = config["JWT_SIGNING_KEY"];
if (string.IsNullOrWhiteSpace(signingKey))
{
    throw new InvalidOperationException("JWT_SIGNING_KEY environment variable is not configured.");
}

var jwt = new JwtOptions { Key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)) };
builder.Services.AddSingleton(jwt);

builder.Services.AddSingleton(new SupabaseOptions
{
    Url = config["SUPABASE_URL"] ?? "",
    AnonKey = config["SUPABASE_ANON_KEY"] ?? "",
    ServiceRoleKey = config["SUPABASE_SERVICE_ROLE_KEY"] ?? "",
    DatabaseUrl = config["DATABASE_URL"] ?? ""
});

builder.Services.AddScoped<Db>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddHttpClient<SupabaseAuthClient>();
builder.Services.AddHttpClient<SupabaseAdminClient>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Keep original claim names (sub, role, full_name). The default inbound
        // mapping renames them to long XML claim types, so FindFirst("sub")
        // and the role policies would never match.
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwt.Key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "sub"
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("HrAdmin", p => p.RequireClaim("role", "hr_admin"));
    o.AddPolicy("ApproverOrAbove", p => p.RequireAssertion(ctx =>
        ctx.User?.FindFirst("role")?.Value is "hr_admin" or "approver"));
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Pinoy Ride HR API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });
}

// Map Postgres snake_case columns (full_name, work_date, approver_id, ...)
// onto PascalCase properties (FullName, WorkDate, ApproverId, ...) for all
// Dapper queries. Without this, those properties silently stay null.
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// Teach Dapper to bind/parse Postgres date and time values as
// DateOnly/TimeOnly (the types the models use).
DapperTypeHandlers.Register();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(s => s.SwaggerEndpoint("/swagger/v1/swagger.json", "Pinoy Ride HR API v1"));
}

app.MapControllers();
app.Run();

public partial class Program { }