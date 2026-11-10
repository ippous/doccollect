namespace DocCollect.Domain;

/// <summary>Violation d'une règle métier. <see cref="Code"/> est stable et peut être affiché/traduit côté client.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string InvalidState = "invalid_state";
    public const string ItemNotFound = "item_not_found";
    public const string DocumentNotFound = "document_not_found";
    public const string ContentTypeNotAllowed = "content_type_not_allowed";
    public const string FileTooLarge = "file_too_large";
    public const string TooManyFiles = "too_many_files";
    public const string AlreadyDelivered = "already_delivered";
    public const string Validation = "validation";
}
