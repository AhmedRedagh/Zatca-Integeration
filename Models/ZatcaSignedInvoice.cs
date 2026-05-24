namespace Girix.Zatca.Models;

public class ZatcaSignedInvoice
{
    public required string SignedXml       { get; init; }
    public required string InvoiceBase64   { get; init; }
    public required string InvoiceHash     { get; init; }
    public required string SignatureBase64  { get; init; }
    public required string QrBase64        { get; init; }
}
