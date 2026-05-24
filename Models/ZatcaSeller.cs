namespace Girix.Zatca.Models;

public class ZatcaSeller
{
    public required string SellerName           { get; init; }
    public required string VatRegistrationNumber{ get; init; }
    public required string CrNumber            { get; init; }
    public required string BuildingNumber      { get; init; }
    public required string StreetName          { get; init; }
    public required string CityName            { get; init; }
    public required string PostalCode          { get; init; }
    public required string CountryCode         { get; init; }
    public string?         DistrictName        { get; init; }
}
