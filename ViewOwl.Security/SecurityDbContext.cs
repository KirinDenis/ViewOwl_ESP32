using Microsoft.EntityFrameworkCore;
using ViewOwl.Security.Models;

namespace ViewOwl.Security
{
    /// <summary>
    /// EF Core database context for security-related tables (events, blocked IPs, session fingerprints).
    /// Uses a separate SQLite file from the main application database so that security data
    /// can be managed and rotated independently.
    /// </summary>
    public sealed class SecurityDbContext : DbContext
    {
        /// <summary>
        /// Initialises the context with the provided options (injected by DI).
        /// </summary>
        /// <param name="options">EF Core context options, including the SQLite connection string.</param>
        public SecurityDbContext(DbContextOptions<SecurityDbContext> options)
            : base(options)
        {
        }

        /// <summary>All recorded security events.</summary>
        public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

        /// <summary>Currently and historically blocked IP addresses.</summary>
        public DbSet<BlockedIp> BlockedIps => Set<BlockedIp>();

        /// <summary>Per-session network fingerprints used to detect session hijacking.</summary>
        public DbSet<SessionFingerprint> SessionFingerprints => Set<SessionFingerprint>();

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            base.OnModelCreating(modelBuilder);

            // ── SecurityEvent ─────────────────────────────────────────────────

            modelBuilder.Entity<SecurityEvent>(entity =>
            {
                entity.HasKey(e => e.Id);

                // Composite index for the most common query: events from a given IP in a time window.
                entity.HasIndex(e => new { e.IpAddress, e.CreatedAt })
                      .HasDatabaseName("IX_SecurityEvents_IpAddress_CreatedAt");

                // Index for filtering by event category (e.g. all BruteForce events).
                entity.HasIndex(e => e.EventType)
                      .HasDatabaseName("IX_SecurityEvents_EventType");

                entity.Property(e => e.IpAddress).HasMaxLength(45);    // max IPv6 length
                entity.Property(e => e.Endpoint).HasMaxLength(2048);
                entity.Property(e => e.Method).HasMaxLength(10);
                entity.Property(e => e.EventType).HasMaxLength(64);
                entity.Property(e => e.RequestBody).HasMaxLength(500);
                entity.Property(e => e.UserId).HasMaxLength(64);
                entity.Property(e => e.SessionId).HasMaxLength(128);
                entity.Property(e => e.Details).HasMaxLength(1024);
            });

            // ── BlockedIp ────────────────────────────────────────────────────

            modelBuilder.Entity<BlockedIp>(entity =>
            {
                entity.HasKey(e => e.Id);

                // Direct lookup by IP is the hot path in the request pipeline.
                entity.HasIndex(e => e.IpAddress)
                      .HasDatabaseName("IX_BlockedIps_IpAddress");

                entity.Property(e => e.IpAddress).HasMaxLength(45);
                entity.Property(e => e.Reason).HasMaxLength(256);
            });

            // ── SessionFingerprint ────────────────────────────────────────────

            modelBuilder.Entity<SessionFingerprint>(entity =>
            {
                entity.HasKey(e => e.Id);

                // Lookup by userId + sessionId is the validation hot path.
                entity.HasIndex(e => new { e.UserId, e.SessionId })
                      .HasDatabaseName("IX_SessionFingerprints_UserId_SessionId");

                entity.Property(e => e.UserId).HasMaxLength(64);
                entity.Property(e => e.SessionId).HasMaxLength(128);
                entity.Property(e => e.IpAddress).HasMaxLength(45);
                entity.Property(e => e.UserAgentHash).HasMaxLength(64);
            });
        }
    }
}
