using Unity.WebRTC;
using NativeWebSocket;
using LiteNetLib;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Text.RegularExpressions;
using System.Linq;

namespace LiteP2PNet {
    public class UdpClient : MonoBehaviour, INetEventListener {
        private static UdpClient _instance;
        public static UdpClient Instance {
            get {
                if (_instance != null) return _instance;

                _instance = FindAnyObjectByType<UdpClient>();
                if (_instance != null) return _instance;

                var obj = new GameObject("UdpClient (Singleton)");
                _instance = obj.AddComponent<UdpClient>();
                DontDestroyOnLoad(obj);
                return _instance;
            }
        }

        private static readonly Regex _udpInfoPattern = new(@"(\d{1,3}(?:\.\d{1,3}){3}) (\d+)");
        private static bool ExtractUdpInfo(IEnumerable<RTCIceCandidate> candidates, Func<string, bool> filter, out UdpInfo udpInfo) {
            foreach (var c in candidates) {
                if (filter != null && !filter.Invoke(c.Candidate)) continue;

                var match = _udpInfoPattern.Match(c.Candidate);
                if (match.Success) {
                    string ip = match.Groups[1].Value;
                    int port = int.Parse(match.Groups[2].Value);
                    udpInfo = new UdpInfo { ip = ip, port = port };
                    return true;
                }
            }

            udpInfo = null;
            return false;
        }

        private WebSocket _signaling;
        private Dictionary<string, RTCPeerConnection> _peerConnectionMap = new();
        private NetManager _netManager;
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap = new();
        private ConcurrentQueue<SignalingMessage> _incomingMessageQueue = new();
        private PairMap<string, UdpInfo> _udpInfoBiMap = new();
        private Dictionary<string, bool> _udpInfoSentMap = new();

        private Dictionary<string, string> _connectionKeyMap = new();
        private PairMap<string, NetPeer> _peerBiMap = new();

        private string _userId;
        private string _serverUrl;
        private int _bursts;
        private int _burstInterval;
        private int _maxBurstRounds;

        private List<RTCIceServer> _iceServers = new();

        public bool isConnectedToServer { get; private set; } = false;

        private bool _debugLog = false;

        private Dictionary<string, bool> _isConnectionEstablishedMap = new();
        private Dictionary<string, bool> _isDescriptionReadyMap = new();

        private Dictionary<string, int> _latencyMap = new();

        void Awake() {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public bool TryGetLatency(string peerId, out int latency) {
            return _latencyMap.TryGetValue(peerId, out latency);
        }

        public void Init(string serverUrl, string userId, string[] stunServers = null, string[] turnServers = null, bool debugLog = false, int udpBursts = 5, int udpBurstInterval = 150, int udpMaxBurstRounds = 4) {
            _userId = userId;
            _serverUrl = serverUrl;
            isConnectedToServer = false;
            _debugLog = debugLog;
            _bursts = udpBursts;
            _burstInterval = udpBurstInterval;
            _maxBurstRounds = udpMaxBurstRounds;

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

            _netManager = new NetManager(this);
            _netManager.UnconnectedMessagesEnabled = true;
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
                var message = System.Text.Encoding.UTF8.GetString(bytes);
                Debug.Log(message);
                var signalingMessage = JsonUtility.FromJson<SignalingMessage>(message);
                _incomingMessageQueue.Enqueue(signalingMessage);
            };
        }

        public IEnumerator ConnectServerAsync(string lobbyId, float timeout = 10f) {
            _signaling = new WebSocket(_serverUrl, new Dictionary<string, string> {
                { "user-id", _userId },
                { "lobby-id", lobbyId }
            });

            _isConnectionEstablishedMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageQueue = new();
            _udpInfoBiMap = new();
            _peerBiMap = new();
            _connectionKeyMap = new();
            _latencyMap = new();
            _udpInfoSentMap = new();

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

                    SendSignalingMessage("ice-candidate", peerId, new IceCandidateData {
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
                        if (_udpInfoSentMap.ContainsKey(peerId) && _udpInfoSentMap[peerId]) {
                            // already sent udp info
                            return;
                        }

                        if (!_isConnectionEstablishedMap.ContainsKey(peerId) || !_isConnectionEstablishedMap[peerId]) {
                            _isConnectionEstablishedMap.Add(peerId, true);
                            if (_debugLog) Debug.Log($"WebSocket connection established with {peerId}");

                            SendUdpInfo(peerId);
                        }
                        break;
                }
            };

            _peerConnectionMap.Add(peerId, connection);
        }
        
        private void SendUdpInfo(string peerId) {
            if (!_remoteIceCandidatesMap.ContainsKey(peerId)) {
                if (_debugLog) Debug.LogWarning($"No Peer Connection for {peerId}, cannot send UDP info");
                return;
            }

            if (!_myIceCandidatesMap.ContainsKey(peerId)) {
                if (_debugLog) Debug.LogWarning($"No local ICE candidates for {peerId}, cannot send UDP info");
                return;
            }

            var remoteCandidates = _remoteIceCandidatesMap[peerId].Where(c => c.Candidate.Contains("udp"));
            var localCandidates = _myIceCandidatesMap[peerId].Where(c => c.Candidate.Contains("udp"));

            UdpInfo remoteSrflxInfo, localSrflxInfo, localUdpInfo;

            // Get Remote Srflx UDP Info
            ExtractUdpInfo(remoteCandidates, (c) => c.Contains("typ srflx"), out remoteSrflxInfo);
            // Get Local Srflx UDP Info
            ExtractUdpInfo(localCandidates, (c) => c.Contains("typ srflx"), out localSrflxInfo);

            // check if they are on same NAT
            if (remoteSrflxInfo.ip == localSrflxInfo.ip) {
                if (_debugLog) Debug.Log($"Peers {peerId} appear to be on the same NAT, sending local UDP info");
                if (ExtractUdpInfo(localCandidates, (c) => c.Contains("typ host") && IpUtil.IsPrivateIPv4(c), out localUdpInfo)) {
                    // send private ip
                    SendSignalingMessage("udp-info", peerId, localUdpInfo);
                    _udpInfoSentMap[peerId] = true;
                    return;
                }
            }

            if (_debugLog) Debug.Log($"Peers {peerId} appear to be on different NATs, sending public UDP info");

            // send public ip
            if (ExtractUdpInfo(localCandidates, (c) => c.Contains("typ srflx"), out localUdpInfo)) {
                SendSignalingMessage("udp-info", peerId, localUdpInfo);
                _udpInfoSentMap[peerId] = true;
                return;
            }

            // send any
            if (ExtractUdpInfo(localCandidates, null, out localUdpInfo)) {
                SendSignalingMessage("udp-info", peerId, localUdpInfo);
                _udpInfoSentMap[peerId] = true;
                return;
            } else {
                if (_debugLog) Debug.LogWarning($"Failed to extract any UDP info to send to {peerId}");
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
                case "udp-info":
                    HandleUdpInfo(message);
                    break;
                case "connect":
                    yield return HandleConnectionRequest(message);
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
                //_remoteIceCandidatesMap.Remove(peerId);
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

        private void HandleUdpInfo(SignalingMessage message) {
            var udpInfo = JsonUtility.FromJson<UdpInfo>(message.body);
            _udpInfoBiMap[message.from] = udpInfo;

            if (_debugLog) Debug.Log($"Received UDP info from {message.from}: {udpInfo.ip}:{udpInfo.port}");
        }

        private IEnumerator HandleConnectionRequest(SignalingMessage message) {
            var request = JsonUtility.FromJson<ConnectionRequest>(message.body);

            _connectionKeyMap[request.target] = request.key;

            // start udp hole punching
            if (_udpInfoBiMap.TryGetByFirst(request.target, out var udpInfo)) {
                var remoteEndPoint = new IPEndPoint(IPAddress.Parse(udpInfo.ip), udpInfo.port);

                byte[] msg = System.Text.Encoding.UTF8.GetBytes($"hole-punching:{_userId}:{request.key}");
                int maxTotalJitter = _burstInterval / 10 * _bursts;
                for (int round = 0; round < _maxBurstRounds; round++) {
                    int totalJitter = 0;
                    for (int i = 0; i < _bursts; i++) {
                        _netManager.SendUnconnectedMessage(msg, remoteEndPoint);
                        if (_debugLog) Debug.Log($"Sent UDP hole punching message to {request.target} at {udpInfo.ip}:{udpInfo.port}");

                        int jitter = UnityEngine.Random.Range(-_burstInterval / 10, _burstInterval / 10);
                        totalJitter += jitter;
                        yield return new WaitForSeconds((_burstInterval + jitter) / 1000f);
                    }

                    // connection check
                    if (_peerBiMap.ContainsFirst(request.target) && _peerBiMap[request.target].ConnectionState == ConnectionState.Connected) {
                        yield break;
                    }

                    if (round < _maxBurstRounds - 1 && _debugLog) Debug.Log("Failed to establish UDP connection, retrying...");
                    yield return new WaitForSeconds((maxTotalJitter - totalJitter) / 1000f);
                }

                Debug.LogWarning($"Failed to establish UDP connection with {request.target} after multiple attempts.");
            }
        }

        public void OnPeerConnected(NetPeer peer) {
            if (_debugLog) Debug.Log("Connected to peer: " + peer.Address + ":" + peer.Port);


        }
        
        private void CleanUpPeer(string peerId) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                connection.Close();
                connection.Dispose();
                _peerConnectionMap.Remove(peerId);
            }

            _myIceCandidatesMap.Remove(peerId);
            _remoteIceCandidatesMap.Remove(peerId);
            _isConnectionEstablishedMap.Remove(peerId);
            _isDescriptionReadyMap.Remove(peerId);
            _latencyMap.Remove(peerId);
            _udpInfoBiMap.RemoveByFirst(peerId);
            _connectionKeyMap.Remove(peerId);
            _peerBiMap.RemoveByFirst(peerId);
            _udpInfoSentMap.Remove(peerId);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
            if (_debugLog) Debug.Log("Disconnected from peer: " + peer.Address + ":" + peer.Port);

            string peerId = _peerBiMap[peer];
            CleanUpPeer(peerId);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) {
            Debug.LogError($"Network error at {endPoint}: {socketError}");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) {
            string data = System.Text.Encoding.UTF8.GetString(reader.GetRemainingBytes());
            if (_debugLog) Debug.Log($"Received UDP message from {peer.Address}:{peer.Port}: {data}");

            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) {
            string message = System.Text.Encoding.UTF8.GetString(reader.GetRemainingBytes());
            var parts = message.Split(':');
            if (_debugLog) Debug.Log($"Received Unconnected UDP message from {remoteEndPoint}: {message}");

            // Handle hole punching message
            if (parts[0] == "hole-punching" && _connectionKeyMap.TryGetValue(parts[1], out var key) && parts[2] == key) {
                _netManager.Connect(remoteEndPoint, key);
            }
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) {
            if (_peerBiMap.TryGetBySecond(peer, out var peerId)) {
                _latencyMap[peerId] = latency;
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

        void Update() {
            _netManager?.PollEvents();

            #if !UNITY_WEBGL || UNITY_EDITOR
            _signaling?.DispatchMessageQueue();
            #endif

            while (_incomingMessageQueue.TryDequeue(out var message)) {
                StartCoroutine(HandleSignalingMessage(message));
            }
        }

        public void OnConnectionRequest(LiteNetLib.ConnectionRequest request) {
            if (_debugLog) Debug.Log("Received connection request from " + request.RemoteEndPoint);

            var udpInfo = new UdpInfo {
                ip = request.RemoteEndPoint.Address.ToString(),
                port = request.RemoteEndPoint.Port
            };

            if (_udpInfoBiMap.TryGetBySecond(udpInfo, out var peerId)) {
                request.AcceptIfKey(_connectionKeyMap[peerId]);
            }
            else {
                request.Reject();
                if (_debugLog) Debug.Log("Rejected connection request from " + request.RemoteEndPoint);
            }
        }

        private void CleanUp() {
            if (_signaling != null) DisconnectServer();

            if (_netManager == null) return;
            _netManager.DisconnectAll();
            _netManager.Stop();
            _netManager = null;
        }

        void OnApplicationQuit() {
            CleanUp();
        }

        void OnDestroy() {
            CleanUp();
        }
    }
}