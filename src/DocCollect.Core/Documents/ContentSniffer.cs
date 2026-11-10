namespace DocCollect.Core.Documents;

/// <summary>
/// Détermine le type réel d'un fichier à partir de ses premiers octets, pour ne pas se fier
/// au type déclaré par le navigateur (un .exe renommé en .pdf est détecté).
/// </summary>
public static class ContentSniffer
{
    public const int HeaderLength = 16;

    public static string? Detect(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith("%PDF-"u8)) return "application/pdf";
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)) return "image/gif";
        if (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = h[8..12];
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8)) return "image/heic";
        }
        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8)) return "image/tiff";
        if (h.StartsWith("PK\x03\x04"u8)) return "application/zip"; // docx/xlsx/odt sont des zip : affiné par l'extension
        if (h.StartsWith("MZ"u8)) return "application/x-msdownload";
        return null;
    }

    private static readonly Dictionary<string, string> OfficeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet"
    };

    /// <summary>Type à retenir : le type détecté prime ; les conteneurs zip sont affinés par l'extension.</summary>
    public static string Resolve(ReadOnlySpan<byte> header, string declaredContentType, string fileName)
    {
        var detected = Detect(header);
        if (detected == "application/zip" && OfficeByExtension.TryGetValue(Path.GetExtension(fileName), out var office))
            return office;
        if (detected is not null) return detected;
        var declared = (declaredContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(declared) ? "application/octet-stream" : declared;
    }

    public static bool IsDangerous(string contentType) => contentType is "application/x-msdownload";
}
