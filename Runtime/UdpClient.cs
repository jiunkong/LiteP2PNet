using Unity.WebRTC;
using NativeWebSocket;
using LiteNetLib;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections;
using System.Data;
using UnityEngine.Analytics;
using Unity.VisualScripting;

namespace LiteP2PNet {
    public class UdpClient : MonoBehaviour, INetEventListener {
        private WebSocket _signaling;
        private Dictionary<string, RTCPeerConnection> _peerConnectionMap;
        private NetManager _netManager;
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap;
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap;
        private Dictionary<string, ConcurrentQueue<SignalingMessage>> _incomingMessageMap;
        private Dictionary<string, UdpInfo> _udpInfoMap;

        private string _userId;
        private string _serverUrl;

        private List<RTCIceServer> _iceServers = new();

        public bool isConnectedToServer { get; private set; } = false;

        private bool _debugLog = false;

        private Dictionary<string, bool> _isConnectionEstablishedMap;
        private Dictionary<string, bool> _isDescriptionReadyMap;

        public void Init(string serverUrl, string userId, string[] stunServers = null, string[] turnServers = null, bool debugLog = false) {
            _userId = userId;
            _serverUrl = serverUrl;
            isConnectedToServer = false;
            _debugLog = debugLog;

            if (stunServers != null) {
                foreach (var stun in stunServers) {
                    _iceServers.Add(new RTCIceServer { urls = new[] { stun } });
                }
            }

            if (turnServers != null) {
                foreach (var turn in turnServers) {
                    _iceServers.Add(new RTCIceServer { urls = new[] { turn } });
                }
            }

            SetupSignaling();

            _netManager = new NetManager(this);
            _netManager.Start();
        }

        private void SetupSignaling() {
            _signaling.OnOpen += () => {
                isConnectedToServer = true;
                if (_debugLog) Debug.Log("Connected to signaling server");
            };

            _signaling.OnError += (e) => {
                if (_debugLog) Debug.LogError("Signaling error: " + e);
            };

            _signaling.OnClose += (e) => {
                isConnectedToServer = false;
                if (_debugLog) Debug.Log("Disconnected from signaling server");
            };

            _signaling.OnMessage += (bytes) => {
                if (!_incomingMessageMap.ContainsKey(_userId)) _incomingMessageMap.Add(_userId, new());

                var message = System.Text.Encoding.UTF8.GetString(bytes);
                var signalingMessage = JsonUtility.FromJson<SignalingMessage>(message);
                _incomingMessageMap[_userId].Enqueue(signalingMessage);
            };
        }

        public void ConnectServer(string lobbyId) {
            _signaling = new WebSocket(_serverUrl, new Dictionary<string, string> {
                { "user-id", _userId },
                { "lobby-id", lobbyId }
            });

            _isConnectionEstablishedMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageMap = new();
            _udpInfoMap = new();

            _signaling.Connect();
        }

        public void DisconnectServer() {
            _signaling.Close();
            _signaling = null;
        }

        public IEnumerator ConnectPeer(string peerId) {
            SetupPeerConnection(peerId);

            var connection = _peerConnectionMap[peerId];
            var offerOp = connection.CreateOffer();
            yield return offerOp;
            var offerDesc = offerOp.Desc;
            connection.SetLocalDescription(ref offerDesc);

            SendSignalingMessage("offer", peerId, new OfferAnswerData {
                sdp = offerDesc.sdp,
                type = offerDesc.type.ToString().ToLower()
            });

            if (_debugLog) Debug.Log($"Connecting to {peerId}...");
        }

        private void SetupPeerConnection(string peerId) {
            var config = new RTCConfiguration { iceServers = _iceServers.ToArray() };

            var connection = new RTCPeerConnection(ref config);
            var channel = connection.CreateDataChannel("data"); // Create a data channel

            if (_debugLog) Debug.Log($"Created peer connection for {peerId}");

            _myIceCandidatesMap.Add(peerId, new());

            connection.OnIceCandidate = candidate => {
                if (candidate != null) {
                    lock (_myIceCandidatesMap) {
                        _myIceCandidatesMap[peerId]?.Add(candidate);
                    }
                    if (_debugLog) Debug.Log($"New Local ICE candidate for {peerId}: {candidate.Candidate}");

                    SendSignalingMessage("ice-candidate", peerId, new IceCandidateData{
                        candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid,
                        sdpMLineIndex = new NullableInt(candidate.SdpMLineIndex)
                    });
                }
            };

            connection.OnIceConnectionChange = state => {
                if (_debugLog) Debug.Log($"ICE connection state for {peerId}: {state}");

                switch (state) {
                    case RTCIceConnectionState.Connected:
                    case RTCIceConnectionState.Completed:
                        if (!_isConnectionEstablishedMap.ContainsKey(peerId) || !_isConnectionEstablishedMap[peerId]) {
                            _isConnectionEstablishedMap.Add(peerId, true);
                            if (_debugLog) Debug.Log($"WebSocket connection established with {peerId}");
                            StartUdpHolePunching(peerId);
                        }
                        break;
                }
            };

            _peerConnectionMap.Add(peerId, connection);
        }

        private IEnumerator HandleSignalingMessage(SignalingMessage message) {
            switch (message.type) {
                case "offer":
                    yield return HandleOffer(message);
                    break;
                case "answer":
                    yield return HandleAnswer(message);
                    break;
                case "ice-candidate":
                    HandleIceCandidate(message);
                    break;
                case "udp-info":
                    HandleUdpInfo(message);
                    break;
                case "connect":
                    StartUdpHolePunching("");
                    break;
                case "user-change":
                    break;
                default:
                    if (_debugLog) Debug.LogWarning($"Unknown signaling message type: {message.type}");
                    break;
            }
        }

        private IEnumerator HandleOffer(SignalingMessage message)
        {
            SetupPeerConnection(message.from);

            var offerData = JsonUtility.FromJson<OfferAnswerData>(message.data);
            var offer = new RTCSessionDescription
            {
                type = RTCSdpType.Offer,
                sdp = offerData.sdp
            };

            var connection = _peerConnectionMap[message.from];
            yield return connection.SetRemoteDescription(ref offer);

            var answerOp = connection.CreateAnswer();
            yield return answerOp;
            var answerDesc = answerOp.Desc;
            yield return connection.SetLocalDescription(ref answerDesc);

            ProcessQueuedRemoteIceCandidates(message.from);
            _isDescriptionReadyMap[message.from] = true;

            SendSignalingMessage("answer", message.from, new OfferAnswerData {
                sdp = answerDesc.sdp,
                type = answerDesc.type.ToString().ToLower()
            });
        }

        private void ProcessQueuedRemoteIceCandidates(string peerId)
        {
            if (_isDescriptionReadyMap.ContainsKey(peerId) && _isDescriptionReadyMap[peerId]) return;

            if (_remoteIceCandidatesMap.TryGetValue(peerId, out var _remoteIceCandidates))
            {
                foreach (var c in _remoteIceCandidates)
                {
                    if (_debugLog) Debug.Log($"Processing Queued Remote ICE Candidate: {c.Candidate}");
                    _peerConnectionMap[peerId]?.AddIceCandidate(c);
                }
                _remoteIceCandidatesMap.Remove(peerId);
            }
        }

        private IEnumerator HandleAnswer(SignalingMessage message)
        {
            var answerData = JsonUtility.FromJson<OfferAnswerData>(message.data);
            var answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = answerData.sdp
            };

            var connection = _peerConnectionMap[message.from];
            yield return connection.SetRemoteDescription(ref answer);

            ProcessQueuedRemoteIceCandidates(message.from);
            _isDescriptionReadyMap[message.from] = true;
        }

        private void HandleIceCandidate(SignalingMessage message)
        {
            var candidateData = JsonUtility.FromJson<IceCandidateData>(message.data);
            var candidate = new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidateData.candidate,
                sdpMid = candidateData.sdpMid,
                sdpMLineIndex = candidateData.sdpMLineIndex.ToNullable()
            });

            if (_isDescriptionReadyMap.ContainsKey(message.from) && _isDescriptionReadyMap[message.from])
            {
                if (_remoteIceCandidatesMap.ContainsKey(message.from))
                {
                    _remoteIceCandidatesMap[message.from].Add(candidate);
                }
                else
                {
                    _remoteIceCandidatesMap.Add(message.from, new() { candidate });
                }
                Debug.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");
            }
            else
            {
                _peerConnectionMap[message.from].AddIceCandidate(candidate);
                Debug.Log($"Added Remote ICE Candidate: {candidate.Candidate}");
            }
        }

        private void HandleUdpInfo(SignalingMessage message) {
            var udpInfo = JsonUtility.FromJson<UdpInfo>(message.data);
            _udpInfoMap[message.from] = udpInfo;

            if (_debugLog) Debug.Log($"Received UDP info from {message.from}: {udpInfo.ip}:{udpInfo.port}");
        }

        private void StartUdpHolePunching(string peerId) {

        }

        public void OnPeerConnected(NetPeer peer) {
            throw new System.NotImplementedException();
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
            throw new System.NotImplementedException();
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) {
            throw new System.NotImplementedException();
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) {
            throw new System.NotImplementedException();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) {
            throw new System.NotImplementedException();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) {
            throw new System.NotImplementedException();
        }

        public void OnConnectionRequest(ConnectionRequest request) {
            throw new System.NotImplementedException();
        }

        private void SendSignalingMessage(string type, string to, object data) {
            if (!isConnectedToServer) return;
            try {
                string json = JsonUtility.ToJson(data);
                var message = new SignalingMessage {
                    type = type,
                    from = _userId,
                    to = to,
                    data = json
                };

                _signaling.SendText(JsonUtility.ToJson(message));
            }
            catch (Exception ex) {
                throw new Exception("Failed to send signaling message", ex);
            }
        }

        void Update() {
            _netManager?.PollEvents();

            foreach (var key in _incomingMessageMap.Keys) {
                while (_incomingMessageMap[key].TryDequeue(out var message)) {
                    StartCoroutine(HandleSignalingMessage(message));
                }
            }
        }
    }
}