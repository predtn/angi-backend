using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Audit
{
    /// <summary>Registers the audit log writer, used by several modules (Auth, Account, Moderation, Administration).</summary>
    public static class AuditModule
    {
        /// <summary>Maps IAuditLogService to the implementation that writes through ANGIContext.</summary>
        public static IServiceCollection AddAuditInfrastructure(this IServiceCollection services)
        {
            services.AddScoped<IAuditLogService, AuditLogService>();

            return services;
        }
    }
}
