namespace Girix.Zatca.Models;

public class ZatcaClearanceResult
{
    public required string        InvoiceUuid          { get; init; }
    public required string        InvoiceHash          { get; init; }
    public required string        Status               { get; init; }
    public string?                ClearedInvoiceBase64 { get; init; }
    public string?                QrCode               { get; init; }
    public IReadOnlyList<string>  Warnings             { get; init; } = [];
    public IReadOnlyList<string>  Errors               { get; init; } = [];

    public bool IsAccepted => Status is "CLEARED" or "REPORTED";
}
