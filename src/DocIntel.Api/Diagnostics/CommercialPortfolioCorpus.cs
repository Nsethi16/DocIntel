namespace DocIntel.Api.Diagnostics;

/// <summary>Original synthetic commercial portfolio with natural filenames, rich layouts and multilingual sources.</summary>
public static class CommercialPortfolioCorpus
{
    public const string Version = "DVX.CommercialPortfolio.20261104.v1";

    public static IReadOnlyList<DealVelocityDemoCorpus.Document> Build()
    {
        (string Key, string FileName, int Pages)[] manifest =
        [
            ("DVXQuoteAR", "Arbor-Ridge-Analytics-Commercial-Offer-DVX-Q-2026-1101.pdf", 3),
            ("DVXAgreement", "Arbor-Ridge-Master-Services-Agreement-DVX-MSA-2026-1101.pdf", 4),
            ("DVXSow", "Arbor-Ridge-Implementation-Statement-of-Work-DVX-SOW-2026-1101.pdf", 3),
            ("DVXOrder", "Arbor-Ridge-Purchase-Order-DVX-PO-2026-1101.pdf", 1),
            ("DVXInvoice", "Arbor-Ridge-Mobilization-Invoice-DVX-INV-2026-1101.pdf", 1),
            ("DVXAmendment", "Arbor-Ridge-Supplier-Risk-Amendment-DVX-AM-2026-1101.pdf", 2),
            ("DVXQuoteES", "Summit-Harbor-Propuesta-de-Automatizacion-DVX-Q-2026-1201.pdf", 3),
            ("DVXPendingES", "Summit-Harbor-Extension-Scope-DVX-Q-2026-1202.pdf", 1),
            ("DVXQuoteFR", "Crescent-Vale-Offre-de-Services-DVX-Q-2026-1301.pdf", 3),
            ("DVXPrivacyFR", "Crescent-Vale-Accord-Traitement-Donnees-DVX-DPA-2026-1301.pdf", 2),
            ("DVXQuoteDE", "Redwood-Lane-Handelsangebot-DVX-Q-2026-1401.pdf", 3),
            ("DVXPacket", "Arbor-Ridge-Commercial-Agreement-Packet-DVX-COM-2026-1101.pdf", 7),
            ("DVXCopy", "Arbor-Ridge-Commercial-Offer-DVX-Q-2026-1101-Procurement-Copy.pdf", 3),
            ("DVXScannedInvoice", "Arbor-Ridge-Mobilization-Invoice-DVX-INV-2026-1101-Scanned.pdf", 1)
        ];
        return manifest.Select(item =>
        {
            using var resource = typeof(CommercialPortfolioCorpus).Assembly.GetManifestResourceStream(
                "DocIntel.Api.Diagnostics.CommercialPortfolio." + item.FileName)
                ?? throw new InvalidDataException("A commercial portfolio PDF is unavailable.");
            if (resource.Length is < 5 or > 5 * 1024 * 1024)
                throw new InvalidDataException("A commercial portfolio PDF exceeds its bounds.");
            var bytes = new byte[checked((int)resource.Length)];
            resource.ReadExactly(bytes);
            if (!bytes.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidDataException("The commercial portfolio resource is not a PDF.");
            return new DealVelocityDemoCorpus.Document(item.Key, item.FileName, bytes, item.Pages);
        }).ToArray();
    }
}
