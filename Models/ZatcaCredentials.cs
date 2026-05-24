namespace Girix.Zatca.Models;

public class ZatcaCredentials
{
    public required string   RequestId            { get; init; }
    public required string   BinarySecurityToken  { get; init; }
    public required string   Secret               { get; init; }
    public required string   CertificatePem       { get; init; }
    public required bool     IsProduction         { get; init; }
    public required DateTime IssuedAt             { get; init; }

    internal static ZatcaCredentials FromApiResponse(
        string requestId,
        string binarySecurityToken,
        string secret,
        bool isProduction = false)
    {
        var cert = string.IsNullOrEmpty(binarySecurityToken)
            ? ""
            : $"-----BEGIN CERTIFICATE-----\n{binarySecurityToken}\n-----END CERTIFICATE-----";

        return new ZatcaCredentials
        {
            RequestId           = requestId,
            BinarySecurityToken = binarySecurityToken,
            Secret              = secret,
            CertificatePem      = cert,
            IsProduction        = isProduction,
            IssuedAt            = DateTime.UtcNow
        };
    }
}
