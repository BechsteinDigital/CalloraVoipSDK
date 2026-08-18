using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 8829 §4.1.3 signalling state machine on its own (#332). Its rules used to be restated inline in each
/// negotiation path, where they could only be exercised by driving a whole peer through an offer/answer exchange;
/// naming the transitions makes the legality rules directly testable — including the illegal ones, which a
/// full-peer test can barely reach.
/// </summary>
public sealed class WebRtcNegotiationStateTests
{
    // The state machine only carries the model as the offerer/answerer discriminator; its contents never matter.
    private static SdpSessionDescription Model() => new()
    {
        OriginAddress = "127.0.0.1",
        ConnectionAddress = "127.0.0.1",
        Media = [],
    };

    [Fact]
    public void A_fresh_state_is_stable_with_no_descriptions()
    {
        var state = new WebRtcNegotiationState(new object());

        Assert.Equal(WebRtcSignalingState.Stable, state.Current);
        Assert.Null(state.LocalDescription);
        Assert.Null(state.RemoteDescription);
    }

    [Fact]
    public void The_first_offer_crosses_the_edge_and_a_re_offer_does_not()
    {
        var state = new WebRtcNegotiationState(new object());

        Assert.True(state.EnterHaveLocalOffer(Model(), "offer-1"));   // Stable → HaveLocalOffer: a transition
        Assert.False(state.EnterHaveLocalOffer(Model(), "offer-2"));  // replaces the pending offer, no transition

        Assert.Equal(WebRtcSignalingState.HaveLocalOffer, state.Current);
        Assert.Equal("offer-2", state.LocalDescription);
    }

    [Fact]
    public void An_offer_is_rejected_while_a_remote_offer_is_pending()
    {
        var state = new WebRtcNegotiationState(new object());
        state.BeginApplyRemote();   // answerer → HaveRemoteOffer
        Assert.Equal(WebRtcSignalingState.HaveRemoteOffer, state.Current);

        var ex = Assert.Throws<InvalidOperationException>(() => state.EnterHaveLocalOffer(Model(), "offer"));
        Assert.Contains("HaveRemoteOffer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answerer_enters_have_remote_offer_and_an_offerer_stays_put()
    {
        var answerer = new WebRtcNegotiationState(new object());
        var (pendingOffer, pendingLocal) = answerer.BeginApplyRemote();
        Assert.Null(pendingOffer);       // no local offer → this peer answers
        Assert.Null(pendingLocal);
        Assert.Equal(WebRtcSignalingState.HaveRemoteOffer, answerer.Current);

        var offerer = new WebRtcNegotiationState(new object());
        var model = Model();
        offerer.EnterHaveLocalOffer(model, "offer");
        var (offererPending, offererLocal) = offerer.BeginApplyRemote();
        Assert.Same(model, offererPending);
        Assert.Equal("offer", offererLocal);
        // The offerer stays in HaveLocalOffer until the answer is applied — it is not answering anything.
        Assert.Equal(WebRtcSignalingState.HaveLocalOffer, offerer.Current);
    }

    [Fact]
    public void A_second_remote_description_is_rejected_while_one_is_pending()
    {
        var state = new WebRtcNegotiationState(new object());
        state.BeginApplyRemote();

        Assert.Throws<InvalidOperationException>(() => state.BeginApplyRemote());
    }

    [Fact]
    public void Renegotiation_reads_the_role_from_the_state_not_from_a_past_offer()
    {
        // The discriminator that differs from the first cycle: after cycle 1 a local offer model exists forever,
        // so "was an offer created" would make every later cycle look like the offerer's.
        var state = new WebRtcNegotiationState(new object());
        state.EnterHaveLocalOffer(Model(), "offer-1");
        state.SettleStable("remote-1", "local-1");

        var (isAnswerer, _, localSdp) = state.BeginRenegotiate();
        Assert.True(isAnswerer);                    // Stable → this remote is a new offer to answer
        Assert.Equal("local-1", localSdp);
        Assert.Equal(WebRtcSignalingState.HaveRemoteOffer, state.Current);

        state.RollBackToStable();
        state.EnterHaveLocalOffer(Model(), "offer-2");
        var (isAnswererAgain, _, _) = state.BeginRenegotiate();
        Assert.False(isAnswererAgain);              // HaveLocalOffer → this remote answers our re-offer
    }

    [Fact]
    public void Rolling_back_a_failed_answer_makes_a_later_attempt_possible()
    {
        var state = new WebRtcNegotiationState(new object());
        state.BeginApplyRemote();                   // stranded in HaveRemoteOffer if the answer fails

        state.RollBackToStable();

        Assert.Equal(WebRtcSignalingState.Stable, state.Current);
        Assert.True(state.EnterHaveLocalOffer(Model(), "offer")); // possible again — the point of the rollback
    }

    [Fact]
    public void Closing_is_terminal_and_reports_only_the_first_close()
    {
        var state = new WebRtcNegotiationState(new object());

        Assert.True(state.Close());     // the caller raises the event once
        Assert.False(state.Close());    // idempotent across a double dispose

        Assert.Equal(WebRtcSignalingState.Closed, state.Current);
        Assert.Throws<InvalidOperationException>(() => state.EnterHaveLocalOffer(Model(), "offer"));
        Assert.Throws<InvalidOperationException>(() => state.BeginApplyRemote());
    }
}
