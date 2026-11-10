using DocCollect.Domain;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocCollect.Api.Infrastructure;

public sealed class SeedOptions
{
    public const string Section = "Seed";
    /// <summary>Premier administrateur (créé seulement s'il n'existe aucun admin). Il devra activer la 2FA à la première connexion.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    /// <summary>Dev/Test uniquement : demandeur de démonstration.</summary>
    public string? DemoRequesterEmail { get; set; }
    public string? DemoRequesterPassword { get; set; }
    public bool PublicTemplates { get; set; } = true;
}

public static class DataSeeder
{
    public static async Task InitializeAsync(IServiceProvider services, SeedOptions seed, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (db.Database.IsRelational() && db.Database.GetMigrations().Any())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync(); // avant la première migration (voir docs/dev/getting-started.md)

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (!string.IsNullOrWhiteSpace(seed.AdminEmail) && !string.IsNullOrWhiteSpace(seed.AdminPassword)
            && (await users.GetUsersInRoleAsync(Roles.Admin)).Count == 0)
        {
            await CreateUserAsync(users, seed.AdminEmail, seed.AdminPassword, Roles.Admin, null, logger);
        }

        if (!string.IsNullOrWhiteSpace(seed.DemoRequesterEmail) && !string.IsNullOrWhiteSpace(seed.DemoRequesterPassword)
            && await users.FindByEmailAsync(seed.DemoRequesterEmail) is null)
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == "demo")
                         ?? db.Tenants.Add(new Tenant { Name = "Étude de démonstration", Slug = "demo", AllowedEmbedOrigins = ["https://localhost:5002"] }).Entity;
            await db.SaveChangesAsync();
            await CreateUserAsync(users, seed.DemoRequesterEmail, seed.DemoRequesterPassword, Roles.Requester, tenant.Id, logger);
        }

        if (seed.PublicTemplates && !await db.Templates.AnyAsync(t => t.Visibility == TemplateVisibility.Public))
        {
            db.Templates.AddRange(PublicTemplates());
            await db.SaveChangesAsync();
        }
    }

    private static async Task CreateUserAsync(UserManager<AppUser> users, string email, string password, string role, Guid? tenantId, ILogger logger)
    {
        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, TenantId = tenantId };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogError("Création de {Email} impossible : {Errors}", email, string.Join(" ", result.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(user, role);
        logger.LogInformation("Utilisateur initial {Email} ({Role}) créé : la 2FA sera demandée à la première connexion.", email, role);
    }

    private static TemplateItem Item(string label, string? expected = null, bool required = true, string[]? types = null, int maxFiles = 1, string? description = null) => new()
    {
        Label = label, ExpectedDocumentType = expected, Required = required, MaxFiles = maxFiles, Description = description,
        AcceptedContentTypes = [.. types ?? ["application/pdf", "image/*"]], MaxSizeMb = 20
    };

    private static ChecklistTemplate Public(string name, string description, DeliveryMode mode, params TemplateItem[] items)
    {
        var t = new ChecklistTemplate { Name = name, Description = description, Visibility = TemplateVisibility.Public, DefaultDeliveryMode = mode };
        for (var i = 0; i < items.Length; i++) { items[i].Order = i; items[i].TemplateId = t.Id; }
        t.Items = [.. items];
        return t;
    }

    public static IEnumerable<ChecklistTemplate> PublicTemplates() =>
    [
        Public("Vente immobilière — vendeur", "Pièces habituellement demandées par le notaire au vendeur.", DeliveryMode.Incremental,
            Item("Pièce d'identité de chaque vendeur", "carte nationale d'identité ou passeport en cours de validité", maxFiles: 4),
            Item("Livret de famille ou contrat de mariage / PACS", "livret de famille, contrat de mariage ou convention de PACS", required: false, maxFiles: 3),
            Item("Titre de propriété", "acte notarié de propriété (titre de propriété)"),
            Item("Dernier avis de taxe foncière", "avis d'imposition de taxe foncière"),
            Item("Dossier de diagnostic technique", "rapport de diagnostic immobilier (DPE, amiante, plomb, électricité, gaz…)", maxFiles: 10),
            Item("Coordonnées du syndic et derniers PV d'AG (copropriété)", required: false, maxFiles: 5),
            Item("RIB", "relevé d'identité bancaire")),
        Public("Succession", "Premières pièces pour l'ouverture d'un dossier de succession.", DeliveryMode.AllAtOnce,
            Item("Acte de décès", "acte de décès délivré par la mairie"),
            Item("Livret de famille du défunt", "livret de famille"),
            Item("Pièce d'identité de chaque héritier", "carte nationale d'identité ou passeport", maxFiles: 10),
            Item("Contrat de mariage ou PACS du défunt", required: false),
            Item("Derniers avis d'imposition du défunt", "avis d'impôt sur le revenu", maxFiles: 2),
            Item("Relevés de comptes au jour du décès", "relevé de compte bancaire", required: false, maxFiles: 10)),
        Public("Dossier de location — locataire", "Pièces justificatives d'un candidat locataire (liste conforme au décret de 2015).", DeliveryMode.AllAtOnce,
            Item("Pièce d'identité", "carte nationale d'identité, passeport ou titre de séjour"),
            Item("Justificatif de domicile actuel", "quittance de loyer, facture d'énergie ou attestation d'hébergement"),
            Item("Trois derniers bulletins de salaire", "bulletin de salaire", maxFiles: 3),
            Item("Dernier avis d'imposition", "avis d'impôt sur le revenu"),
            Item("Contrat de travail ou attestation employeur", "contrat de travail ou attestation d'employeur", required: false)),
        Public("Bilan annuel — client d'un cabinet comptable", "Pièces de fin d'exercice.", DeliveryMode.Incremental,
            Item("Relevés bancaires de l'exercice", "relevé de compte bancaire", maxFiles: 24, types: ["application/pdf"]),
            Item("Factures d'achats", maxFiles: 50),
            Item("Factures de ventes", maxFiles: 50),
            Item("Inventaire des stocks au dernier jour", required: false),
            Item("Tableaux d'emprunts", "tableau d'amortissement d'emprunt", required: false, maxFiles: 5))
    ];
}
