using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Girix.Zatca.Services;

/// <summary>
/// Computes the ZATCA invoice hash per the Phase 2 spec:
/// removes UBLExtensions, Signature, and QR AdditionalDocumentReference,
/// then SHA-256-hashes the serialized root element (no XML declaration).
/// </summary>
public class ZatcaInvoiceHasher
{
    private const string NsCac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private const string NsCbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string NsExt = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

    public string HashInvoice(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
        doc.LoadXml(xml);

        var root  = doc.DocumentElement!;
        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        nsMgr.AddNamespace("ext", NsExt);
        nsMgr.AddNamespace("cac", NsCac);
        nsMgr.AddNamespace("cbc", NsCbc);

        // Remove ext:UBLExtensions
        RemoveChildren(root, root.SelectNodes("ext:UBLExtensions", nsMgr));

        // Remove cac:Signature
        RemoveChildren(root, root.SelectNodes("cac:Signature", nsMgr));

        // Remove QR AdditionalDocumentReference
        var qrNodes = new List<XmlNode>();
        var refs = root.SelectNodes("cac:AdditionalDocumentReference", nsMgr);
        if (refs != null)
        {
            foreach (XmlNode node in refs)
            {
                var idNode = node.SelectSingleNode("cbc:ID", nsMgr);
                if (idNode?.InnerText.Trim() == "QR")
                    qrNodes.Add(node);
            }
        }
        foreach (var node in qrNodes)
            root.RemoveChild(node);

        // Serialize root element only (no XML declaration) — matches C14N output
        var sb      = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration  = true,
            Indent              = false,
            Encoding            = Encoding.UTF8
        };
        using (var writer = XmlWriter.Create(sb, settings))
            root.WriteTo(writer);

        var canonical = sb.ToString();
        var bytes     = Encoding.UTF8.GetBytes(canonical);
        var hash      = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private static void RemoveChildren(XmlElement parent, XmlNodeList? nodes)
    {
        if (nodes == null) return;
        var list = nodes.Cast<XmlNode>().ToList();
        foreach (var node in list)
            parent.RemoveChild(node);
    }
}
