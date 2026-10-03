using System.Xml.Linq;

namespace SDS200_ControlApp.Protocol;

public sealed record ScannerXmlNode(string Label, IReadOnlyList<ScannerXmlNode> Children);

public static class ScannerXmlTreeParser
{
    public static ScannerXmlNode Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(xml.TrimEnd('\r', '\n'));
        return document.Root is null
            ? throw new System.Xml.XmlException("The XML document has no root element.")
            : ParseElement(document.Root);
    }

    private static ScannerXmlNode ParseElement(XElement element)
    {
        var attributes = element.Attributes()
            .Select(attribute => $" @{attribute.Name.LocalName}={attribute.Value}");
        var childElements = element.Elements().ToArray();
        var text = childElements.Length == 0 ? element.Value.Trim() : string.Empty;
        var label = $"<{element.Name.LocalName}{string.Concat(attributes)}>{(text.Length > 0 ? $" {text}" : string.Empty)}";
        return new ScannerXmlNode(label, childElements.Select(ParseElement).ToArray());
    }
}