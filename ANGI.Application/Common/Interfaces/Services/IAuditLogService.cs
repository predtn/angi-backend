using ANGI.Application.Common.Models.Audit;

namespace ANGI.Application.Common.Interfaces.Services
{
    /// <summary>Writes audit_logs rows (coding_rule.md §17).</summary>
    public interface IAuditLogService
    {
        /// <summary>
        /// Adds the row to the Unit of Work; the use case's <c>SaveChangesAsync</c> saves it together
        /// with the change it describes. Actor, IP address and User-Agent come from the current request.
        /// </summary>
        void Add(AuditLogEntry entry);
    }
}
