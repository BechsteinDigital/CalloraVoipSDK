using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the STUN transaction registry that matches inbound responses to outbound checks on the
/// shared media socket (RFC 8445 §7.2.5 / RFC 7675): a registered transaction completes when its
/// response arrives, times out when it does not, and an unknown transaction is ignored.
/// </summary>
public sealed class IceStunTransactionRegistryTests
{
    private static byte[] TxId(byte seed)
    {
        var id = new byte[12];
        Array.Fill(id, seed);
        return id;
    }

    [Fact]
    public async Task Completes_when_matching_response_arrives()
    {
        var registry = new IceStunTransactionRegistry();
        var txId = TxId(1);

        // Register (synchronously) and start awaiting, then deliver the response.
        var pending = registry.AwaitResponseAsync(txId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(registry.TryComplete(txId));

        Assert.True(await pending);
    }

    [Fact]
    public async Task Times_out_when_no_response_arrives()
    {
        var registry = new IceStunTransactionRegistry();

        var answered = await registry.AwaitResponseAsync(TxId(2), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(answered);
    }

    [Fact]
    public void TryComplete_returns_false_for_unknown_transaction()
    {
        var registry = new IceStunTransactionRegistry();

        Assert.False(registry.TryComplete(TxId(3)));
    }

    [Fact]
    public async Task Completed_transaction_is_removed_so_a_late_duplicate_does_not_match()
    {
        var registry = new IceStunTransactionRegistry();
        var txId = TxId(4);

        var pending = registry.AwaitResponseAsync(txId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(registry.TryComplete(txId));
        Assert.True(await pending);

        // The entry is gone after completion; a duplicate/retransmitted response no longer matches.
        Assert.False(registry.TryComplete(txId));
    }
}
