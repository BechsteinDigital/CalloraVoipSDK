using Docker.DotNet;
using Xunit;

namespace CalloraVoipSdk.InteropTests;

/// <summary>
/// Ein <see cref="FactAttribute"/>, das den Test überspringt, wenn kein Docker-Daemon erreichbar ist.
/// Ermöglicht es, Docker-abhängige Interop-Tests auf Maschinen ohne Docker-Daemon stabil zu überspringen
/// statt fehlzuschlagen.
/// </summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    private static readonly bool DockerAvailable = ProbeDocker();

    /// <summary>
    /// Erstellt das Attribut und setzt <see cref="FactAttribute.Skip"/>, falls kein Docker-Daemon
    /// erreichbar ist.
    /// </summary>
    public DockerRequiredFactAttribute()
    {
        if (!DockerAvailable)
            Skip = "Kein erreichbarer Docker-Daemon — Interop-Test übersprungen.";
    }

    private static bool ProbeDocker()
    {
        try
        {
            // DockerClientBuilder() liest DOCKER_HOST / DOCKER_CONTEXT und fällt andernfalls
            // auf den plattformüblichen Standard-Socket zurück (Unix: /var/run/docker.sock,
            // Windows: npipe://./pipe/docker_engine).
            // PingAsync mit kurzem Timeout liefert eine schnelle, zuverlässige Antwort.
            using var client = new DockerClientBuilder()
                .WithTimeout(TimeSpan.FromSeconds(3))
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.System.PingAsync(cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
