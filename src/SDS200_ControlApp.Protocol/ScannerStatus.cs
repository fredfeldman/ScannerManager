using System.Globalization;
using System.Xml.Linq;

namespace SDS200_ControlApp.Protocol;

public sealed record ScannerInfo(
    string RawXml,
    string? Mode,
    string? Frequency,
    string? System,
    string? Department,
    string? Site,
    string? Channel,
    int? DatabaseCounter);

public static class ScannerInfoParser
{
    public static ScannerInfo Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(xml.TrimEnd('\r', '\n'));
        if (!string.Equals(document.Root?.Name.LocalName, "ScannerInfo", StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Xml.XmlException("The XML document root must be ScannerInfo.");
        }

        return new ScannerInfo(
            xml,
            FindValue(document, "Mode", "CurrentMode"),
            FindValue(document, "Frequency", "CurrentFrequency", "Freq"),
            FindValue(document, "System", "CurrentSystem"),
            FindValue(document, "Department", "CurrentDepartment"),
            FindValue(document, "Site", "CurrentSite"),
            FindValue(document, "Channel", "CurrentChannel"),
            FindInteger(document, "DB_Counter", "DatabaseCounter"));
    }

    private static string? FindValue(XDocument document, params string[] names)
    {
        return document
            .Descendants()
            .Where(element => names.Any(name => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)))
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static int? FindInteger(XDocument document, params string[] names)
    {
        var value = FindValue(document, names);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new System.Xml.XmlException($"The {names[0]} value is not a valid integer.");
    }
}
