using ANGI.Application.Common.Interfaces.Repositories;

namespace ANGI.Infrastructure.Persistences.Repositories
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ANGIContext _context;

        public UnitOfWork(ANGIContext context)
        {
            _context = context;
        }

        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            return _context.SaveChangesAsync(ct);
        }
    }
}
