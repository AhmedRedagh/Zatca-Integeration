using System.Text;
using System.Xml;
using System.Xml.Linq;
using Girix.Zatca.Enums;
using Girix.Zatca.Exceptions;
using Girix.Zatca.Models;

namespace Girix.Zatca.Services;

/// <summary>
/// Builds ZATCA-compliant UBL 2.1 invoice XML for standard, simplified, credit, and debit invoices.
/// Mirrors the Dart ZatcaXmlBuilderImpl structure and element ordering exactly.
/// </summary>
public class ZatcaXmlBuilder
{
    private const string NsUbl = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private const string NsCac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private const string NsCbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string NsExt = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private const string NsXsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string EmptySig = "NWZlY2ViNjZmZmM4NmYzMDY5NTkwN2I1ZWIzZWYxZQ==";

    public string BuildStandardInvoice(ZatcaInvoice invoice)   => BuildInvoice(invoice, "0100000");
    public string BuildSimplifiedInvoice(ZatcaInvoice invoice) => BuildInvoice(invoice, "0200000");
    public string BuildCreditNote(ZatcaInvoice invoice)        => BuildInvoice(invoice, "0100000", "381");
    public string BuildDebitNote(ZatcaInvoice invoice)         => BuildInvoice(invoice, "0100000", "383");

    private static string BuildInvoice(ZatcaInvoice invoice, string subtype, string typeCode = "388")
    {
        try
        {
            var dateStr    = invoice.InvoiceDate.ToString("yyyy-MM-dd");
            var timeStr    = invoice.InvoiceDate.ToString("HH:mm:ss");
            var payCode    = PaymentCode(invoice.PaymentMeans);
            var vatRate    = invoice.Lines.Count > 0 ? invoice.Lines[0].VatRate * 100 : 15.0;
            var previousHash = invoice.PreviousInvoiceHash ?? EmptySig;

            var doc = new XmlDocument { XmlResolver = null };
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));

            var root = doc.CreateElement("Invoice", NsUbl);
            root.SetAttribute("xmlns:cac", NsCac);
            root.SetAttribute("xmlns:cbc", NsCbc);
            root.SetAttribute("xmlns:ext", NsExt);
            root.SetAttribute("xmlns:xsi", NsXsi);
            doc.AppendChild(root);

            // ext:UBLExtensions
            AppendUblExtensions(doc, root);

            // Header fields
            root.AppendChild(Elem(doc, NsCbc, "cbc:ProfileID", "reporting:1.0"));
            root.AppendChild(Elem(doc, NsCbc, "cbc:ID",           invoice.InvoiceNumber));
            root.AppendChild(Elem(doc, NsCbc, "cbc:UUID",         invoice.Uuid));
            root.AppendChild(Elem(doc, NsCbc, "cbc:IssueDate",    dateStr));
            root.AppendChild(Elem(doc, NsCbc, "cbc:IssueTime",    timeStr));

            var typeCodeElem = Elem(doc, NsCbc, "cbc:InvoiceTypeCode", typeCode);
            typeCodeElem.SetAttribute("name", subtype);
            root.AppendChild(typeCodeElem);

            root.AppendChild(Elem(doc, NsCbc, "cbc:DocumentCurrencyCode", "SAR"));
            root.AppendChild(Elem(doc, NsCbc, "cbc:TaxCurrencyCode",      "SAR"));

            // ICV reference
            root.AppendChild(IcvRef(doc, invoice.InvoiceCounterValue));
            // PIH reference
            root.AppendChild(PihRef(doc, previousHash));
            // QR placeholder
            root.AppendChild(QrRef(doc));
            // Signature reference
            root.AppendChild(SignatureRef(doc));

            // Parties
            root.AppendChild(SupplierParty(doc, invoice.Seller));
            root.AppendChild(CustomerParty(doc, invoice.Buyer));

            // Delivery
            root.AppendChild(Delivery(doc, dateStr));

            // Payment
            root.AppendChild(PaymentMeansElem(doc, payCode));

            // Allowance
            if (invoice.AllowanceAmount > 0)
                root.AppendChild(AllowanceCharge(doc, invoice));

            // Tax totals
            TaxTotal(doc, root, invoice, vatRate);

            // Monetary total
            root.AppendChild(LegalMonetaryTotal(doc, invoice));

            // Lines
            foreach (var line in invoice.Lines)
                root.AppendChild(InvoiceLine(doc, line));

            return doc.OuterXml;
        }
        catch (Exception ex)
        {
            throw new ZatcaXmlException("Invoice XML build failed", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Signature + QR embedding
    // ────────────────────────────────────────────────────────────────────────────

    public string EmbedSignatureAndQr(
        string xml,
        string invoiceHash,
        string signatureBase64,
        string qrBase64,
        string certificatePem)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);

            var nsMgr = BuildNsMgr(doc);
            var root  = doc.DocumentElement!;

            // Clean cert
            var cleanedCert = certificatePem
                .Replace("-----BEGIN CERTIFICATE-----", "")
                .Replace("-----END CERTIFICATE-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();

            // Build signature block XML
            var sigXml = $@"<sig:UBLDocumentSignatures
      xmlns:ds=""http://www.w3.org/2000/09/xmldsig#""
      xmlns:xades=""http://uri.etsi.org/01903/v1.3.2#""
      xmlns:sig=""urn:oasis:names:specification:ubl:schema:xsd:CommonSignatureComponents-2""
      xmlns:sac=""urn:oasis:names:specification:ubl:schema:xsd:SignatureAggregateComponents-2""
      xmlns:sbc=""urn:oasis:names:specification:ubl:schema:xsd:SignatureBasicComponents-2"">
  <sac:SignatureInformation>
    <cbc:ID xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"">urn:oasis:names:specification:ubl:signature:1</cbc:ID>
    <sbc:ReferencedSignatureID>urn:oasis:names:specification:ubl:signature:Invoice</sbc:ReferencedSignatureID>
    <ds:Signature Id=""signature"">
      <ds:SignedInfo>
        <ds:CanonicalizationMethod Algorithm=""http://www.w3.org/2006/12/xml-c14n11""/>
        <ds:SignatureMethod Algorithm=""http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256""/>
        <ds:Reference Id=""invoiceSignedData"" URI="""">
          <ds:Transforms>
            <ds:Transform Algorithm=""http://www.w3.org/TR/1999/REC-xpath-19991116"">
              <ds:XPath>not(//ancestor-or-self::ext:UBLExtensions)</ds:XPath>
            </ds:Transform>
            <ds:Transform Algorithm=""http://www.w3.org/TR/1999/REC-xpath-19991116"">
              <ds:XPath>not(//ancestor-or-self::cac:Signature)</ds:XPath>
            </ds:Transform>
            <ds:Transform Algorithm=""http://www.w3.org/TR/1999/REC-xpath-19991116"">
              <ds:XPath>not(//ancestor-or-self::cac:AdditionalDocumentReference[cac:Attachment/cbc:EmbeddedDocumentBinaryObject])</ds:XPath>
            </ds:Transform>
            <ds:Transform Algorithm=""http://www.w3.org/2006/12/xml-c14n11""/>
          </ds:Transforms>
          <ds:DigestMethod Algorithm=""http://www.w3.org/2001/04/xmlenc#sha256""/>
          <ds:DigestValue>{invoiceHash}</ds:DigestValue>
        </ds:Reference>
      </ds:SignedInfo>
      <ds:SignatureValue>{signatureBase64}</ds:SignatureValue>
      <ds:KeyInfo>
        <ds:X509Data>
          <ds:X509Certificate>{cleanedCert}</ds:X509Certificate>
        </ds:X509Data>
      </ds:KeyInfo>
    </ds:Signature>
  </sac:SignatureInformation>
</sig:UBLDocumentSignatures>";

            // Find ext:ExtensionContent and replace its content
            var extContent = root.SelectSingleNode(
                "ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent", nsMgr);

            if (extContent != null)
            {
                extContent.InnerXml = "";
                var sigDoc = new XmlDocument { XmlResolver = null };
                sigDoc.LoadXml(sigXml);
                var imported = doc.ImportNode(sigDoc.DocumentElement!, true);
                extContent.AppendChild(imported);
            }

            // Update QR placeholder
            var qrRefs = root.SelectNodes("cac:AdditionalDocumentReference", nsMgr);
            if (qrRefs != null)
            {
                foreach (XmlNode refNode in qrRefs)
                {
                    var idNode = refNode.SelectSingleNode("cbc:ID", nsMgr);
                    if (idNode?.InnerText.Trim() == "QR")
                    {
                        var embObj = refNode.SelectSingleNode(
                            "cac:Attachment/cbc:EmbeddedDocumentBinaryObject", nsMgr);
                        if (embObj != null)
                            embObj.InnerText = qrBase64;
                        break;
                    }
                }
            }

            return doc.OuterXml;
        }
        catch (Exception ex)
        {
            throw new ZatcaXmlException("Signature embedding failed", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Element builders
    // ────────────────────────────────────────────────────────────────────────────

    private static void AppendUblExtensions(XmlDocument doc, XmlElement root)
    {
        var exts   = doc.CreateElement("ext:UBLExtensions", NsExt);
        var ext    = doc.CreateElement("ext:UBLExtension",  NsExt);
        var uri    = doc.CreateElement("ext:ExtensionURI",  NsExt);
        uri.InnerText = "urn:oasis:names:specification:ubl:dsig:ext:1.0";
        var content = doc.CreateElement("ext:ExtensionContent", NsExt);
        ext.AppendChild(uri);
        ext.AppendChild(content);
        exts.AppendChild(ext);
        root.AppendChild(exts);
    }

    private static XmlElement IcvRef(XmlDocument doc, int icv)
    {
        var r  = doc.CreateElement("cac:AdditionalDocumentReference", NsCac);
        r.AppendChild(Elem(doc, NsCbc, "cbc:ID",   "ICV"));
        r.AppendChild(Elem(doc, NsCbc, "cbc:UUID", icv.ToString()));
        return r;
    }

    private static XmlElement PihRef(XmlDocument doc, string previousHash)
    {
        var r          = doc.CreateElement("cac:AdditionalDocumentReference", NsCac);
        var attachment = doc.CreateElement("cac:Attachment", NsCac);
        var embedded   = doc.CreateElement("cbc:EmbeddedDocumentBinaryObject", NsCbc);
        embedded.SetAttribute("mimeCode", "text/plain");
        embedded.InnerText = previousHash;
        attachment.AppendChild(embedded);
        r.AppendChild(Elem(doc, NsCbc, "cbc:ID", "PIH"));
        r.AppendChild(attachment);
        return r;
    }

    private static XmlElement QrRef(XmlDocument doc)
    {
        var r          = doc.CreateElement("cac:AdditionalDocumentReference", NsCac);
        var attachment = doc.CreateElement("cac:Attachment", NsCac);
        var embedded   = doc.CreateElement("cbc:EmbeddedDocumentBinaryObject", NsCbc);
        embedded.SetAttribute("mimeCode", "text/plain");
        embedded.InnerText = "";
        attachment.AppendChild(embedded);
        r.AppendChild(Elem(doc, NsCbc, "cbc:ID", "QR"));
        r.AppendChild(attachment);
        return r;
    }

    private static XmlElement SignatureRef(XmlDocument doc)
    {
        var r = doc.CreateElement("cac:Signature", NsCac);
        r.AppendChild(Elem(doc, NsCbc, "cbc:ID",              "urn:oasis:names:specification:ubl:signature:Invoice"));
        r.AppendChild(Elem(doc, NsCbc, "cbc:SignatureMethod", "urn:oasis:names:specification:ubl:dsig:enveloped:xades"));
        return r;
    }

    private static XmlElement SupplierParty(XmlDocument doc, ZatcaSeller s)
    {
        var supplierParty = doc.CreateElement("cac:AccountingSupplierParty", NsCac);
        var party         = doc.CreateElement("cac:Party", NsCac);

        // PartyIdentification
        var partyId = doc.CreateElement("cac:PartyIdentification", NsCac);
        var crId    = Elem(doc, NsCbc, "cbc:ID", s.CrNumber);
        crId.SetAttribute("schemeID", "CRN");
        partyId.AppendChild(crId);
        party.AppendChild(partyId);

        // PostalAddress
        var addr = doc.CreateElement("cac:PostalAddress", NsCac);
        addr.AppendChild(Elem(doc, NsCbc, "cbc:StreetName",     s.StreetName));
        addr.AppendChild(Elem(doc, NsCbc, "cbc:BuildingNumber", s.BuildingNumber));
        if (!string.IsNullOrEmpty(s.DistrictName))
            addr.AppendChild(Elem(doc, NsCbc, "cbc:CitySubdivisionName", s.DistrictName));
        addr.AppendChild(Elem(doc, NsCbc, "cbc:CityName",  s.CityName));
        addr.AppendChild(Elem(doc, NsCbc, "cbc:PostalZone", s.PostalCode));
        var country = doc.CreateElement("cac:Country", NsCac);
        country.AppendChild(Elem(doc, NsCbc, "cbc:IdentificationCode", s.CountryCode));
        addr.AppendChild(country);
        party.AppendChild(addr);

        // PartyTaxScheme
        var taxScheme = doc.CreateElement("cac:PartyTaxScheme", NsCac);
        taxScheme.AppendChild(Elem(doc, NsCbc, "cbc:CompanyID", s.VatRegistrationNumber));
        var ts = doc.CreateElement("cac:TaxScheme", NsCac);
        ts.AppendChild(Elem(doc, NsCbc, "cbc:ID", "VAT"));
        taxScheme.AppendChild(ts);
        party.AppendChild(taxScheme);

        // PartyLegalEntity
        var legal = doc.CreateElement("cac:PartyLegalEntity", NsCac);
        legal.AppendChild(Elem(doc, NsCbc, "cbc:RegistrationName", s.SellerName));
        party.AppendChild(legal);

        supplierParty.AppendChild(party);
        return supplierParty;
    }

    private static XmlElement CustomerParty(XmlDocument doc, ZatcaBuyer b)
    {
        var customerParty = doc.CreateElement("cac:AccountingCustomerParty", NsCac);
        var party         = doc.CreateElement("cac:Party", NsCac);

        if (!string.IsNullOrEmpty(b.StreetName))
        {
            var addr = doc.CreateElement("cac:PostalAddress", NsCac);
            addr.AppendChild(Elem(doc, NsCbc, "cbc:StreetName", b.StreetName));
            if (!string.IsNullOrEmpty(b.BuildingNumber))
                addr.AppendChild(Elem(doc, NsCbc, "cbc:BuildingNumber", b.BuildingNumber));
            if (!string.IsNullOrEmpty(b.DistrictName))
                addr.AppendChild(Elem(doc, NsCbc, "cbc:CitySubdivisionName", b.DistrictName));
            if (!string.IsNullOrEmpty(b.CityName))
                addr.AppendChild(Elem(doc, NsCbc, "cbc:CityName", b.CityName));
            if (!string.IsNullOrEmpty(b.PostalCode))
                addr.AppendChild(Elem(doc, NsCbc, "cbc:PostalZone", b.PostalCode));
            var country = doc.CreateElement("cac:Country", NsCac);
            country.AppendChild(Elem(doc, NsCbc, "cbc:IdentificationCode", b.CountryCode));
            addr.AppendChild(country);
            party.AppendChild(addr);
        }

        if (!string.IsNullOrEmpty(b.VatRegistrationNumber))
        {
            var taxScheme = doc.CreateElement("cac:PartyTaxScheme", NsCac);
            taxScheme.AppendChild(Elem(doc, NsCbc, "cbc:CompanyID", b.VatRegistrationNumber));
            var ts = doc.CreateElement("cac:TaxScheme", NsCac);
            ts.AppendChild(Elem(doc, NsCbc, "cbc:ID", "VAT"));
            taxScheme.AppendChild(ts);
            party.AppendChild(taxScheme);
        }

        var legal = doc.CreateElement("cac:PartyLegalEntity", NsCac);
        legal.AppendChild(Elem(doc, NsCbc, "cbc:RegistrationName", b.BuyerName));
        party.AppendChild(legal);

        customerParty.AppendChild(party);
        return customerParty;
    }

    private static XmlElement Delivery(XmlDocument doc, string dateStr)
    {
        var delivery = doc.CreateElement("cac:Delivery", NsCac);
        delivery.AppendChild(Elem(doc, NsCbc, "cbc:ActualDeliveryDate", dateStr));
        return delivery;
    }

    private static XmlElement PaymentMeansElem(XmlDocument doc, string payCode)
    {
        var pm = doc.CreateElement("cac:PaymentMeans", NsCac);
        pm.AppendChild(Elem(doc, NsCbc, "cbc:PaymentMeansCode", payCode));
        return pm;
    }

    private static XmlElement AllowanceCharge(XmlDocument doc, ZatcaInvoice invoice)
    {
        var ac = doc.CreateElement("cac:AllowanceCharge", NsCac);
        ac.AppendChild(Elem(doc, NsCbc, "cbc:ChargeIndicator",       "false"));
        ac.AppendChild(Elem(doc, NsCbc, "cbc:AllowanceChargeReason", "discount"));

        var amt = Elem(doc, NsCbc, "cbc:Amount", invoice.AllowanceAmount.ToString("F2"));
        amt.SetAttribute("currencyID", "SAR");
        ac.AppendChild(amt);

        var taxCat = doc.CreateElement("cac:TaxCategory", NsCac);
        taxCat.AppendChild(Elem(doc, NsCbc, "cbc:ID",      "S"));
        taxCat.AppendChild(Elem(doc, NsCbc, "cbc:Percent", "15"));
        var ts = doc.CreateElement("cac:TaxScheme", NsCac);
        ts.AppendChild(Elem(doc, NsCbc, "cbc:ID", "VAT"));
        taxCat.AppendChild(ts);
        ac.AppendChild(taxCat);
        return ac;
    }

    private static void TaxTotal(XmlDocument doc, XmlElement root, ZatcaInvoice invoice, double vatRate)
    {
        // First TaxTotal with subtotals
        var tt1 = doc.CreateElement("cac:TaxTotal", NsCac);
        var ta1 = Elem(doc, NsCbc, "cbc:TaxAmount", invoice.VatAmount.ToString("F2"));
        ta1.SetAttribute("currencyID", "SAR");
        tt1.AppendChild(ta1);

        var subtotal = doc.CreateElement("cac:TaxSubtotal", NsCac);
        var taxableAmt = Elem(doc, NsCbc, "cbc:TaxableAmount", invoice.TaxableAmount.ToString("F2"));
        taxableAmt.SetAttribute("currencyID", "SAR");
        subtotal.AppendChild(taxableAmt);
        var taxAmt = Elem(doc, NsCbc, "cbc:TaxAmount", invoice.VatAmount.ToString("F2"));
        taxAmt.SetAttribute("currencyID", "SAR");
        subtotal.AppendChild(taxAmt);

        var taxCat = doc.CreateElement("cac:TaxCategory", NsCac);
        taxCat.AppendChild(Elem(doc, NsCbc, "cbc:ID",      "S"));
        taxCat.AppendChild(Elem(doc, NsCbc, "cbc:Percent", vatRate.ToString("F2")));
        var ts1 = doc.CreateElement("cac:TaxScheme", NsCac);
        ts1.AppendChild(Elem(doc, NsCbc, "cbc:ID", "VAT"));
        taxCat.AppendChild(ts1);
        subtotal.AppendChild(taxCat);
        tt1.AppendChild(subtotal);
        root.AppendChild(tt1);

        // Second TaxTotal (summary only)
        var tt2 = doc.CreateElement("cac:TaxTotal", NsCac);
        var ta2 = Elem(doc, NsCbc, "cbc:TaxAmount", invoice.VatAmount.ToString("F2"));
        ta2.SetAttribute("currencyID", "SAR");
        tt2.AppendChild(ta2);
        root.AppendChild(tt2);
    }

    private static XmlElement LegalMonetaryTotal(XmlDocument doc, ZatcaInvoice invoice)
    {
        var lmt = doc.CreateElement("cac:LegalMonetaryTotal", NsCac);

        XmlElement AmtElem(string tag, double val)
        {
            var e = Elem(doc, NsCbc, tag, val.ToString("F2"));
            e.SetAttribute("currencyID", "SAR");
            return e;
        }

        lmt.AppendChild(AmtElem("cbc:LineExtensionAmount", invoice.LineExtensionAmount));
        lmt.AppendChild(AmtElem("cbc:TaxExclusiveAmount",  invoice.TaxableAmount));
        lmt.AppendChild(AmtElem("cbc:TaxInclusiveAmount",  invoice.PayableAmount));
        lmt.AppendChild(AmtElem("cbc:AllowanceTotalAmount", invoice.AllowanceAmount));
        lmt.AppendChild(AmtElem("cbc:PrepaidAmount",       0.0));
        lmt.AppendChild(AmtElem("cbc:PayableAmount",       invoice.PayableAmount));
        return lmt;
    }

    private static XmlElement InvoiceLine(XmlDocument doc, ZatcaInvoiceLine line)
    {
        var il = doc.CreateElement("cac:InvoiceLine", NsCac);
        il.AppendChild(Elem(doc, NsCbc, "cbc:ID", line.LineId.ToString()));

        var qty = Elem(doc, NsCbc, "cbc:InvoicedQuantity", line.Quantity.ToString("F2"));
        qty.SetAttribute("unitCode", line.UnitCode);
        il.AppendChild(qty);

        var lea = Elem(doc, NsCbc, "cbc:LineExtensionAmount", line.LineExtensionAmount.ToString("F2"));
        lea.SetAttribute("currencyID", "SAR");
        il.AppendChild(lea);

        var taxTotal = doc.CreateElement("cac:TaxTotal", NsCac);
        var ta = Elem(doc, NsCbc, "cbc:TaxAmount", line.VatAmount.ToString("F2"));
        ta.SetAttribute("currencyID", "SAR");
        taxTotal.AppendChild(ta);
        var ra = Elem(doc, NsCbc, "cbc:RoundingAmount", line.LineTotal.ToString("F2"));
        ra.SetAttribute("currencyID", "SAR");
        taxTotal.AppendChild(ra);
        il.AppendChild(taxTotal);

        var item = doc.CreateElement("cac:Item", NsCac);
        item.AppendChild(Elem(doc, NsCbc, "cbc:Name", line.ProductName));
        var cls = doc.CreateElement("cac:ClassifiedTaxCategory", NsCac);
        cls.AppendChild(Elem(doc, NsCbc, "cbc:ID",      line.VatCategoryCode));
        cls.AppendChild(Elem(doc, NsCbc, "cbc:Percent", (line.VatRate * 100).ToString("F2")));
        var ts = doc.CreateElement("cac:TaxScheme", NsCac);
        ts.AppendChild(Elem(doc, NsCbc, "cbc:ID", "VAT"));
        cls.AppendChild(ts);
        item.AppendChild(cls);
        il.AppendChild(item);

        var price = doc.CreateElement("cac:Price", NsCac);
        var pa = Elem(doc, NsCbc, "cbc:PriceAmount", line.UnitPrice.ToString("F6"));
        pa.SetAttribute("currencyID", "SAR");
        price.AppendChild(pa);
        var bq = Elem(doc, NsCbc, "cbc:BaseQuantity", "1");
        bq.SetAttribute("unitCode", line.UnitCode);
        price.AppendChild(bq);
        il.AppendChild(price);

        return il;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────

    private static XmlElement Elem(XmlDocument doc, string ns, string qualifiedName, string text)
    {
        var e = doc.CreateElement(qualifiedName, ns);
        e.InnerText = text;
        return e;
    }

    private static string PaymentCode(ZatcaPaymentMeans means) => means switch
    {
        ZatcaPaymentMeans.Cash   => "10",
        ZatcaPaymentMeans.Credit => "30",
        ZatcaPaymentMeans.Bank   => "42",
        _                        => "10"
    };

    private static XmlNamespaceManager BuildNsMgr(XmlDocument doc)
    {
        var mgr = new XmlNamespaceManager(doc.NameTable);
        mgr.AddNamespace("ext", NsExt);
        mgr.AddNamespace("cac", NsCac);
        mgr.AddNamespace("cbc", NsCbc);
        mgr.AddNamespace("sig", "urn:oasis:names:specification:ubl:schema:xsd:CommonSignatureComponents-2");
        return mgr;
    }
}
