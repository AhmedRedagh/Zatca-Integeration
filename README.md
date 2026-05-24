# Girix.Zatca

A **.NET 8 class library** implementing ZATCA Phase 2 e-invoicing for Saudi Arabia.
Ported 1-to-1 from the Flutter/Dart implementation used in the Spider mobile app.

## What's included

| Feature | Details |
|---------|---------|
| CSR generation | ECDSA secp256k1 key pair + PKCS#10 CSR with ZATCA-required extensions |
| XML building | UBL 2.1 — standard, simplified, credit note, debit note |
| Invoice hashing | SHA-256 per ZATCA C14N spec (strips UBLExtensions / Signature / QR before hashing) |
| QR encoding | TLV (Tag-Length-Value) base64, 8 tags as per Phase 2 spec |
| Digital signing | ECDSA-SHA256, DER-encoded, deterministic K (RFC 6979) |
| Onboarding | Full 4-step: CSR → compliance CSID → compliance checks → production CSID |
| Clearance | B2B real-time clearance (`/invoices/clearance/single`) |
| Reporting | B2C simplified invoice reporting (`/invoices/reporting/single`) |

## Requirements

- .NET 10 or later
- NuGet: `BouncyCastle.Cryptography` 2.4.x (automatically restored)

## Installation

Add a project reference or build and copy `Girix.Zatca.dll` + `BouncyCastle.Cryptography.dll`.

```xml
<!-- .csproj -->
<ProjectReference Include="..\Girix.Zatca\Girix.Zatca.csproj" />
```

## Quick start

### 1. One-time onboarding

Run once per registered ZATCA device. Store the returned keys and credentials securely
(database, Azure Key Vault, etc.) — never regenerate them per invoice.

```csharp
using Girix.Zatca;
using Girix.Zatca.Enums;
using Girix.Zatca.Models;

var client = new ZatcaClient(ZatcaEnvironment.Sandbox);

var config = new ZatcaOnboardingConfig
{
    OrganizationIdentifier = "399999999900003",
    OrganizationUnitName   = "399999999900003",
    OrganizationName       = "My Company Ltd",
    InvoiceType            = "1100",
    BusinessCategory       = "Supply activities",
    SerialNumber           = "1-MyApp|2-v1.0|3-device-uuid-here",
    RegisteredAddress      = "RRRD2929",
    CountryName            = "SA",
    Environment            = ZatcaEnvironment.Sandbox
};

OnboardingResult result = await client.OnboardAsync(otp: "123321", config);

// Persist these — never generate them again for the same device
string privateKeyPem         = result.PrivateKeyPem;
string productionBst         = result.ProductionCredentials.BinarySecurityToken;
string productionSecret      = result.ProductionCredentials.Secret;
string productionCertPem     = result.ProductionCredentials.CertificatePem;
string productionRequestId   = result.ProductionCredentials.RequestId;
```

### 2. Reconstruct credentials from storage

```csharp
var credentials = new ZatcaCredentials
{
    RequestId           = productionRequestId,
    BinarySecurityToken = productionBst,
    Secret              = productionSecret,
    CertificatePem      = productionCertPem,
    IsProduction        = true,
    IssuedAt            = DateTime.UtcNow
};
```

### 3. Clear a B2B (standard) invoice

```csharp
var client = new ZatcaClient(ZatcaEnvironment.Production);

var invoice = new ZatcaInvoice
{
    Uuid                = Guid.NewGuid().ToString(),
    InvoiceNumber       = "INV-2025-00001",
    InvoiceCounterValue = 1,
    InvoiceType         = ZatcaInvoiceType.Standard,
    InvoiceDate         = DateTime.UtcNow,
    Seller = new ZatcaSeller
    {
        SellerName            = "My Company Ltd",
        VatRegistrationNumber = "300000000000003",
        CrNumber              = "1234567890",
        BuildingNumber        = "1234",
        StreetName            = "King Fahad Road",
        CityName              = "Riyadh",
        PostalCode            = "12345",
        CountryCode           = "SA"
    },
    Buyer = new ZatcaBuyer
    {
        BuyerName             = "Client Corp",
        CountryCode           = "SA",
        VatRegistrationNumber = "300000000100003",
        StreetName            = "Olaya St",
        BuildingNumber        = "5678",
        CityName              = "Riyadh",
        PostalCode            = "11543"
    },
    Lines =
    [
        new ZatcaInvoiceLine
        {
            LineId              = 1,
            ProductName         = "Consulting Services",
            Quantity            = 10.0,
            UnitCode            = "HUR",
            UnitPrice           = 500.0,
            LineExtensionAmount = 5000.0,
            TaxableAmount       = 5000.0,
            VatRate             = 0.15,
            VatAmount           = 750.0,
            LineTotal           = 5750.0
        }
    ],
    LineExtensionAmount = 5000.0,
    TaxableAmount       = 5000.0,
    VatAmount           = 750.0,
    PayableAmount       = 5750.0,
    PaymentMeans        = ZatcaPaymentMeans.Bank,
    PreviousInvoiceHash = lastInvoiceHash   // from DB; null on first invoice
};

ZatcaClearanceResult clearanceResult = await client.ClearInvoiceAsync(
    invoice,
    credentials,
    privateKeyPem);

Console.WriteLine($"Status: {clearanceResult.Status}");
Console.WriteLine($"Accepted: {clearanceResult.IsAccepted}");
```

### 4. Report a B2C (simplified) invoice

```csharp
var invoice = new ZatcaInvoice
{
    InvoiceType = ZatcaInvoiceType.Simplified,
    // ...same fields as above; Buyer address/VAT are optional for B2C
};

ZatcaReportingResult reportResult = await client.ReportInvoiceAsync(
    invoice,
    credentials,
    privateKeyPem);
```

### 5. Sign only (local, no network)

Useful when you want to persist the signed XML separately and submit later:

```csharp
ZatcaSignedInvoice signed = client.SignInvoice(invoice, credentials, privateKeyPem);

// signed.SignedXml        — full UBL XML with embedded signature and QR
// signed.InvoiceBase64    — base64 of signed XML (what ZATCA receives)
// signed.InvoiceHash      — SHA-256 hash (store as PreviousInvoiceHash for next invoice)
// signed.QrBase64         — TLV QR code for printing on the invoice

// Submit later:
ZatcaClearanceResult result = await client.ClearSignedInvoiceAsync(signed, invoice.Uuid, credentials);
```

## Dependency injection (.NET DI)

```csharp
// Program.cs / Startup.cs
builder.Services.AddHttpClient("zatca");
builder.Services.AddSingleton<ZatcaClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ZatcaClient(ZatcaEnvironment.Production, factory.CreateClient("zatca"));
});
```

## Environments

| Enum value | Base URL |
|-----------|---------|
| `Sandbox` (default) | `https://gw-fatoora.zatca.gov.sa/e-invoicing/developer-portal` |
| `Simulation` | Same as sandbox (different template name in CSR) |
| `Production` | `https://gw-fatoora.zatca.gov.sa/e-invoicing/core` |

## Errors

All errors throw a subclass of `ZatcaException`:

```csharp
try
{
    await client.ClearInvoiceAsync(...);
}
catch (ZatcaClearanceException ex)
{
    // ex.Message contains ZATCA API error detail
    // ex.Code contains the HTTP status code
}
catch (ZatcaCryptoException ex) { /* key / signing error */ }
catch (ZatcaXmlException ex)    { /* XML building error */ }
catch (ZatcaOnboardingException ex) { /* onboarding step failed */ }
```

## Cryptography notes

- Curve: **secp256k1** (same as Bitcoin/Ethereum, required by ZATCA)
- Signing: ECDSA-SHA256 with RFC 6979 deterministic K (via BouncyCastle)
- CSR: PKCS#10 with `certificateTemplateName` and `subjectAltName` (dirName) extensions required by ZATCA
- Hash chain: store `ZatcaSignedInvoice.InvoiceHash` and pass it as `PreviousInvoiceHash` on the next invoice

## Building

```bash
dotnet build -c Release
# Output: bin/Release/net10.0/Girix.Zatca.dll
```
