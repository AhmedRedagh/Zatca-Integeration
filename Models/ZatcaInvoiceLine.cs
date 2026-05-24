namespace Girix.Zatca.Models;

public class ZatcaInvoiceLine
{
    public required int    LineId               { get; init; }
    public required string ProductName          { get; init; }
    public required double Quantity             { get; init; }
    public required string UnitCode             { get; init; }
    public required double UnitPrice            { get; init; }
    public required double LineExtensionAmount  { get; init; }
    public required double TaxableAmount        { get; init; }
    public required double VatRate              { get; init; }
    public required double VatAmount            { get; init; }
    public required double LineTotal            { get; init; }
    public string?         ProductCode          { get; init; }
    public double          DiscountAmount       { get; init; } = 0.0;
    public string          VatCategoryCode      { get; init; } = "S";
}
