using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;

namespace ViewOwl.Data
{
    /// <summary>
    /// EF Core database context for ViewOwl.
    /// Manages Users, Devices, Templates, DevicePings, GrabLogs, Connections, and DeviceCommands via SQLite.
    /// </summary>
    public sealed class ViewOwlDbContext : DbContext
    {
        /// <summary>
        /// Initialises the context with the supplied options.
        /// </summary>
        /// <param name="options">EF Core configuration (connection string, provider).</param>
        public ViewOwlDbContext(DbContextOptions<ViewOwlDbContext> options)
            : base(options)
        {
        }

        /// <summary>Application users.</summary>
        public DbSet<User> Users => Set<User>();

        /// <summary>Registered ESP32 display devices.</summary>
        public DbSet<Device> Devices => Set<Device>();

        /// <summary>HTML display templates.</summary>
        public DbSet<Template> Templates => Set<Template>();

        /// <summary>Device ping / connectivity history.</summary>
        public DbSet<DevicePing> DevicePings => Set<DevicePing>();

        /// <summary>On-demand screenshot grab log entries.</summary>
        public DbSet<GrabLog> GrabLogs => Set<GrabLog>();

        /// <summary>Template-to-Device connections (which template streams to which device).</summary>
        public DbSet<Connection> Connections => Set<Connection>();

        /// <summary>Queued commands waiting to be delivered to devices over UDP.</summary>
        public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();

        /// <summary>Per-user React Flow canvas node positions.</summary>
        public DbSet<FlowLayout> FlowLayouts => Set<FlowLayout>();

        /// <summary>End-to-end pipeline state-transition event log (grab → UDP → device).</summary>
        public DbSet<PipelineEvent> PipelineEvents => Set<PipelineEvent>();

        /// <summary>Template display categories (e.g. Weather, Clock, Menu).</summary>
        public DbSet<TemplateCategory> TemplateCategories => Set<TemplateCategory>();

        /// <summary>Guest devices flashed from the landing page (no user account).</summary>
        public DbSet<GuestDevice> GuestDevices => Set<GuestDevice>();

        /// <inheritdoc/>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            // ── User ──────────────────────────────────────────────────────────
            modelBuilder.Entity<User>(e =>
            {
                e.HasKey(u => u.Id);
                e.HasIndex(u => u.Login).IsUnique();
                e.Property(u => u.Login).HasMaxLength(64).IsRequired();
                e.Property(u => u.PasswordHash).IsRequired();
                e.Property(u => u.Role).HasConversion<string>(); // stored as "Admin"/"User"
            });

            // ── Device ────────────────────────────────────────────────────────
            modelBuilder.Entity<Device>(e =>
            {
                e.HasKey(d => d.Id);
                e.HasIndex(d => d.Token).IsUnique();
                e.HasIndex(d => new { d.UserId, d.Name }).IsUnique(); // name unique per user
                e.Property(d => d.Token).HasMaxLength(128).IsRequired();
                e.Property(d => d.Name).HasMaxLength(256).IsRequired();

                e.HasOne(d => d.User)
                 .WithMany(u => u.Devices)
                 .HasForeignKey(d => d.UserId)
                 .OnDelete(DeleteBehavior.Cascade);

                // ActiveTemplate is optional; do not cascade-delete templates when a device is removed
                e.HasOne(d => d.ActiveTemplate)
                 .WithMany()
                 .HasForeignKey(d => d.ActiveTemplateId)
                 .OnDelete(DeleteBehavior.SetNull);

                // Many-to-many: Device ↔ Template via DeviceTemplate join table
                e.HasMany(d => d.Templates)
                 .WithMany(t => t.Devices)
                 .UsingEntity(
                     "DeviceTemplate",
                     l => l.HasOne(typeof(Template)).WithMany().HasForeignKey("TemplateId").OnDelete(DeleteBehavior.Cascade),
                     r => r.HasOne(typeof(Device)).WithMany().HasForeignKey("DeviceId").OnDelete(DeleteBehavior.Cascade));

                // Monitoring / hardware metadata columns
                e.Property(d => d.DisplayType).HasMaxLength(64);
                e.Property(d => d.FirmwareVersion).HasMaxLength(32);
                e.Property(d => d.IpAddress).HasMaxLength(45); // max IPv6 length
                e.Property(d => d.Status).HasConversion<string>(); // stored as "Online"/"Offline"
            });

            // ── Template ──────────────────────────────────────────────────────
            modelBuilder.Entity<Template>(e =>
            {
                e.HasKey(t => t.Id);
                e.HasIndex(t => new { t.UserId, t.Name }).IsUnique(); // name unique per user
                e.Property(t => t.Name).HasMaxLength(256).IsRequired();
                // Landing-page visibility: DB default true preserves existing templates on upgrade.
                e.Property(t => t.IsPublic).HasDefaultValue(true);

                e.HasOne(t => t.User)
                 .WithMany(u => u.Templates)
                 .HasForeignKey(t => t.UserId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── DevicePing ────────────────────────────────────────────────────
            modelBuilder.Entity<DevicePing>(e =>
            {
                e.HasKey(p => p.Id);
                // Index on DeviceId+Timestamp speeds up stats queries
                e.HasIndex(p => new { p.DeviceId, p.Timestamp });

                e.HasOne(p => p.Device)
                 .WithMany(d => d.Pings)
                 .HasForeignKey(p => p.DeviceId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── GrabLog ───────────────────────────────────────────────────────
            modelBuilder.Entity<GrabLog>(e =>
            {
                e.HasKey(g => g.Id);
                // Composite index for GetRecentAsync / GetLastSuccessfulAsync queries
                e.HasIndex(g => new { g.TemplateId, g.RequestedAt });

                e.HasOne(g => g.Template)
                 .WithMany(t => t.GrabLogs)
                 .HasForeignKey(g => g.TemplateId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.Property(g => g.Message).HasMaxLength(1024);
            });

            // ── Connection ────────────────────────────────────────────────────
            modelBuilder.Entity<Connection>(e =>
            {
                e.HasKey(c => c.Id);

                e.HasOne(c => c.User)
                 .WithMany()
                 .HasForeignKey(c => c.UserId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(c => c.Template)
                 .WithMany()
                 .HasForeignKey(c => c.TemplateId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(c => c.Device)
                 .WithMany()
                 .HasForeignKey(c => c.DeviceId)
                 .OnDelete(DeleteBehavior.Cascade);

                // Quick lookup: which template is connected to which device (unique pair)
                e.HasIndex(c => new { c.DeviceId, c.TemplateId }).IsUnique();
            });

            // ── DeviceCommand ─────────────────────────────────────────────────
            modelBuilder.Entity<DeviceCommand>(e =>
            {
                e.HasKey(dc => dc.Id);

                e.Property(dc => dc.CommandType).HasMaxLength(64).IsRequired();
                e.Property(dc => dc.Status).HasMaxLength(32).IsRequired();

                e.HasOne(dc => dc.Device)
                 .WithMany()
                 .HasForeignKey(dc => dc.DeviceId)
                 .OnDelete(DeleteBehavior.Cascade);

                // Index for polling pending commands per device
                e.HasIndex(dc => new { dc.DeviceId, dc.Status });
            });

            // ── FlowLayout ────────────────────────────────────────────────────
            modelBuilder.Entity<FlowLayout>(e =>
            {
                e.HasKey(fl => fl.Id);
                // One layout row per user
                e.HasIndex(fl => fl.UserId).IsUnique();

                e.Property(fl => fl.PositionsJson).IsRequired();

                e.HasOne(fl => fl.User)
                 .WithMany()
                 .HasForeignKey(fl => fl.UserId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── TemplateCategory ──────────────────────────────────────────────
            modelBuilder.Entity<TemplateCategory>(e =>
            {
                e.HasKey(c => c.Id);
                e.HasIndex(c => c.Slug).IsUnique(); // slug must be globally unique
                e.Property(c => c.Label).HasMaxLength(64).IsRequired();
                e.Property(c => c.Slug).HasMaxLength(64).IsRequired();
            });

            // ── PipelineEvent ─────────────────────────────────────────────────
            modelBuilder.Entity<PipelineEvent>(e =>
            {
                e.HasKey(pe => pe.Id);
                e.Property(pe => pe.EventType).HasMaxLength(64).IsRequired();
                e.Property(pe => pe.SessionId).HasMaxLength(32);
                e.Property(pe => pe.ErrorMessage).HasMaxLength(2048);

                // Primary query pattern: recent events for a specific device or template
                e.HasIndex(pe => new { pe.DeviceId,   pe.Ts });
                e.HasIndex(pe => new { pe.TemplateId, pe.Ts });
                // Secondary: all events of a given type, newest first
                e.HasIndex(pe => new { pe.EventType,  pe.Ts });

                // Device FK — nullable (grab-only events have no device)
                e.HasOne(pe => pe.Device)
                 .WithMany()
                 .HasForeignKey(pe => pe.DeviceId)
                 .OnDelete(DeleteBehavior.SetNull);

                // Template FK — nullable (device-only events have no template)
                e.HasOne(pe => pe.Template)
                 .WithMany()
                 .HasForeignKey(pe => pe.TemplateId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── GuestDevice ───────────────────────────────────────────────────
            modelBuilder.Entity<GuestDevice>(e =>
            {
                e.HasKey(g => g.Id);
                e.HasIndex(g => g.Guid).IsUnique();
                e.Property(g => g.TemplateName).HasMaxLength(256).IsRequired();
                e.Property(g => g.CreatedAt).IsRequired();
            });
        }
    }
}
