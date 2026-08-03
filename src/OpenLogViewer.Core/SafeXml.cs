using System.Xml;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// Reading XML that came from somewhere else.
///
/// Every XML this application parses arrives in a file someone was given: a
/// tune emailed by a tuner, a datalog downloaded from a forum, an .mlg with a
/// tune embedded inside it. None of it is this application's own output, so
/// none of it can be assumed well-meaning.
///
/// <see cref="XDocument.Parse(string)"/> is not the right tool for that. It does
/// refuse to fetch external entities — a document naming
/// <c>file:///c:/…/passwords.txt</c> gets nothing, which was measured rather
/// than assumed — but it expands entities declared inside the document, and
/// that is enough on its own:
///
/// <code>
///   &lt;!ENTITY a "aaaaaaaaaa"&gt;
///   &lt;!ENTITY b "&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;&amp;a;"&gt;   …and so on
/// </code>
///
/// Ten of those nested is a gigabyte from a file of a few hundred bytes. The
/// three-deep version above really did expand to 10,199 characters here, so the
/// deeper one is arithmetic rather than speculation.
///
/// Ignoring the document type definition rather than prohibiting it, which is
/// the usual advice, because the usual advice costs more than it needs to here:
/// prohibiting rejects any file carrying a &lt;!DOCTYPE&gt; at all, including the
/// harmless ones some writers emit, while ignoring loads those perfectly and
/// still leaves a bomb's entities undeclared and its parse refused. Both were
/// checked; only the second reads every legitimate file tried.
/// </summary>
public static class SafeXml
{
    /// <summary>Reads a document, or throws <see cref="XmlException"/>.</summary>
    public static XDocument Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        using var reader = XmlReader.Create(new StringReader(xml), Settings);

        return XDocument.Load(reader);
    }

    private static XmlReaderSettings Settings => new()
    {
        // No entity expansion and no document type definition, so nothing in the
        // file can decide how much memory reading it takes.
        DtdProcessing = DtdProcessing.Ignore,

        // Belt and braces: with the definition ignored there is nothing left to
        // resolve, but a null resolver means a document cannot reach the disk or
        // the network even if that changes.
        XmlResolver = null,

        // The application has no use for either, and both are places where a
        // reader can be asked to do work out of proportion to the file.
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
    };
}
