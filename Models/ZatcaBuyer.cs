namespace Girix.Zatca.Models;

public class ZatcaBuyer
{
    public required string BuyerName            { get; init; }
    public required string CountryCode          { get; init; }
    public string?         VatRegistrationNumber{ get; init; }
    public string?         NationalId           { get; init; }
    public string?         StreetName           { get; init; }
    public string?         BuildingNumber       { get; init; }
    public string?         CityName             { get; init; }
    public string?         PostalCode           { get; init; }
    public string?         DistrictName         { get; init; }
}
