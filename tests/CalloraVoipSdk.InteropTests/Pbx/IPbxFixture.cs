namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Ein Fremd-PBX-Peer für die Media-Szenario-Matrix (Asterisk, FreeSWITCH, …).</summary>
public interface IPbxFixture : IAsyncDisposable
{
    /// <summary>Startet den PBX-Container und wartet, bis er SIP-ready ist.</summary>
    Task StartAsync();

    /// <summary>Register-Ziel-Host (Container-Bridge-IP o. ä.). Nur nach <see cref="StartAsync"/> gültig.</summary>
    string SipHost { get; }

    /// <summary>Register-Ziel-UDP-Port.</summary>
    int SipUdpPort { get; }

    /// <summary>
    /// Ein gebrücktes Endpunkt-Paar: Caller- und Callee-Credentials plus die Dial-URI, die der Caller wählt,
    /// damit der PBX ihn an den registrierten Callee brückt. <paramref name="index"/> wählt eines der
    /// bereitgestellten Paare (0-basiert; für den Concurrent-Soak).
    /// </summary>
    PbxBridgePair BridgePair(PbxMediaMode mode, int index);

    /// <summary>Dial-URI einer Extension, die antwortet und Endlos-Media spielt (Transfer-Konsultation).</summary>
    string MediaPlaybackUri { get; }

    /// <summary>Kombinierte Container-Konsolen-Logs (Diagnose).</summary>
    Task<string> GetLogsAsync();
}

/// <summary>Medien-Sicherheitsmodus eines Bridge-Paars.</summary>
public enum PbxMediaMode { Plain, Sdes }

/// <summary>Digest-Credentials eines registrierbaren PBX-Endpunkts.</summary>
public sealed record PbxEndpoint(string Username, string Password);

/// <summary>Ein Caller/Callee-Paar plus die Bridge-Dial-URI, die den Caller an den Callee brückt.</summary>
public sealed record PbxBridgePair(PbxEndpoint Caller, PbxEndpoint Callee, string BridgeDialUri);
