using System.IdentityModel.Tokens.Jwt;
using System.Text;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.WebApi.Configs
{
    public static class JwtConfig
    {
        private const string UnauthorizedCode = "UNAUTHORIZED";
        private const string UnauthorizedMessage = "Bạn cần đăng nhập để thực hiện thao tác này.";
        private const string ForbiddenCode = "FORBIDDEN";
        private const string ForbiddenMessage = "Bạn không có quyền thực hiện thao tác này.";

        public static IServiceCollection AddJwtConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var jwtSection = configuration.GetSection(JwtSettings.SectionName);
            var jwtSettings = jwtSection.Get<JwtSettings>() ?? new JwtSettings();

            services.AddOptions<JwtSettings>()
                .Bind(jwtSection)
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.Issuer), "Jwt:Issuer is required.")
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.Audience), "Jwt:Audience is required.")
                .Validate(settings => Encoding.UTF8.GetByteCount(settings.SecretKey) >= 32, "Jwt:SecretKey must be at least 32 bytes.")
                .Validate(settings => settings.AccessTokenMinutes > 0, "Jwt:AccessTokenMinutes must be greater than zero.")
                .Validate(settings => settings.RefreshTokenDays > 0, "Jwt:RefreshTokenDays must be greater than zero.")
                .ValidateOnStart();

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwtSettings.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwtSettings.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero,
                        NameClaimType = JwtRegisteredClaimNames.Sub,
                        RoleClaimType = "role"
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnChallenge = async context =>
                        {
                            context.HandleResponse();

                            if (!context.Response.HasStarted)
                            {
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                                {
                                    Success = false,
                                    Message = UnauthorizedMessage,
                                    ErrorCode = UnauthorizedCode,
                                    Data = null
                                });
                            }
                        },
                        OnForbidden = async context =>
                        {
                            if (!context.Response.HasStarted)
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                                {
                                    Success = false,
                                    Message = ForbiddenMessage,
                                    ErrorCode = ForbiddenCode,
                                    Data = null
                                });
                            }
                        }
                    };
                });

            services.AddAuthorization();

            return services;
        }
    }
}
