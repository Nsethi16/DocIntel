using System.Text.Json.Serialization;

namespace DocIntel.Core.Ocr;

/// <summary>Controls when the accepted document configuration requires source text recognition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrMode { Auto, Always, Never }

/// <summary>Tenant policy selects recognition behavior, never a credential-bearing provider endpoint.</summary>
public sealed class OcrPolicy
{
    public OcrMode Mode { get; set; } = OcrMode.Auto;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("The OCR mode is not supported.");
    }
}
