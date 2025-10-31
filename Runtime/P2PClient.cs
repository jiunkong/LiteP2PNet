using Unity.WebRTC;
using NativeWebSocket;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Text;

namespace LiteP2PNet {
    public class P2PClient : MonoBehaviour {
        private static P2PClient _instance;
        public static P2PClient Instance {
            get {
                if (_instance != null) return _instance;

                _instance = FindAnyObjectByType<P2PClient>();
                if (_instance != null) return _instance;

                var obj = new GameObject("P2PClient (Singleton)");
                _instance = obj.AddComponent<P2PClient>();
                DontDestroyOnLoad(obj);
                return _instance;
            }
        }

        private WebSocket _signaling;
        private Dictionary<string, RTCPeerConnection> _peerConnectionMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap = new();
        private ConcurrentQueue<SignalingMessage> _incomingMessageQueue = new();
        private Dictionary<string, bool> _isDescriptionReadyMap = new();

        private string _userId;
        private string _serverUrl;

        private List<RTCIceServer> _iceServers = new();

        public bool isConnectedToServer { get; private set; } = false;

        private bool _debugLog = false;

        private Dictionary<string, List<RTCDataChannel>> _dataChannelListMap = new();

        delegate void BytesHandler(string peerId, byte[] data, SendOption? option);
        delegate void StringHandler(string peerId, string data, SendOption? option);
        private class HandlerGroup {
            public BytesHandler bytesHandler;
            public StringHandler stringHandler;
        }

        private Dictionary<string, HandlerGroup> _handlerMap = new();

        void Awake() {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public IEnumerator GetRTT(string peerId, Action<double?> onResult) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                var statsOp = connection.GetStats();
                yield return statsOp;
                foreach (var report in statsOp.Value.Stats.Values) {
                    if (report.Type == RTCStatsType.CandidatePair) {
                        RTCIceCandidatePairStats pairStats = (RTCIceCandidatePairStats)report;
                        onResult(pairStats.currentRoundTripTime * 1000.0);
                    }
                }
            }

            onResult(null);
        }

        public void Init(string serverUrl, string userId, string[] stunServers = null, string[] turnServers = null, bool debugLog = false) {
            if (userId.Length > 256) throw new Exception("User ID must be less than 256 characters");
            
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
                var message = System.Text.Encoding.UTF8.GetString(bytes);
                var signalingMessage = JsonUtility.FromJson<SignalingMessage>(message);
                _incomingMessageQueue.Enqueue(signalingMessage);
            };
        }

        public IEnumerator ConnectServerAsync(string lobbyId, float timeout = 10f) {
            _signaling = new WebSocket(_serverUrl, new Dictionary<string, string> {
                { "user-id", _userId },
                { "lobby-id", lobbyId }
            });

            _dataChannelListMap = new();
            _handlerMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageQueue = new();

            SetupSignaling();

            _signaling.Connect();

            float elapsed = 0f;
            
            while (!isConnectedToServer && elapsed < timeout) {
                yield return null;
                elapsed += Time.deltaTime;
            }
            
            if (!isConnectedToServer) {
                Debug.LogError("Failed to connect to signaling server");
            }
        }

        public void DisconnectServer() {
            _signaling.Close();
            _signaling = null;
        }

        public IEnumerator ConnectPeerAsync(string peerId) {
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

        public void DisconnectPeer(string peerId) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                connection.Close();
                connection.Dispose();
                _peerConnectionMap.Remove(peerId);
            }

            _myIceCandidatesMap.Remove(peerId);
            _isDescriptionReadyMap.Remove(peerId);
        }

        private void SetupPeerConnection(string peerId) {
            var config = new RTCConfiguration { iceServers = _iceServers.ToArray() };

            var connection = new RTCPeerConnection(ref config);

            List<RTCDataChannel> channels = new() {
                connection.CreateDataChannel("OrderedReliable", new RTCDataChannelInit { ordered = true }),
                connection.CreateDataChannel("OrderedUnReliable", new RTCDataChannelInit { ordered = true, maxRetransmits = 0 }),
                connection.CreateDataChannel("UnorderedReliable", new RTCDataChannelInit { ordered = false }),
                connection.CreateDataChannel("UnorderedUnreliable", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 })
            };

            _handlerMap[peerId] = new();

            foreach (var channel in channels) {
                channel.OnMessage += (rawdata) => {
                    HandleDataChannel(peerId, rawdata);
                };
            }

            if (_debugLog) Debug.Log($"Created peer connection for {peerId}");

            _myIceCandidatesMap.Add(peerId, new());

            connection.OnIceCandidate = candidate => {
                if (candidate != null) {
                    lock (_myIceCandidatesMap) {
                        _myIceCandidatesMap[peerId]?.Add(candidate);
                    }
                    if (_debugLog) Debug.Log($"New Local ICE candidate for {peerId}: {candidate.Candidate}");

                    SendSignalingMessage("ice-candidate", peerId, new IceCandidateData {
                        candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid,
                        sdpMLineIndex = new NullableInt(candidate.SdpMLineIndex)
                    });
                }
            };

            connection.OnIceConnectionChange = state => {
                if (_debugLog) Debug.Log($"ICE connection state for {peerId}: {state}");
            };

            _peerConnectionMap.Add(peerId, connection);
            _dataChannelListMap.Add(peerId, channels);
        }

        private void HandleDataChannel(string peerId, byte[] rawdata) {
            if (!_handlerMap.TryGetValue(peerId, out var handler)) return;
            SendOption? option = null;

            switch (rawdata[0] >> 4) {
                case (byte)SendOption.OrderedReliable:
                    option = SendOption.OrderedReliable;
                    break;
                case (byte)SendOption.OrderedUnreliable:
                    option = SendOption.OrderedUnreliable;
                    break;
                case (byte)SendOption.UnorderedReliable:
                    option = SendOption.UnorderedReliable;
                    break;
                case (byte)SendOption.UnorderedUnreliable:
                    option = SendOption.UnorderedUnreliable;
                    break;
            }

            switch (rawdata[0] >> 6) {
                case (byte)DataType.Byte:
                    handler.bytesHandler?.Invoke(peerId, rawdata[1..^0], option);
                    break;
                case (byte)DataType.String:
                    handler.stringHandler?.Invoke(peerId, Encoding.UTF8.GetString(rawdata[1..^0]), option);
                    break;
            }
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

            var offerData = JsonUtility.FromJson<OfferAnswerData>(message.body);
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
            var answerData = JsonUtility.FromJson<OfferAnswerData>(message.body);
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
            var candidateData = JsonUtility.FromJson<IceCandidateData>(message.body);
            var candidate = new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidateData.candidate,
                sdpMid = candidateData.sdpMid,
                sdpMLineIndex = candidateData.sdpMLineIndex.ToNullable()
            });

            if (_isDescriptionReadyMap.ContainsKey(message.from) && _isDescriptionReadyMap[message.from]) {
                _peerConnectionMap[message.from].AddIceCandidate(candidate);
                Debug.Log($"Added Remote ICE Candidate: {candidate.Candidate}");
            } else {
                Debug.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");
            }
            
            if (_remoteIceCandidatesMap.ContainsKey(message.from)) {
                _remoteIceCandidatesMap[message.from].Add(candidate);
            } else {
                _remoteIceCandidatesMap.Add(message.from, new() { candidate });
            }
        }

        private void SendSignalingMessage(string type, string to, object data) {
            if (!isConnectedToServer) return;
            try {
                string json = JsonUtility.ToJson(data);
                var message = new SignalingMessage {
                    type = type,
                    from = _userId,
                    to = to,
                    body = json
                };

                _signaling.SendText(JsonUtility.ToJson(message));
            }
            catch (Exception ex) {
                throw new Exception("Failed to send signaling message", ex);
            }
        }

        private bool SendData(string peerId, byte[] data, SendOption option) {
            if (!_dataChannelListMap.TryGetValue(peerId, out var channels)) return false;

            channels[(byte)option].Send(data);
            return true;
        }

        public bool SendBytes(string peerId, byte[] data, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Byte << 6;
            prefix &= (byte)((byte)option << 4);

            List<byte> _data = new() { prefix };
            
            _data.AddRange(data);

            return SendData(peerId, _data.ToArray(), option);
        }

        public bool SendString(string peerId, string data, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.String << 6;
            prefix &= (byte)((byte)option << 4);

            byte[] rawdata = Encoding.UTF8.GetBytes(data);

            List<byte> _data = new() { prefix };

            _data.AddRange(rawdata);

            return SendData(peerId, _data.ToArray(), option);
        }


        void Update() {
            #if !UNITY_WEBGL || UNITY_EDITOR
            _signaling?.DispatchMessageQueue();
            #endif

            while (_incomingMessageQueue.TryDequeue(out var message)) {
                StartCoroutine(HandleSignalingMessage(message));
            }
        }

        private void CleanUp() {
            if (_signaling != null) DisconnectServer();
        }

        void OnApplicationQuit() {
            CleanUp();
        }

        void OnDestroy() {
            CleanUp();
        }
    }
}