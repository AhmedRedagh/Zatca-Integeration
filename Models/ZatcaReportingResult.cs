namespace Girix.Zatca.Models;

public class ZatcaReportingResult
{
    public required string        InvoiceUuid { get; init; }
    public required string        InvoiceHash { get; init; }
    public required string        Status      { get; init; }
    public IReadOnlyList<string>  Warnings    { get; init; } = [];
    public IReadOnlyList<string>  Errors      { get; init; } = [];

    public bool IsAccepted => Status is "REPORTED" or "CLEARED";
}
