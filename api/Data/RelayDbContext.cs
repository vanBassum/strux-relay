using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StruxRelay.Data;

/// <summary>
/// What survives a restart: which device ids are approved and what token each one
/// proved itself with, the refused attempts waiting for a decision, and a log of
/// both.
///
/// Deliberately provider-neutral — no raw SQL and no provider-specific column
/// defaults. Timestamps are <see cref="DateTime"/> in UTC, and that is a
/// constraint rather than a preference: SQLite refuses to ORDER BY a
/// DateTimeOffset outright ("does not support expressions of type
/// 'DateTimeOffset' in ORDER BY clauses"), because it stores one as text with an
/// offset suffix that does not sort chronologically. PostgreSQL orders one
/// perfectly well, so this is exactly the kind of difference that only shows up
/// on the provider you did not develop against. A UTC DateTime is stored as
/// sortable ISO text on SQLite and as a timestamp on PostgreSQL, and orders
/// correctly on both.
/// </summary>
internal sealed class RelayDbContext(DbContextOptions<RelayDbContext> options)
    : DbContext(options)
{
    public DbSet<ApprovedDevice> Approved => Set<ApprovedDevice>();

    public DbSet<PendingDevice> Pending => Set<PendingDevice>();

    public DbSet<RelayEvent> Events => Set<RelayEvent>();

    /// <summary>
    /// Everything stored is UTC, but a database does not necessarily say so on the
    /// way back: SQLite returns a DateTime with Kind Unspecified, which
    /// System.Text.Json then writes without a trailing Z — and a browser reads
    /// that as LOCAL time. An hour or two of silent skew in every timestamp on the
    /// page, from a value that was correct in the database. Stamping the Kind on
    /// read is what closes it, in one place rather than at each edge.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ApprovedDevice>(device => device.HasKey(entity => entity.DeviceId));

        // The composite key is the point of this table; see PendingDevice.
        builder.Entity<PendingDevice>(device =>
            device.HasKey(entity => new { entity.DeviceId, entity.Token }));

        builder.Entity<RelayEvent>(entry =>
        {
            entry.HasKey(entity => entity.Id);
            // The dashboard only ever reads the newest handful.
            entry.HasIndex(entity => entity.At).IsDescending();
        });
    }
}

/// <summary>
/// Its own type rather than an inline converter, because ConfigureConventions
/// takes a converter TYPE and instantiates it.
/// </summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            stored => stored.ToUniversalTime(),
            read => DateTime.SpecifyKind(read, DateTimeKind.Utc))
    {
    }
}
