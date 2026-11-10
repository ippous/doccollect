using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Api.Endpoints;

public static class Mapping
{
    public static TenantDto ToDto(this Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.StorageProfileId, t.EncryptAtRest, t.AiVerificationEnabled, t.AiProviderKey, t.AllowedEmbedOrigins);

    public static TemplateItemDto ToDto(this TemplateItem i) =>
        new(i.Id, i.Label, i.Description, i.Required, i.AcceptedContentTypes, i.MaxSizeMb, i.MaxFiles, i.ExpectedDocumentType, i.Order);

    public static TemplateDto ToDto(this ChecklistTemplate t) =>
        new(t.Id, t.TenantId, t.Name, t.Description, t.Visibility, t.DefaultDeliveryMode,
            t.Items.OrderBy(i => i.Order).Select(ToDto).ToList());

    public static TemplateItem ToEntity(this TemplateItemDto d, int order) => new()
    {
        Label = d.Label.Trim(),
        Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim(),
        Required = d.Required,
        AcceptedContentTypes = d.AcceptedContentTypes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList(),
        MaxSizeMb = Math.Clamp(d.MaxSizeMb, 1, 100),
        MaxFiles = Math.Clamp(d.MaxFiles, 1, 50),
        ExpectedDocumentType = string.IsNullOrWhiteSpace(d.ExpectedDocumentType) ? null : d.ExpectedDocumentType.Trim(),
        Order = order
    };

    public static DocumentDto ToDto(this StoredDocument d) =>
        new(d.Id, d.FileName, d.ContentType, d.SizeBytes, d.UploadedAt, d.Encrypted, d.AiVerdict, d.AiDetectedType, d.AiConfidence, d.AiComment, d.DeliveredAt);

    public static DossierItemDto ToDto(this DossierItem i) =>
        new(i.Id, i.Label, i.Description, i.Required, i.AcceptedContentTypes, i.MaxSizeMb, i.MaxFiles, i.ExpectedDocumentType,
            i.Status, i.RejectionReason, i.Documents.OrderBy(d => d.UploadedAt).Select(ToDto).ToList());

    public static DossierDto ToDto(this Dossier d, string tenantName) =>
        new(d.Id, d.TenantId, tenantName, d.Title, d.Reference, d.ProviderEmail, d.ProviderName, d.Status, d.DeliveryMode,
            d.RequireRequesterValidation, d.DueDate, d.MessageToProvider,
            d.Items.OrderBy(i => i.Order).Select(ToDto).ToList(),
            d.MissingRequiredItems().Select(i => i.Label).ToList(),
            d.CreatedAt);

    public static DossierSummaryDto ToSummary(this Dossier d) =>
        new(d.Id, d.Title, d.Reference, d.ProviderEmail, d.Status, d.DeliveryMode,
            d.Items.Count(i => i.Required), d.Items.Count(i => i.Required && d.IsSatisfied(i)), d.DueDate, d.CreatedAt);

    public static void ValidateTemplate(SaveTemplateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new DomainException(ErrorCodes.Validation, "Le nom du modèle est obligatoire.");
        if (req.Items.Count == 0 || req.Items.Any(i => string.IsNullOrWhiteSpace(i.Label)))
            throw new DomainException(ErrorCodes.Validation, "Chaque pièce doit avoir un libellé, et le modèle au moins une pièce.");
    }
}
