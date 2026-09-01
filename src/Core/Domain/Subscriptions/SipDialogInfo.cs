using System.Xml.Linq;

namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>
/// What a watched line is currently doing (dialog-info, RFC 4235).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the busy lamp.</b> Subscribe to the <c>dialog</c> package of an extension and each
/// notification says whether it is idle, ringing, or on a call — the difference between a telephone
/// system that shows its colleagues and one that does not.
/// </para>
/// <para>
/// <b>Partial updates are the trap.</b> A document is <c>full</c> or <c>partial</c>: a full one lists
/// every dialog and therefore says "everything not mentioned is over", a partial one carries only what
/// changed and says nothing about the rest. Treating a partial document as complete clears a lamp that
/// should still be lit — so <see cref="IsFullState"/> is exposed rather than smoothed away, and a
/// consumer that ignores it will get it wrong in exactly that one direction.
/// </para>
/// <para>
/// <see cref="Version"/> comes with the same warning: notifications may arrive out of order, and a
/// document older than the one already applied has to be dropped rather than merged.
/// </para>
/// </remarks>
public sealed class SipDialogInfo
{
    private SipDialogInfo(
        string? entity, long? version, bool isFullState, IReadOnlyList<SipDialogInfoEntry> dialogs)
    {
        Entity = entity;
        Version = version;
        IsFullState = isFullState;
        Dialogs = dialogs;
    }

    /// <summary>Whose dialogs these are, from the <c>entity</c> attribute.</summary>
    public string? Entity { get; }

    /// <summary>Document version; increases per notification. Null when the sender omitted it.</summary>
    public long? Version { get; }

    /// <summary>
    /// Whether this document lists every dialog (<c>full</c>) or only what changed (<c>partial</c>).
    /// </summary>
    public bool IsFullState { get; }

    /// <summary>The dialogs the document reports.</summary>
    public IReadOnlyList<SipDialogInfoEntry> Dialogs { get; }

    /// <summary>
    /// Whether the watched party is on a call or being called right now.
    /// </summary>
    /// <remarks>
    /// Early counts as busy, and that is deliberate: a colleague whose phone is ringing cannot take a
    /// second call either, and a lamp that only lights on <c>confirmed</c> invites exactly that.
    /// </remarks>
    public bool IsBusy => Dialogs.Any(dialog =>
        dialog.State is SipDialogState.Early or SipDialogState.Confirmed or SipDialogState.Proceeding);

    /// <summary>Reads a dialog-info document, or returns null when it is not one.</summary>
    /// <remarks>
    /// Null rather than an exception: it arrives on a NOTIFY from somebody else's server, and their
    /// malformed XML must not take down a call path of ours.
    /// </remarks>
    public static SipDialogInfo? TryParse(string? xml)
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

        if (root is null || !string.Equals(root.Name.LocalName, "dialog-info", StringComparison.Ordinal))
        {
            return null;
        }

        var dialogs = root.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "dialog", StringComparison.Ordinal))
            .Select(ReadDialog)
            .ToArray();

        return new SipDialogInfo(
            (string?)root.Attribute("entity"),
            long.TryParse((string?)root.Attribute("version"), out var version) ? version : null,
            // Anything other than an explicit "partial" is treated as full, including a missing
            // attribute: RFC 4235 makes full the default, and guessing partial would leave lamps lit
            // for calls that ended.
            !string.Equals((string?)root.Attribute("state"), "partial", StringComparison.OrdinalIgnoreCase),
            dialogs);
    }

    private static SipDialogInfoEntry ReadDialog(XElement dialog)
    {
        var state = dialog.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "state", StringComparison.Ordinal))
            ?.Value.Trim();

        return new SipDialogInfoEntry(
            (string?)dialog.Attribute("id"),
            ReadState(state),
            (string?)dialog.Attribute("direction"),
            Identity(dialog, "local"),
            Identity(dialog, "remote"));
    }

    private static SipDialogState ReadState(string? value) => value?.ToLowerInvariant() switch
    {
        "trying" => SipDialogState.Trying,
        "proceeding" => SipDialogState.Proceeding,
        "early" => SipDialogState.Early,
        "confirmed" => SipDialogState.Confirmed,
        "terminated" => SipDialogState.Terminated,
        // Including null. An unrecognised state is not idle — a lamp that goes dark on a word we do
        // not know is worse than one that stays as it was.
        _ => SipDialogState.Unknown
    };

    private static string? Identity(XElement dialog, string side)
    {
        var party = dialog.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, side, StringComparison.Ordinal));
        var identity = party?.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "identity", StringComparison.Ordinal));
        var value = identity?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
