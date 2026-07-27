using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MiniCore.Compare.Interop.Adapters;

namespace MiniCore.Compare.Interop.Asterisk;

public sealed class AsteriskTestServer : IAsyncDisposable
{
    private const string PjsipConf =
        "[transport-udp]\n" +
        "type=transport\n" +
        "protocol=udp\n" +
        "bind=0.0.0.0:5060\n" +
        "\n" +
        "[6001]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw\n" +
        "auth=6001\n" +
        "aors=6001\n" +
        "direct_media=no\n" +
        "\n" +
        "[6001]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6001\n" +
        "password=secret\n" +
        "\n" +
        "[6001]\n" +
        "type=aor\n" +
        "max_contacts=1\n" +
        "remove_existing=yes\n";

    private const string ExtensionsConf =
        "[default]\n" +
        "exten => answer,1,Answer()\n" +
        "same => n,Milliwatt()\n" +
        "exten => echo,1,Answer()\n" +
        "same => n,Echo()\n" +
        "exten => dtmf,1,Answer()\n" +
        "same => n,Wait(2)\n" +
        "same => n,SendDTMF(1234)\n" +
        "same => n,Wait(30)\n" +
        "exten => busy,1,Busy()\n" +
        "exten => decline,1,Hangup(21)\n" +
        "exten => remotehangup,1,Answer()\n" +
        "same => n,Wait(3)\n" +
        "same => n,Hangup()\n" +
        "exten => noanswer,1,Ringing()\n" +
        "same => n,Wait(3600)\n";

    private readonly IContainer _container;
    private readonly FileInfo _pjsipConfFile;
    private readonly FileInfo _extensionsConfFile;

    public AsteriskTestServer()
    {
        _pjsipConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_pjsipConfFile.FullName, PjsipConf);
        _extensionsConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_extensionsConfFile.FullName, ExtensionsConf);

        _container = new ContainerBuilder("andrius/asterisk:22")
            .WithResourceMapping(_pjsipConfFile, new FileInfo("/etc/asterisk/pjsip.conf"))
            .WithResourceMapping(_extensionsConfFile, new FileInfo("/etc/asterisk/extensions.conf"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready."))
            .Build();
    }

    public string ServerAddress => _container.IpAddress;

    public SipTestAccount Account => new(ServerAddress, 5060, "6001", "secret");

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public string Target(string extension) => $"sip:{extension}@{ServerAddress}:5060";

    public async Task EnablePjsipLoggerAsync()
    {
        var result = await _container
            .ExecAsync(["asterisk", "-rx", "pjsip set logger on"])
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Asterisk PJSIP logger activation failed ({result.ExitCode}): {result.Stderr}");
        }
    }

    public async Task<string> GetLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
        return string.Concat(stdout, Environment.NewLine, stderr);
    }

    public async Task OriginateInboundAsync()
    {
        var result = await _container
            .ExecAsync(["asterisk", "-rx", "channel originate PJSIP/6001 application Milliwatt"])
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Asterisk originate failed ({result.ExitCode}): {result.Stderr}");
        }
    }

    public async Task<string> ShowChannelsAsync()
    {
        var result = await _container
            .ExecAsync(["asterisk", "-rx", "core show channels count"])
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Asterisk channel query failed ({result.ExitCode}): {result.Stderr}");
        }

        return result.Stdout;
    }

    public async Task<string> ShowContactsAsync()
    {
        var result = await _container
            .ExecAsync(["asterisk", "-rx", "pjsip show contacts"])
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Asterisk contact query failed ({result.ExitCode}): {result.Stderr}");
        }

        return result.Stdout;
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;

        try
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        DeleteTempFile(_pjsipConfFile, ref failures);
        DeleteTempFile(_extensionsConfFile, ref failures);

        if (failures is not null)
        {
            throw new AggregateException("Asterisk test server cleanup failed.", failures);
        }
    }

    private static void DeleteTempFile(FileInfo file, ref List<Exception>? failures)
    {
        try
        {
            file.Delete();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
    }
}
