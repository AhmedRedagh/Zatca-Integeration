using System.Text;
using Girix.Zatca.Models;

namespace Girix.Zatca.Services;

/// <summary>
/// Encodes a ZATCA Phase 2 QR code as TLV (Tag-Length-Value) base64.
/// Tags: 1=SellerName, 2=VatNumber, 3=Timestamp, 4=Total, 5=VAT,
///       6=InvoiceHash, 7=Signature(raw bytes), 8=PublicKey(raw EC point).
/// </summary>
public class ZatcaQrEncoder
{
    public string Encode(
        ZatcaInvoice invoice,
        string signatureBase64,
        string certificatePem,
        string invoiceHash)
    {
        var timestamp  = invoice.InvoiceDate.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                         .Replace("'T'", "T").Replace("'Z'", "Z");
        var totalStr   = invoice.PayableAmount.ToString("F2");
        var vatStr     = invoice.VatAmount.ToString("F2");
        var sigBytes   = Convert.FromBase64String(signatureBase64);
        var pubBytes   = ExtractEcPublicKey(certificatePem);

        var tlv = new List<byte>();
        tlv.AddRange(TlvField(1, Encoding.UTF8.GetBytes(invoice.Seller.SellerName)));
        tlv.AddRange(TlvField(2, Encoding.UTF8.GetBytes(invoice.Seller.VatRegistrationNumber)));
        tlv.AddRange(TlvField(3, Encoding.UTF8.GetBytes(timestamp)));
        tlv.AddRange(TlvField(4, Encoding.UTF8.GetBytes(totalStr)));
        tlv.AddRange(TlvField(5, Encoding.UTF8.GetBytes(vatStr)));
        tlv.AddRange(TlvField(6, Encoding.UTF8.GetBytes(invoiceHash)));
        tlv.AddRange(TlvField(7, sigBytes));
        tlv.AddRange(TlvField(8, pubBytes));

        return Convert.ToBase64String(tlv.ToArray());
    }

    private static IEnumerable<byte> TlvField(byte tag, byte[] value) =>
        new byte[] { tag, (byte)(value.Length & 0xFF) }.Concat(value);

    private static byte[] ExtractEcPublicKey(string pem)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(pem, @"-----[^-]+-----", "")
                      .Replace("\n", "").Replace("\r", "").Trim();

        if (string.IsNullOrEmpty(cleaned))
            return new byte[65];

        try
        {
            var der = Convert.FromBase64String(cleaned);

            // Search DER for BIT STRING (0x03) containing uncompressed EC point (0x04)
            for (int i = 0; i < der.Length - 4; i++)
            {
                if (der[i] != 0x03) continue;

                int len, dataOffset;
                if (der[i + 1] < 0x80)
                {
                    len        = der[i + 1];
                    dataOffset = i + 2;
                }
                else if (der[i + 1] == 0x81)
                {
                    len        = der[i + 2];
                    dataOffset = i + 3;
                }
                else continue;

                if (dataOffset + len > der.Length) continue;
                if (der[dataOffset]     != 0x00) continue;  // unused bits
                if (der[dataOffset + 1] != 0x04) continue;  // uncompressed EC point

                var pointLen = len - 1;
                if (pointLen == 65 || pointLen == 97)
                    return der.Skip(dataOffset + 1).Take(pointLen).ToArray();
            }
        }
        catch { /* fall through */ }

        return new byte[65]; // zero placeholder on parse failure
    }
}
