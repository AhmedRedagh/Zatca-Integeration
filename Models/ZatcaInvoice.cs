using Girix.Zatca.Enums;

namespace Girix.Zatca.Models;

public class ZatcaInvoice
{
    public required string               Uuid                 { get; init; }
    public required string               InvoiceNumber        { get; init; }
    public required int                  InvoiceCounterValue  { get; init; }
    public required ZatcaInvoiceType     InvoiceType          { get; init; }
    public required DateTime             InvoiceDate          { get; init; }
    public required ZatcaSeller          Seller               { get; init; }
    public required ZatcaBuyer           Buyer                { get; init; }
    public required List<ZatcaInvoiceLine> Lines              { get; init; }
    public required double               LineExtensionAmount  { get; init; }
    public required double               TaxableAmount        { get; init; }
    public required double               VatAmount            { get; init; }
    public required double               PayableAmount        { get; init; }
    public required ZatcaPaymentMeans    PaymentMeans         { get; init; }
    public double                        AllowanceAmount      { get; init; } = 0.0;
    public string                        Currency             { get; init; } = "SAR";
    public string?                       Notes                { get; init; }
    public string?                       PreviousInvoiceHash  { get; init; }
    public string?                       OriginalInvoiceNumber{ get; init; }
}
