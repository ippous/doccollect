namespace DocCollect.Shared;

// ---------- Authentification ----------

public sealed record LoginRequest(string Email, string Password, string? TwoFactorCode = null, string? RecoveryCode = null);

public sealed record LoginResponse(
    bool Succeeded,
    bool RequiresTwoFactor = false,
    TwoFactorMethod Method = TwoFactorMethod.None,
    bool MustEnrollTwoFactor = false,
    string? AccessToken = null,
    string? RefreshToken = null,
    int ExpiresInSeconds = 0,
    string? Error = null);

public sealed record RefreshRequest(string RefreshToken);

public sealed record MeResponse(
    string UserId,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    Guid? TenantId,
    bool TwoFactorEnabled,
    TwoFactorMethod TwoFactorMethod);

public sealed record AuthenticatorSetupResponse(string SharedKey, string AuthenticatorUri);

public sealed record EnableTwoFactorRequest(TwoFactorMethod Method, string Code);

/// <summary>Après activation, de nouveaux jetons sont émis (ils portent l'indicateur 2FA).</summary>
public sealed record EnableTwoFactorResponse(
    bool Succeeded,
    IReadOnlyList<string> RecoveryCodes,
    string? Error = null,
    LoginResponse? Tokens = null);

public sealed record AcceptInvitationRequest(string Token, string Password, string? DisplayName);

public sealed record InvitationInfo(string Email, string? TenantName, string Role, bool Usable);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Code, string NewPassword);

public sealed record InviteUserRequest(string Email, string Role, string? DisplayName);

public sealed record UserDto(string Id, string Email, string? DisplayName, IReadOnlyList<string> Roles, Guid? TenantId, bool TwoFactorEnabled, bool LockedOut);

// ---------- Tenants (demandeurs) ----------

public sealed record TenantDto(
    Guid Id,
    string Name,
    string Slug,
    Guid? StorageProfileId,
    bool EncryptAtRest,
    bool AiVerificationEnabled,
    string? AiProviderKey,
    IReadOnlyList<string> AllowedEmbedOrigins);

public sealed record CreateTenantRequest(string Name, string Slug, string OwnerEmail, string? OwnerDisplayName);

public sealed record UpdateTenantSettingsRequest(
    Guid? StorageProfileId,
    bool EncryptAtRest,
    bool AiVerificationEnabled,
    string? AiProviderKey,
    IReadOnlyList<string> AllowedEmbedOrigins);

// ---------- Stockage ----------

public sealed record StorageProviderInfo(string Type, string DisplayName, IReadOnlyList<StorageSettingField> Fields);

public sealed record StorageSettingField(string Name, string Label, bool Required, bool Secret, string? DefaultValue = null, string? Help = null);

public sealed record StorageProfileDto(
    Guid Id,
    Guid? TenantId,
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string?> Settings,
    bool IsDefault);

public sealed record SaveStorageProfileRequest(
    string Name,
    string ProviderType,
    Dictionary<string, string?> Settings,
    bool IsDefault);

public sealed record StorageTestResult(bool Succeeded, string Message);

// ---------- Modèles ----------

public sealed record TemplateItemDto(
    Guid? Id,
    string Label,
    string? Description,
    bool Required,
    IReadOnlyList<string> AcceptedContentTypes,
    int MaxSizeMb,
    int MaxFiles,
    string? ExpectedDocumentType,
    int Order);

public sealed record TemplateDto(
    Guid Id,
    Guid? TenantId,
    string Name,
    string? Description,
    TemplateVisibility Visibility,
    DeliveryMode DefaultDeliveryMode,
    IReadOnlyList<TemplateItemDto> Items);

public sealed record SaveTemplateRequest(
    string Name,
    string? Description,
    TemplateVisibility Visibility,
    DeliveryMode DefaultDeliveryMode,
    IReadOnlyList<TemplateItemDto> Items);

// ---------- Dossiers ----------

public sealed record CreateDossierRequest(
    string Title,
    string? Reference,
    Guid? TemplateId,
    IReadOnlyList<TemplateItemDto>? AdditionalItems,
    string ProviderEmail,
    string? ProviderName,
    DeliveryMode DeliveryMode,
    bool RequireRequesterValidation,
    DateOnly? DueDate,
    string? MessageToProvider);

public sealed record DossierSummaryDto(
    Guid Id,
    string Title,
    string? Reference,
    string ProviderEmail,
    DossierStatus Status,
    DeliveryMode DeliveryMode,
    int RequiredCount,
    int SatisfiedCount,
    DateOnly? DueDate,
    DateTimeOffset CreatedAt);

public sealed record DossierDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Title,
    string? Reference,
    string ProviderEmail,
    string? ProviderName,
    DossierStatus Status,
    DeliveryMode DeliveryMode,
    bool RequireRequesterValidation,
    DateOnly? DueDate,
    string? MessageToProvider,
    IReadOnlyList<DossierItemDto> Items,
    IReadOnlyList<string> MissingRequiredItems,
    DateTimeOffset CreatedAt);

public sealed record DossierItemDto(
    Guid Id,
    string Label,
    string? Description,
    bool Required,
    IReadOnlyList<string> AcceptedContentTypes,
    int MaxSizeMb,
    int MaxFiles,
    string? ExpectedDocumentType,
    ItemStatus Status,
    string? RejectionReason,
    IReadOnlyList<DocumentDto> Documents);

public sealed record DocumentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    bool Encrypted,
    AiVerdict AiVerdict,
    string? AiDetectedType,
    double? AiConfidence,
    string? AiComment,
    DateTimeOffset? DeliveredAt);

public sealed record RejectItemRequest(string Reason);

public sealed record DeliveryChannelDto(Guid Id, DeliveryChannelType Type, string Name, IReadOnlyDictionary<string, string?> Settings, bool Enabled);

public sealed record SaveDeliveryChannelRequest(DeliveryChannelType Type, string Name, Dictionary<string, string?> Settings, bool Enabled);

// ---------- IA ----------

public sealed record AiProviderInfo(string Key, string DisplayName, string? Region, bool SupportsPdf);

public sealed record AiUsageDto(Guid TenantId, int Year, int Month, int Calls, long InputTokens, long OutputTokens);

// ---------- Divers ----------

public sealed record ApiError(string Code, string Message);
