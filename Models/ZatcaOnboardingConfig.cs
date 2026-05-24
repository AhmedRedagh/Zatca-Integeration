using Girix.Zatca.Enums;

namespace Girix.Zatca.Models;

public class ZatcaOnboardingConfig
{
    public string           OrganizationIdentifier { get; init; } = "399999999900003";
    public string           OrganizationUnitName   { get; init; } = "399999999900003";
    public string           OrganizationName       { get; init; } = "Maximum Speed Tech Supply LTD";
    public string           InvoiceType            { get; init; } = "1100";
    public string           BusinessCategory       { get; init; } = "Supply activities";
    public string           SerialNumber           { get; init; } = "1-SpiderSystem|2-v1.0|3-ed22f1d8-e6a2-1118-9b58-d9a8f11e445f";
    public string           RegisteredAddress      { get; init; } = "RRRD2929";
    public string           CountryName            { get; init; } = "SA";
    public ZatcaEnvironment Environment            { get; init; } = ZatcaEnvironment.Sandbox;
}
