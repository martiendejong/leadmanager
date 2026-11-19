using System.Xml;
using System.Xml.Linq;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// The only way the hunt and the enrichment read an XML document from the web (a sitemap). XDocument.Parse accepts a DOCTYPE with entity
/// declarations (a "billion laughs" document expands a few kilobytes into gigabytes) and any nesting depth. Here a DOCTYPE is refused, no
/// resolver is used, the document size is capped and the nesting depth and node count are checked in a first pass, before the document is loaded.
/// </summary>
public static class SafeXml
{
    public const int MaxCharacters = 5_000_000;
    public const int MaxDepth = 64;
    public const int MaxNodes = 500_000;

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,   // a DOCTYPE (entities, external subsets) makes the reader throw
        XmlResolver = null,                        // nothing is ever fetched
        MaxCharactersFromEntities = 1_000,
        MaxCharactersInDocument = MaxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>The document, or null when it is empty, too large, too deep, has a DOCTYPE or is not well formed.</summary>
    public static XDocument? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxCharacters) return null;
        try
        {
            using (var probe = XmlReader.Create(new StringReader(xml), Settings()))
            {
                var nodes = 0;
                while (probe.Read())
                    if (probe.Depth > MaxDepth || ++nodes > MaxNodes) return null;
            }
            using var reader = XmlReader.Create(new StringReader(xml), Settings());
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException) { return null; }
    }
}
