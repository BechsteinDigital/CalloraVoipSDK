using System.Xml.Linq;

namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>
/// A presence document as a watcher receives it (PIDF, RFC 3863).
/// </summary>
/// <remarks>
/// <para>
/// Parsed here rather than in every application, for the same reason <c>Diversion</c> and
/// <c>History-Info</c> are: the document is small, the rules are in an RFC, and two consumers reading
/// the same XML will disagree in different ways. What the SDK does not do is interpret it — whether
/// "open with a note" means available to <em>you</em> is a policy question about your product.
/// </para>
/// <para>
/// <b>Namespace-tolerant on purpose.</b> The RFC fixes the namespace, and deployments do not: some
/// registrars send PIDF without one, others with a default that differs by a trailing slash. Matching
/// on the local name accepts all of them, and the cost is accepting a document that was not PIDF —
/// which then produces no tuples and reads as "nothing known", the same as an empty one.
/// </para>
/// </remarks>
public sealed class SipPresence
{
    private SipPresence(string? entity, IReadOnlyList<SipPresenceTuple> tuples)
    {
        Entity = entity;
        Tuples = tuples;
    }

    /// <summary>Who the document is about, from the <c>entity</c> attribute.</summary>
    public string? Entity { get; }

    /// <summary>The reported states. A document may carry several, one per device or service.</summary>
    public IReadOnlyList<SipPresenceTuple> Tuples { get; }

    /// <summary>
    /// Whether anything in this document reports itself as reachable.
    /// </summary>
    /// <remarks>
    /// Any open tuple counts. A person with a desk phone offline and a mobile online is reachable, and
    /// requiring all of them to be open would report the opposite.
    /// </remarks>
    public bool IsOpen => Tuples.Any(tuple => tuple.IsOpen);

    /// <summary>
    /// Reads a PIDF document, or returns null when it is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: this runs on a NOTIFY from another party's registrar, and a
    /// malformed document from somebody else's server must not take down a call path of ours.
    /// </remarks>
    public static SipPresence? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XElement root;
        try
        {
            root = XDocument.Parse(xml).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        if (root is null || !string.Equals(root.Name.LocalName, "presence", StringComparison.Ordinal))
        {
            return null;
        }

        var tuples = root.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "tuple", StringComparison.Ordinal))
            .Select(ReadTuple)
            .ToArray();

        return new SipPresence((string?)root.Attribute("entity"), tuples);
    }

    private static SipPresenceTuple ReadTuple(XElement tuple)
    {
        var status = tuple.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "status", StringComparison.Ordinal));
        var basic = status?.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "basic", StringComparison.Ordinal));

        return new SipPresenceTuple(
            (string?)tuple.Attribute("id"),
            // "open" and nothing else counts as reachable. RFC 3863 defines exactly two values, and a
            // third one is a document we do not understand — reading it as open would announce
            // somebody as available on the strength of a typo.
            string.Equals(basic?.Value.Trim(), "open", StringComparison.OrdinalIgnoreCase),
            Text(tuple, "contact"),
            Text(tuple, "note"));
    }

    private static string? Text(XElement parent, string localName)
    {
        var element = parent.Elements()
            .FirstOrDefault(candidate => string.Equals(candidate.Name.LocalName, localName, StringComparison.Ordinal));
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
