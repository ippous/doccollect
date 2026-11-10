using DocCollect.Domain;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocCollect.Infrastructure.Persistence;

public class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
    /// <summary>Demandeur de rattachement (null pour les admins plateforme et les fournisseurs).</summary>
    public Guid? TenantId { get; set; }
    public TwoFactorMethod PreferredTwoFactorMethod { get; set; } = TwoFactorMethod.None;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<StorageProfile> StorageProfiles => Set<StorageProfile>();
    public DbSet<ChecklistTemplate> Templates => Set<ChecklistTemplate>();
    public DbSet<TemplateItem> TemplateItems => Set<TemplateItem>();
    public DbSet<Dossier> Dossiers => Set<Dossier>();
    public DbSet<DossierItem> DossierItems => Set<DossierItem>();
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<DeliveryChannel> DeliveryChannels => Set<DeliveryChannel>();
    public DbSet<DeliveryRecord> Deliveries => Set<DeliveryRecord>();
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema("doccollect");

        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(60);
            e.Property(x => x.AiProviderKey).HasMaxLength(100);
        });

        b.Entity<StorageProfile>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ProviderType).HasMaxLength(50);
        });

        b.Entity<ChecklistTemplate>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Visibility });
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Items).AutoInclude();
        });
        b.Entity<TemplateItem>(e => e.Property(x => x.Label).HasMaxLength(300));

        b.Entity<Dossier>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.ProviderUserId);
            e.HasIndex(x => x.ProviderEmail);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.ProviderEmail).HasMaxLength(320);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DossierId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsComplete);
            e.Ignore(x => x.AcceptsUploads);
        });

        b.Entity<DossierItem>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(300);
            e.HasMany(x => x.Documents).WithOne().HasForeignKey(d => d.DossierItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StoredDocument>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(500);
        });

        b.Entity<DeliveryChannel>(e => e.HasIndex(x => x.TenantId));
        b.Entity<DeliveryRecord>(e => e.HasIndex(x => new { x.Status, x.NextAttemptAt }));
        b.Entity<AiUsageRecord>(e => e.HasIndex(x => new { x.TenantId, x.At }));
        b.Entity<Invitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
        });
        b.Entity<AuditEvent>(e => e.HasIndex(x => new { x.TenantId, x.At }));

        b.Entity<AppUser>(e => e.HasIndex(x => x.TenantId));

        // Les identifiants sont générés côté domaine (Guid.NewGuid) : EF ne doit pas les considérer comme
        // « générés par la base », sinon un document ajouté à une collection suivie serait pris pour un existant.
        foreach (var type in new[] { typeof(Tenant), typeof(StorageProfile), typeof(ChecklistTemplate), typeof(TemplateItem),
                     typeof(Dossier), typeof(DossierItem), typeof(StoredDocument), typeof(DeliveryChannel), typeof(DeliveryRecord),
                     typeof(AiUsageRecord), typeof(Invitation) })
            b.Entity(type).Property(nameof(Tenant.Id)).ValueGeneratedNever();
    }
}

/// <summary>Chargement d'un dossier complet (pièces + documents).</summary>
public static class DossierQueries
{
    public static IQueryable<Dossier> WithDetails(this IQueryable<Dossier> q) =>
        q.Include(d => d.Items).ThenInclude(i => i.Documents);
}
