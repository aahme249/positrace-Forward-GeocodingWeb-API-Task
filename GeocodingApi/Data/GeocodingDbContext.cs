using Microsoft.EntityFrameworkCore;

namespace GeocodingApi.Data;

public class GeocodingDbContext(DbContextOptions<GeocodingDbContext> options) : DbContext(options)
{
    public DbSet<CachedGeocode> CachedGeocodes => Set<CachedGeocode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedGeocode>(e =>
        {
            e.HasIndex(c => c.NormalizedAddress).IsUnique();
            e.Property(c => c.NormalizedAddress).HasMaxLength(500);
        });
    }
}
