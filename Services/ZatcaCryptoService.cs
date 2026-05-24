using System.Text;
using Girix.Zatca.Exceptions;
using Girix.Zatca.Models;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Girix.Zatca.Services;

/// <summary>
/// Handles ECDSA secp256k1 key generation, PKCS#10 CSR building, and DER-encoded signing.
/// Mirrors the Dart ZatcaCryptoServiceImpl exactly so that CSRs are accepted by ZATCA.
/// </summary>
public class ZatcaCryptoService
{
    private static readonly X9ECParameters  Curve      = SecNamedCurves.GetByName("secp256k1");
    private static readonly ECDomainParameters DomainParams =
        new(Curve.Curve, Curve.G, Curve.N, Curve.H);

    // secp256k1 OID bytes: 1.3.132.0.10
    private static readonly byte[] Secp256k1OidBytes = [0x2b, 0x81, 0x04, 0x00, 0x0a];

    // ────────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────────

    public (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        var keyGenParams = new ECKeyGenerationParameters(DomainParams, new SecureRandom());
        var generator    = new ECKeyPairGenerator();
        generator.Init(keyGenParams);
        var pair = generator.GenerateKeyPair();

        var priv = (ECPrivateKeyParameters)pair.Private;
        var pub  = (ECPublicKeyParameters)pair.Public;

        return (EncodePrivateKeyPem(priv), EncodePublicKeyPem(pub));
    }

    public string BuildCsrPem(string privateKeyPem, string publicKeyPem, ZatcaOnboardingConfig config)
    {
        try
        {
            var priv = DecodePrivateKeyPem(privateKeyPem);
            var pub  = DecodePublicKeyPem(publicKeyPem);

            var subjectDn   = BuildSubjectDn(config);
            var spki        = BuildSpki(pub);
            var certReqInfo = BuildCertReqInfo(subjectDn, spki, config);

            var certReqInfoDer = certReqInfo.GetDerEncoded();

            // Sign CertReqInfo bytes with ECDSA-SHA256
            var signer = new ECDsaSigner(new HMacDsaKCalculator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest()));
            signer.Init(true, new ParametersWithRandom(priv, new SecureRandom()));
            var hash = ComputeSha256(certReqInfoDer);
            var sig  = signer.GenerateSignature(hash);
            var derSig = EncodeDerSignature(sig[0], sig[1]);

            // Build full CSR SEQUENCE
            var csr = new DerSequence(
                certReqInfo,
                EcdsaSha256AlgorithmIdentifier(),
                new DerBitString(derSig)
            );

            return ToPem(csr.GetDerEncoded(), "CERTIFICATE REQUEST");
        }
        catch (Exception ex)
        {
            throw new ZatcaCryptoException("CSR generation failed", ex);
        }
    }

    /// <summary>
    /// Signs the provided raw bytes (expected to be a pre-computed SHA-256 hash) using
    /// ECDSA secp256k1 and returns a DER-encoded signature.
    /// </summary>
    public byte[] SignData(byte[] dataHash, string privateKeyPem)
    {
        try
        {
            var priv   = DecodePrivateKeyPem(privateKeyPem);
            var signer = new ECDsaSigner(new HMacDsaKCalculator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest()));
            signer.Init(true, new ParametersWithRandom(priv, new SecureRandom()));
            var sig = signer.GenerateSignature(dataHash);
            return EncodeDerSignature(sig[0], sig[1]);
        }
        catch (Exception ex)
        {
            throw new ZatcaCryptoException("Signing failed", ex);
        }
    }

    public string ExtractPublicKeyDerBase64(string certificatePem) =>
        certificatePem
            .Replace("-----BEGIN CERTIFICATE-----", "")
            .Replace("-----END CERTIFICATE-----", "")
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Trim();

    // ────────────────────────────────────────────────────────────────────────────
    // PEM encoding / decoding
    // ────────────────────────────────────────────────────────────────────────────

    private static string EncodePrivateKeyPem(ECPrivateKeyParameters priv)
    {
        var d           = BigIntToFixedBytes(priv.D, 32);
        var pubPoint    = DomainParams.G.Multiply(priv.D).Normalize();
        var pubBytes    = pubPoint.GetEncoded(false);

        // SEC1 ECPrivateKey ::= SEQUENCE {
        //   version INTEGER (1),
        //   privateKey OCTET STRING,
        //   [0] { OID secp256k1 },
        //   [1] { BIT STRING (public key) }
        // }
        var ecPrivKey = new DerSequence(
            new DerInteger(BigInteger.One),
            new DerOctetString(d),
            BuildRawAsn1Object(new byte[] { 0xa0, 0x07, 0x06, 0x05 }.Concat(Secp256k1OidBytes).ToArray()),
            WrapContext1BitString(pubBytes)
        );

        return ToPem(ecPrivKey.GetDerEncoded(), "EC PRIVATE KEY");
    }

    private static string EncodePublicKeyPem(ECPublicKeyParameters pub)
    {
        var pubBytes = pub.Q.GetEncoded(false);
        var spki = new DerSequence(
            EcAlgorithmIdentifier(),
            new DerBitString(pubBytes)
        );
        return ToPem(spki.GetDerEncoded(), "PUBLIC KEY");
    }

    private static ECPrivateKeyParameters DecodePrivateKeyPem(string pem)
    {
        var der   = PemToDer(pem);
        var seq   = (Asn1Sequence)Asn1Object.FromByteArray(der);
        var octet = (Asn1OctetString)seq[1];
        var d     = new BigInteger(1, octet.GetOctets());
        return new ECPrivateKeyParameters(d, DomainParams);
    }

    private static ECPublicKeyParameters DecodePublicKeyPem(string pem)
    {
        var der    = PemToDer(pem);
        var seq    = (Asn1Sequence)Asn1Object.FromByteArray(der);
        var bitStr = (DerBitString)seq[1];
        var raw    = bitStr.GetBytes();
        var point  = DomainParams.Curve.DecodePoint(raw);
        return new ECPublicKeyParameters(point, DomainParams);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // CSR building — mirrors Dart _buildCertReqInfo / _buildSubjectDn exactly
    // ────────────────────────────────────────────────────────────────────────────

    private static Asn1Sequence BuildSubjectDn(ZatcaOnboardingConfig config)
    {
        // Order: CN, C (printable), OU, O
        var cn = config.Environment.IsProduction()
            ? config.OrganizationIdentifier
            : $"TST-{config.OrganizationIdentifier}";

        return new DerSequence(
            RdnAttribute("2.5.4.3",  cn),
            RdnAttribute("2.5.4.6",  config.CountryName,          printable: true),
            RdnAttribute("2.5.4.11", config.OrganizationUnitName),
            RdnAttribute("2.5.4.10", config.OrganizationName)
        );
    }

    private static DerSet RdnAttribute(string oid, string value, bool printable = false)
    {
        Asn1Encodable strVal = printable
            ? new DerPrintableString(value)
            : new DerUtf8String(value);

        return new DerSet(
            new DerSequence(
                new DerObjectIdentifier(oid),
                strVal
            )
        );
    }

    private static Asn1Sequence BuildSpki(ECPublicKeyParameters pub)
    {
        var pubBytes = pub.Q.GetEncoded(false);
        return new DerSequence(
            EcAlgorithmIdentifier(),
            new DerBitString(pubBytes)
        );
    }

    private static Asn1Sequence BuildCertReqInfo(
        Asn1Sequence subjectDn,
        Asn1Sequence spki,
        ZatcaOnboardingConfig config)
    {
        return new DerSequence(
            new DerInteger(0),
            subjectDn,
            spki,
            BuildAttributesWithExtensions(config)
        );
    }

    private static Asn1Encodable BuildAttributesWithExtensions(ZatcaOnboardingConfig config)
    {
        var extsSeq = new DerSequence(
            BuildCertificateTemplateExtension(config.Environment.GetTemplateName()),
            BuildSubjectAltNameExtension(config)
        );

        // extensionRequest OID: 1.2.840.113549.1.9.14
        var extReqAttr = new DerSequence(
            RawOid([0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x09, 0x0e]),
            new DerSet(extsSeq)
        );

        // Wrap in SET and change tag byte from 0x31 to 0xa0 (IMPLICIT [0] CONSTRUCTED)
        var attrSet  = new DerSet(extReqAttr);
        var setBytes = attrSet.GetDerEncoded();
        setBytes[0]  = 0xa0;
        return BuildRawAsn1Object(setBytes);
    }

    private static Asn1Sequence BuildCertificateTemplateExtension(string templateName)
    {
        // certificateTemplateName OID: 1.3.6.1.4.1.311.20.2
        var nameBytes    = Encoding.ASCII.GetBytes(templateName);
        var printableStr = new byte[] { 0x13, (byte)nameBytes.Length }.Concat(nameBytes).ToArray();

        return new DerSequence(
            RawOid([0x2b, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x14, 0x02]),
            new DerOctetString(printableStr)
        );
    }

    private static Asn1Sequence BuildSubjectAltNameExtension(ZatcaOnboardingConfig config)
    {
        // dirName RDNSequence: SN, UID, title, registeredAddress, businessCategory
        var nameSeq = new DerSequence(
            SanRdn([0x55, 0x04, 0x04], config.SerialNumber),
            SanRdn([0x09, 0x92, 0x26, 0x89, 0x93, 0xf2, 0x2c, 0x64, 0x01, 0x01], config.OrganizationIdentifier),
            SanRdn([0x55, 0x04, 0x0c], config.InvoiceType),
            SanRdn([0x55, 0x04, 0x1a], config.RegisteredAddress),
            SanRdn([0x55, 0x04, 0x0f], config.BusinessCategory)
        );

        // directoryName [4] EXPLICIT: 0xa4 tag wrapping the Name SEQUENCE
        var nameEncoded     = nameSeq.GetDerEncoded();
        var directoryName   = DerTlv(0xa4, nameEncoded);
        var generalNames    = new DerSequence(BuildRawAsn1Object(directoryName));

        return new DerSequence(
            RawOid([0x55, 0x1d, 0x11]),   // subjectAltName OID: 2.5.29.17
            new DerOctetString(generalNames.GetDerEncoded())
        );
    }

    private static DerSet SanRdn(byte[] oidBytes, string value)
    {
        return new DerSet(
            new DerSequence(
                RawOid(oidBytes),
                new DerUtf8String(value)
            )
        );
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Algorithm identifiers
    // ────────────────────────────────────────────────────────────────────────────

    private static Asn1Sequence EcAlgorithmIdentifier() =>
        new DerSequence(
            new DerObjectIdentifier("1.2.840.10045.2.1"),  // ecPublicKey
            new DerObjectIdentifier("1.3.132.0.10")        // secp256k1
        );

    private static Asn1Sequence EcdsaSha256AlgorithmIdentifier() =>
        new DerSequence(
            new DerObjectIdentifier("1.2.840.10045.4.3.2") // ecdsa-with-SHA256
        );

    // ────────────────────────────────────────────────────────────────────────────
    // ASN.1 / DER helpers
    // ────────────────────────────────────────────────────────────────────────────

    private static byte[] EncodeDerSignature(BigInteger r, BigInteger s)
    {
        var seq = new DerSequence(new DerInteger(r), new DerInteger(s));
        return seq.GetDerEncoded();
    }

    private static Asn1Object RawOid(byte[] oidContentBytes)
    {
        var tlv = new byte[] { 0x06, (byte)oidContentBytes.Length }.Concat(oidContentBytes).ToArray();
        return Asn1Object.FromByteArray(tlv);
    }

    private static Asn1Object BuildRawAsn1Object(byte[] encoded) =>
        Asn1Object.FromByteArray(encoded);

    private static Asn1Object WrapContext1BitString(byte[] publicKeyBytes)
    {
        var bitStringContent = new byte[] { 0x00 }.Concat(publicKeyBytes).ToArray();
        var bitStringLength  = bitStringContent.Length;
        var outerLength      = bitStringLength + 2;
        var outerBytes       = new byte[] { 0xa1, (byte)outerLength, 0x03, (byte)bitStringLength }
            .Concat(bitStringContent).ToArray();
        return Asn1Object.FromByteArray(outerBytes);
    }

    private static byte[] DerTlv(byte tag, byte[] content)
    {
        var len = content.Length;
        if (len < 128)
            return new byte[] { tag, (byte)len }.Concat(content).ToArray();
        if (len < 256)
            return new byte[] { tag, 0x81, (byte)len }.Concat(content).ToArray();
        return new byte[] { tag, 0x82, (byte)(len >> 8), (byte)(len & 0xff) }.Concat(content).ToArray();
    }

    private static byte[] BigIntToFixedBytes(BigInteger value, int length)
    {
        var bytes = value.ToByteArrayUnsigned();
        if (bytes.Length == length) return bytes;
        if (bytes.Length > length)  return bytes.Skip(bytes.Length - length).ToArray();
        var padded = new byte[length];
        Array.Copy(bytes, 0, padded, length - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] ComputeSha256(byte[] data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return sha256.ComputeHash(data);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // PEM utilities
    // ────────────────────────────────────────────────────────────────────────────

    private static string ToPem(byte[] der, string label)
    {
        var b64   = Convert.ToBase64String(der);
        var lines = Enumerable.Range(0, (b64.Length + 63) / 64)
                              .Select(i => b64.Substring(i * 64, Math.Min(64, b64.Length - i * 64)));
        return $"-----BEGIN {label}-----\n{string.Join("\n", lines)}\n-----END {label}-----\n";
    }

    private static byte[] PemToDer(string pem)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(pem, @"-----[^-]+-----", "")
                      .Replace("\n", "").Replace("\r", "").Trim();
        return Convert.FromBase64String(cleaned);
    }
}
