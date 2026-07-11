using System.Net;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Read-only snapshot of ICE (RFC 8445 / RFC 7675) connectivity establishment for one call media
/// leg. Produced once candidate-pair selection completes and exposed via
/// <see cref="ICall.IceSnapshot"/>. It explains which local/remote candidate pair media flows over
/// and why — otherwise an ICE-driven media path or teardown is opaque to the consumer. Non-ICE
/// calls never produce a snapshot (<see cref="ICall.IceSnapshot"/> stays <see langword="null"/>).
/// </summary>
/// <param name="State">Final ICE state reached by the agent for this leg.</param>
/// <param name="HasSelectedPair">
/// True when ICE selected a working candidate pair and media uses
/// <see cref="SelectedLocalEndPoint"/>/<see cref="SelectedRemoteEndPoint"/> instead of the plain SDP endpoints.
/// </param>
/// <param name="Nominated">
/// True when the selected pair was confirmed by a USE-CANDIDATE nomination check (RFC 8445 §8.1.1);
/// false for an unnominated but reachable pair.
/// </param>
/// <param name="LocalCandidate">The selected local ICE candidate, or <see langword="null"/> when no pair was selected.</param>
/// <param name="RemoteCandidate">The selected remote ICE candidate, or <see langword="null"/> when no pair was selected.</param>
/// <param name="SelectedLocalEndPoint">The selected local RTP endpoint, or <see langword="null"/> when no pair was selected.</param>
/// <param name="SelectedRemoteEndPoint">The selected remote RTP endpoint, or <see langword="null"/> when no pair was selected.</param>
public sealed record CallIceSnapshot(
    CallIceState State,
    bool HasSelectedPair,
    bool Nominated,
    CallIceCandidate? LocalCandidate,
    CallIceCandidate? RemoteCandidate,
    IPEndPoint? SelectedLocalEndPoint,
    IPEndPoint? SelectedRemoteEndPoint);
