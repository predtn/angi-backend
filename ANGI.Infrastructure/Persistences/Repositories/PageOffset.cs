namespace ANGI.Infrastructure.Persistences.Repositories;

/// <summary>Row offset of a 1-based page, safe for any page the validators accept.</summary>
internal static class PageOffset
{
    /// <summary>
    /// Computes (page - 1) * pageSize in 64 bits and caps it at int.MaxValue: a huge page would overflow int into a
    /// negative OFFSET, which PostgreSQL rejects (500); the capped offset simply returns an empty page.
    /// </summary>
    public static int Of(int page, int pageSize) => (int)Math.Min(((long)page - 1) * pageSize, int.MaxValue);
}
