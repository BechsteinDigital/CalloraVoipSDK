// Native-browser WebRTC. The .NET host is only a signalling relay (see Program.cs); media is peer-to-peer.

const localVideo = document.getElementById('local');
const remoteVideo = document.getElementById('remote');
const joinButton = document.getElementById('join');
const status = document.getElementById('status');

let peer;
let socket;

function setStatus(text) { status.textContent = text; }
function send(message) { socket.send(JSON.stringify(message)); }

async function start() {
  const localStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
  localVideo.srcObject = localStream;

  // No ICE servers: works on localhost / same LAN. Add STUN/TURN for real-world NAT traversal.
  peer = new RTCPeerConnection({ iceServers: [] });
  localStream.getTracks().forEach((track) => peer.addTrack(track, localStream));

  peer.ontrack = (event) => { remoteVideo.srcObject = event.streams[0]; };
  peer.onicecandidate = (event) => { if (event.candidate) send({ type: 'ice', candidate: event.candidate }); };
  peer.onconnectionstatechange = () => setStatus(`Connection: ${peer.connectionState}`);
}

function connectSignalling() {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  socket = new WebSocket(`${scheme}://${location.host}/ws`);
  socket.onopen = () => setStatus('Waiting for a peer…');

  socket.onmessage = async (event) => {
    const message = JSON.parse(event.data);
    switch (message.type) {
      case 'start': {                       // we are the second peer — we initiate
        const offer = await peer.createOffer();
        await peer.setLocalDescription(offer);
        send({ type: 'offer', sdp: offer });
        break;
      }
      case 'offer':
        await peer.setRemoteDescription(message.sdp);
        const answer = await peer.createAnswer();
        await peer.setLocalDescription(answer);
        send({ type: 'answer', sdp: answer });
        break;
      case 'answer':
        await peer.setRemoteDescription(message.sdp);
        break;
      case 'ice':
        try { await peer.addIceCandidate(message.candidate); }
        catch (error) { console.warn('Failed to add ICE candidate', error); }
        break;
    }
  };
}

joinButton.onclick = async () => {
  joinButton.disabled = true;
  try {
    await start();                 // camera + peer ready before any signalling arrives
    connectSignalling();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
    joinButton.disabled = false;
  }
};
