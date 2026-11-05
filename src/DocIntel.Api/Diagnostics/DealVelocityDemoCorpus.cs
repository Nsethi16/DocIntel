namespace DocIntel.Api.Diagnostics;

/// <summary>Fixed authored fictional commercial documents; no real customer records, contacts or signatures.</summary>
public static class DealVelocityDemoCorpus
{
    public const string Version = "DealVelocity.Demo.20261104.v1";

    public sealed record Document(string Key, string FileName, byte[] Bytes, int Pages);

    /// <summary>Loads immutable PDFs so confirmed retries and inspections use the same original hashes.</summary>
    public static IReadOnlyList<Document> Build()
    {
        (string Key, string FileName, int Pages)[] manifest =
        [
            ("Q1041", "DV-Demo-DV-Q-2026-1041-Arbor-Ridge-Manufacturing.pdf", 2),
            ("Q1042", "DV-Demo-DV-Q-2026-1042-Arbor-Ridge-Manufacturing.pdf", 2),
            ("Q2081", "DV-Demo-DV-Q-2026-2081-Summit-Harbor-Logistics.pdf", 2),
            ("Q3051", "DV-Demo-DV-Q-2026-3051-Redwood-Lane-Retail.pdf", 2),
            ("Q4101", "DV-Demo-DV-Q-2026-4101-Crescent-Vale-Health-Services.pdf", 2),
            ("Q1041Copy", "DV-Demo-Q1041-Arbor-Ridge-Copy.pdf", 2),
            ("Q2082Draft", "DV-Demo-Q2082-Summit-Harbor-Draft.pdf", 1),
            ("Agreement", "DV-Demo-Arbor-Ridge-Master-Services-Agreement.pdf", 3),
            ("Invoice", "DV-Demo-Summit-Harbor-Deposit-Invoice.pdf", 1)
        ];
        return manifest.Select(item => new Document(item.Key, item.FileName, Read(item.FileName), item.Pages)).ToArray();
    }

    private static byte[] Read(string fileName)
    {
        using var resource = typeof(DealVelocityDemoCorpus).Assembly.GetManifestResourceStream(
            "DocIntel.Api.Diagnostics.DealVelocityDocuments." + fileName)
            ?? throw new InvalidDataException("A fixed demonstration PDF resource is unavailable.");
        if (resource.Length is < 5 or > 1024 * 1024)
            throw new InvalidDataException("A fixed demonstration PDF resource exceeds its bounds.");
        var bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("A fixed demonstration resource is not a PDF.");
        return bytes;
    }
}
