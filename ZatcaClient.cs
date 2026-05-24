using System.Text;
using Girix.Zatca.Enums;
using Girix.Zatca.Exceptions;
using Girix.Zatca.Models;
using Girix.Zatca.Services;

namespace Girix.Zatca;

/// <summary>
/// Main entry point for the Girix ZATCA Phase 2 library.
///
/// Typical backend flow:
///   1. OnboardAsync()         — one-time per device
///   2. SignInvoiceAsync()     — for every invoice
///   3. ClearInvoiceAsync()   — B2B (standard invoices)
///      or ReportInvoiceAsync() — B2C (simplified invoices)
/// </summary>
public sealed class ZatcaClient : IDisposable
{
    private readonly ZatcaCryptoService  _crypto;
    private readonly ZatcaXmlBuilder     _xml;
    private readonly ZatcaInvoiceHasher  _hasher;
    private readonly ZatcaQrEncoder      _qr;
    private readonly ZatcaApiClient      _api;
    private readonly ZatcaEnvironment    _environment;

    // ────────────────────────────────────────────────────────────────────────────
    // Construction
    // ────────────────────────────────────────────────────────────────────────────

    /// <param name="environment">Sandbox (default), Simulation, or Production.</param>
    /// <param name="httpClient">Optional: supply your own HttpClient (e.g., from IHttpClientFactory).</param>
    public ZatcaClient(ZatcaEnvironment environment = ZatcaEnvironment.Sandbox, HttpClient? httpClient = null)
    {
        _environment = environment;
        _crypto      = new ZatcaCryptoService();
        _xml         = new ZatcaXmlBuilder();
        _hasher      = new ZatcaInvoiceHasher();
        _qr          = new ZatcaQrEncoder();
        _api         = new ZatcaApiClient(environment.GetBaseUrl(), httpClient);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Onboarding — run once per ZATCA device registration
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full 4-step onboarding:
    ///   1. Generate EC secp256k1 key pair
    ///   2. Build CSR and submit to ZATCA → compliance CSID
    ///   3. Run 3 compliance invoice checks
    ///   4. Upgrade compliance CSID → production CSID
    /// Returns the production credentials that must be stored securely.
    /// </summary>
    public async Task<OnboardingResult> OnboardAsync(string otp, ZatcaOnboardingConfig config)
    {
        try
        {
            // Step 1 — key pair
            var (privateKeyPem, publicKeyPem) = _crypto.GenerateKeyPair();

            // Step 2 — compliance CSID
            var csrPem             = _crypto.BuildCsrPem(privateKeyPem, publicKeyPem, config);
            var complianceCreds    = await _api.FetchComplianceCsidAsync(otp, csrPem);

            // Step 3 — run compliance checks (standard + simplified samples)
            await ExecuteComplianceChecksAsync(complianceCreds, privateKeyPem);

            // Step 4 — production CSID
            var productionCreds = await _api.FetchProductionCsidAsync(complianceCreds);

            return new OnboardingResult
            {
                PrivateKeyPem        = privateKeyPem,
                PublicKeyPem         = publicKeyPem,
                CsrPem               = csrPem,
                ComplianceCredentials = complianceCreds,
                ProductionCredentials = productionCreds,
                IsSuccess            = true
            };
        }
        catch (ZatcaException) { throw; }
        catch (Exception ex)
        {
            throw new ZatcaOnboardingException($"Onboarding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a key pair and CSR only — useful when the caller controls the onboarding steps.
    /// </summary>
    public (string PrivateKeyPem, string PublicKeyPem, string CsrPem) GenerateCsr(ZatcaOnboardingConfig config)
    {
        var (priv, pub) = _crypto.GenerateKeyPair();
        var csr         = _crypto.BuildCsrPem(priv, pub, config);
        return (priv, pub, csr);
    }

    /// <summary>
    /// Step 2 of onboarding: submit CSR and OTP to obtain compliance CSID.
    /// </summary>
    public Task<ZatcaCredentials> GetComplianceCsidAsync(string otp, string csrPem) =>
        _api.FetchComplianceCsidAsync(otp, csrPem);

    /// <summary>
    /// Step 3 of onboarding: run compliance invoice checks.
    /// </summary>
    public Task RunComplianceChecksAsync(ZatcaCredentials complianceCreds, string privateKeyPem) =>
        ExecuteComplianceChecksAsync(complianceCreds, privateKeyPem);

    /// <summary>
    /// Step 4 of onboarding: upgrade compliance CSID to production CSID.
    /// </summary>
    public Task<ZatcaCredentials> GetProductionCsidAsync(ZatcaCredentials complianceCreds) =>
        _api.FetchProductionCsidAsync(complianceCreds);

    // ────────────────────────────────────────────────────────────────────────────
    // Invoice signing (local — no network call)
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs an invoice locally: builds UBL XML, hashes it, signs the hash,
    /// generates a TLV QR code, and embeds everything into the signed XML.
    /// No network call — returns the signed payload ready for submission.
    /// </summary>
    public ZatcaSignedInvoice SignInvoice(
        ZatcaInvoice  invoice,
        ZatcaCredentials credentials,
        string        privateKeyPem)
    {
        try
        {
            var xml = invoice.InvoiceType switch
            {
                ZatcaInvoiceType.Simplified => _xml.BuildSimplifiedInvoice(invoice),
                ZatcaInvoiceType.CreditNote => _xml.BuildCreditNote(invoice),
                ZatcaInvoiceType.DebitNote  => _xml.BuildDebitNote(invoice),
                _                           => _xml.BuildStandardInvoice(invoice)
            };

            var hash           = _hasher.HashInvoice(xml);
            var hashBytes      = Convert.FromBase64String(hash);
            var sigBytes       = _crypto.SignData(hashBytes, privateKeyPem);
            var signatureBase64 = Convert.ToBase64String(sigBytes);

            var qrBase64 = _qr.Encode(invoice, signatureBase64, credentials.CertificatePem, hash);

            var signedXml    = _xml.EmbedSignatureAndQr(xml, hash, signatureBase64, qrBase64, credentials.CertificatePem);
            var invoiceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(signedXml));

            return new ZatcaSignedInvoice
            {
                SignedXml       = signedXml,
                InvoiceBase64   = invoiceBase64,
                InvoiceHash     = hash,
                SignatureBase64  = signatureBase64,
                QrBase64        = qrBase64
            };
        }
        catch (ZatcaException) { throw; }
        catch (Exception ex)
        {
            throw new ZatcaXmlException($"Invoice signing failed: {ex.Message}", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Clearance (B2B — standard invoices)
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<ZatcaClearanceResult> ClearInvoiceAsync(
        ZatcaInvoice     invoice,
        ZatcaCredentials credentials,
        string           privateKeyPem)
    {
        var signed = SignInvoice(invoice, credentials, privateKeyPem);
        return await _api.ClearInvoiceAsync(
            signed.InvoiceBase64,
            signed.InvoiceHash,
            invoice.Uuid,
            credentials);
    }

    /// <summary>
    /// Clears a pre-signed invoice (use when you already called SignInvoice separately).
    /// </summary>
    public Task<ZatcaClearanceResult> ClearSignedInvoiceAsync(
        ZatcaSignedInvoice signed,
        string             invoiceUuid,
        ZatcaCredentials   credentials) =>
        _api.ClearInvoiceAsync(signed.InvoiceBase64, signed.InvoiceHash, invoiceUuid, credentials);

    // ────────────────────────────────────────────────────────────────────────────
    // Reporting (B2C — simplified invoices)
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<ZatcaReportingResult> ReportInvoiceAsync(
        ZatcaInvoice     invoice,
        ZatcaCredentials credentials,
        string           privateKeyPem)
    {
        var signed = SignInvoice(invoice, credentials, privateKeyPem);
        return await _api.ReportInvoiceAsync(
            signed.InvoiceBase64,
            signed.InvoiceHash,
            invoice.Uuid,
            credentials);
    }

    /// <summary>
    /// Reports a pre-signed invoice (use when you already called SignInvoice separately).
    /// </summary>
    public Task<ZatcaReportingResult> ReportSignedInvoiceAsync(
        ZatcaSignedInvoice signed,
        string             invoiceUuid,
        ZatcaCredentials   credentials) =>
        _api.ReportInvoiceAsync(signed.InvoiceBase64, signed.InvoiceHash, invoiceUuid, credentials);

    // ────────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ────────────────────────────────────────────────────────────────────────────

    private async Task ExecuteComplianceChecksAsync(ZatcaCredentials creds, string privateKeyPem)
    {
        // Submit a sample standard invoice and simplified invoice to pass compliance
        var sampleStandard    = BuildSampleInvoice(ZatcaInvoiceType.Standard,    "SAMPLE-STD-001", 1);
        var sampleSimplified  = BuildSampleInvoice(ZatcaInvoiceType.Simplified,  "SAMPLE-SMP-001", 2);

        var signedStd = SignInvoice(sampleStandard,   creds, privateKeyPem);
        var signedSmp = SignInvoice(sampleSimplified, creds, privateKeyPem);

        await _api.RunComplianceCheckAsync(signedStd.InvoiceBase64, signedStd.InvoiceHash, sampleStandard.Uuid,   creds);
        await _api.RunComplianceCheckAsync(signedSmp.InvoiceBase64, signedSmp.InvoiceHash, sampleSimplified.Uuid, creds);
    }

    private static ZatcaInvoice BuildSampleInvoice(ZatcaInvoiceType type, string number, int icv) =>
        new()
        {
            Uuid                = Guid.NewGuid().ToString(),
            InvoiceNumber       = number,
            InvoiceCounterValue = icv,
            InvoiceType         = type,
            InvoiceDate         = DateTime.UtcNow,
            Seller = new ZatcaSeller
            {
                SellerName            = "Sample Company",
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
                BuyerName             = "Sample Buyer",
                CountryCode           = "SA",
                VatRegistrationNumber = "300000000100003"
            },
            Lines = [new ZatcaInvoiceLine
            {
                LineId              = 1,
                ProductName         = "Sample Product",
                Quantity            = 1.0,
                UnitCode            = "PCE",
                UnitPrice           = 100.0,
                LineExtensionAmount = 100.0,
                TaxableAmount       = 100.0,
                VatRate             = 0.15,
                VatAmount           = 15.0,
                LineTotal           = 115.0
            }],
            LineExtensionAmount = 100.0,
            TaxableAmount       = 100.0,
            VatAmount           = 15.0,
            PayableAmount       = 115.0,
            PaymentMeans        = ZatcaPaymentMeans.Cash
        };

    public void Dispose() => _api.Dispose();
}

// ────────────────────────────────────────────────────────────────────────────
// Result types
// ────────────────────────────────────────────────────────────────────────────

public class OnboardingResult
{
    public required string           PrivateKeyPem         { get; init; }
    public required string           PublicKeyPem          { get; init; }
    public required string           CsrPem                { get; init; }
    public required ZatcaCredentials ComplianceCredentials { get; init; }
    public required ZatcaCredentials ProductionCredentials { get; init; }
    public required bool             IsSuccess             { get; init; }
}
