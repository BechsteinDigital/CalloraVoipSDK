using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Maps the internal ICE selection result onto the public <see cref="CallIceSnapshot"/> domain
/// value exposed on <see cref="ICall.IceSnapshot"/>. Kept a pure function so surfacing ICE
/// observability has no runtime side effects.
/// </summary>
internal static class CallIceSnapshotFactory
{
    /// <summary>
    /// Projects <paramref name="selection"/> onto the public ICE snapshot value.
    /// </summary>
    public static CallIceSnapshot From(CallIceSelectionResult selection)
        => new(
            State: MapState(selection.State),
            HasSelectedPair: selection.HasSelectedPair,
            Nominated: selection.Nominated,
            LocalCandidate: selection.LocalCandidate,
            RemoteCandidate: selection.RemoteCandidate,
            SelectedLocalEndPoint: selection.LocalEndPoint,
            SelectedRemoteEndPoint: selection.RemoteEndPoint);

    private static CallIceState MapState(CallIceNegotiationState state)
        => state switch
        {
            CallIceNegotiationState.Disabled => CallIceState.Disabled,
            CallIceNegotiationState.Gathering => CallIceState.Gathering,
            CallIceNegotiationState.Gathered => CallIceState.Gathered,
            CallIceNegotiationState.Checking => CallIceState.Checking,
            CallIceNegotiationState.Nominating => CallIceState.Nominating,
            CallIceNegotiationState.Connected => CallIceState.Connected,
            CallIceNegotiationState.Failed => CallIceState.Failed,
            _ => CallIceState.Disabled
        };
}
