using Unity.WebRTC;
using NativeWebSocket;
using System.Collections;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using MessagePack;
using UnityEditor;

namespace LiteP2PNet {
    public class Network : MonoBehaviour {
        public static bool useAssemblyQualifiedNameForTypes = false;

        private static Network _instance;
        public static Network Instance {
            get {
                if (_instance != null) return _instance;

                _instance = FindAnyObjectByType<Network>();
                if (_instance != null) return _instance;

                var obj = new GameObject("P2PClient (Singleton)");
                _instance = obj.AddComponent<Network>();
                DontDestroyOnLoad(obj);
                return _instance;
            }
        }

        public string Host { get; private set; }
        public bool IsHost => Host == _userId;
        public string[] Members { get; private set; }
        public string[] Peers => _peerConnectionMap.Keys.ToArray();

        private WebSocket _signaling;
        private Dictionary<string, RTCPeerConnection> _peerConnectionMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap = new();
        private ConcurrentQueue<SignalingMessage> _incomingMessageQueue = new();
        private Dictionary<string, bool> _isDescriptionReadyMap = new();

        public Action<string> OnUserJoined = null;
        public Action<string> OnUserLeft = null;
        public Action<string, string> OnHostChanged = null;
        public Action<string> OnPeerConnected = null;
        public Action<string> OnPeerDisconnected = null;

        private string _userId;
        private string _serverUrl;

        private List<RTCIceServer> _iceServers = new();

        public bool IsConnectedToServer { get; private set; } = false;

        private bool _debugLog = false;

        private Dictionary<string, List<RTCDataChannel>> _dataChannelListMap = new();

        private class HandlerGroup {
            public Action<string, byte[], SendOption?> bytesHandler;
            public Dictionary<string, Action<string, long, SendOption?>> signalHandlers = new();
            public Dictionary<string, Action<string, long, object, SendOption?>> packetHandlerWrappers = new();
            public Dictionary<(string, Delegate), Action<string, long, object, SendOption?>> packetHandlerCache = new();
        }

        private static IPacketSerializer _packetSerializer = new JsonUtilityPacketSerializer();

        private Dictionary<string, HandlerGroup> _handlerMap = new();

        private NetworkIdRegistry _networkIdRegistry;

        public NetworkId AllocateNetworkId<T>(T component) => _networkIdRegistry.AllocateNetworkId(component);
        public void RegisterNetworkId<T>(NetworkId networkId, T component) => _networkIdRegistry.RegisterNetworkId(networkId, component);
        public void ReleaseNetworkId(NetworkId networkId) => _networkIdRegistry.ReleaseNetworkId(networkId);

        #region Packet Serialization
        public static void UseJsonUtilityPacketSerializer() {
            _packetSerializer = new JsonUtilityPacketSerializer();
        }

        public static void UseCustomPacketSerializer(IPacketSerializer serializer) {
            _packetSerializer = serializer;
        }
        #endregion

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

        public async Task<double?> GetRTTAsync(string peerId) {
            var task = new TaskCompletionSource<double?>();
            StartCoroutine(GetRTT(peerId, result => task.SetResult(result)));
            return await task.Task;
        }

        public void Init(string serverUrl, string userId, StunServer[] stunServers = null, TurnServer[] turnServers = null, bool debugLog = false) {
            if (userId.Length > 256) throw new Exception("User ID must be less than 256 characters");

            _userId = userId;
            _serverUrl = serverUrl;
            IsConnectedToServer = false;
            _debugLog = debugLog;

            if (stunServers != null) {
                foreach (var stun in stunServers) {
                    _iceServers.Add(new RTCIceServer { urls = stun.urls });
                }
            }

            if (turnServers != null) {
                foreach (var turn in turnServers) {
                    _iceServers.Add(new RTCIceServer {
                        urls = turn.urls,
                        username = turn.username,
                        credential = turn.credential
                    });
                }
            }
        }

        #region Signaling

        private void SetupSignaling() {
            _signaling.OnOpen += () => {
                IsConnectedToServer = true;
                if (_debugLog) Debug.Log("Connected to signaling server");
            };

            _signaling.OnError += (e) => {
                if (_debugLog) Debug.LogError("Signaling error: " + e);
            };

            _signaling.OnClose += (e) => {
                IsConnectedToServer = false;
                if (_debugLog) Debug.Log("Disconnected from signaling server");
            };

            _signaling.OnMessage += (bytes) => {
                var message = Encoding.UTF8.GetString(bytes);
                var signalingMessage = JsonUtility.FromJson<SignalingMessage>(message);
                _incomingMessageQueue.Enqueue(signalingMessage);
            };
        }

        public IEnumerator JoinLobby(string lobbyId, Dictionary<string, string> headers, string userIdKey = "userId", string lobbyIdKey = "lobbyId", float timeout = 10f) {
            if (headers == null) headers = new();

            headers.Add(userIdKey, _userId);
            headers.Add(lobbyIdKey, lobbyId);
            _signaling = new WebSocket(_serverUrl, headers);

            _dataChannelListMap = new();
            _handlerMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageQueue = new();

            _networkIdRegistry = new NetworkIdRegistry(_userId);

            SetupSignaling();

            _signaling.Connect();

            float elapsed = 0f;
            
            while (!IsConnectedToServer && elapsed < timeout) {
                yield return null;
                elapsed += Time.deltaTime;
            }
            
            if (!IsConnectedToServer) {
                Debug.LogError("Failed to connect to signaling server");
            }
        }

        public void LeaveLobby() {
            _signaling.Close();
            _signaling = null;
        }

        private void SendSignalingMessage(string type, string to, object data) {
            if (!IsConnectedToServer) return;
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
                case "lobby-update":
                    HandleLobbyUpdate(message);
                    break;
                default:
                    if (_debugLog) Debug.LogWarning($"Unknown signaling message type: {message.type}");
                    break;
            }
        }

        private void HandleLobbyUpdate(SignalingMessage message) {
            var lobbyUpdateData = JsonUtility.FromJson<LobbyUpdateData>(message.body);

            switch (lobbyUpdateData.type) {
                case "join": {
                        Host = lobbyUpdateData.hostId;
                        Members = lobbyUpdateData.members;
                        OnUserJoined?.Invoke(lobbyUpdateData.target);
                        break;
                    }
                case "leave": {
                        if (Host != lobbyUpdateData.hostId) {
                            // Reconnect
                            DisconnectPeer(Host);
                            StartCoroutine(ConnectPeerAsync(lobbyUpdateData.hostId));
                            OnHostChanged?.Invoke(Host, lobbyUpdateData.hostId);
                        }

                        Host = lobbyUpdateData.hostId;
                        Members = lobbyUpdateData.members;
                        OnUserLeft?.Invoke(lobbyUpdateData.target);
                        break;
                    }
                case "init": {
                        Host = lobbyUpdateData.hostId;
                        Members = lobbyUpdateData.members;

                        if (Host != _userId) StartCoroutine(ConnectPeerAsync(Host));

                        break;
                    }
            }
        }

        #endregion

        #region Peer Connection
        private IEnumerator ConnectPeerAsync(string peerId) {
            SetupPeerConnection(peerId, true);

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

        private void DisconnectPeer(string peerId) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                if (connection.ConnectionState != RTCPeerConnectionState.Closed)
                    connection.Close();
                connection.Dispose();
                _peerConnectionMap.Remove(peerId);
            }

            _myIceCandidatesMap.Remove(peerId);
            _isDescriptionReadyMap.Remove(peerId);
            _dataChannelListMap.Remove(peerId);
            _handlerMap.Remove(peerId);
        }

        private void SetupPeerConnection(string peerId, bool isOfferer) {
            var config = new RTCConfiguration { iceServers = _iceServers.ToArray() };

            var connection = new RTCPeerConnection(ref config);

            _handlerMap[peerId] = new();

            if (isOfferer) {
                List<RTCDataChannel> channels = new() {
                    connection.CreateDataChannel("OrderedReliable", new RTCDataChannelInit { ordered = true }),
                    connection.CreateDataChannel("OrderedUnreliable", new RTCDataChannelInit { ordered = true, maxRetransmits = 0 }),
                    connection.CreateDataChannel("UnorderedReliable", new RTCDataChannelInit { ordered = false }),
                    connection.CreateDataChannel("UnorderedUnreliable", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 })
                };

                foreach (var channel in channels) {
                    channel.OnMessage += (rawdata) => {
                        HandleDataChannel(rawdata);
                    };
                }

                _dataChannelListMap.Add(peerId, channels);
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

            bool isConnected = false;
            connection.OnIceConnectionChange = state => {
                if (_debugLog) Debug.Log($"ICE connection state for {peerId}: {state}");
                switch (state) {
                    case RTCIceConnectionState.Connected:
                    case RTCIceConnectionState.Completed:
                        if (!isConnected) {
                            OnPeerConnected?.Invoke(peerId);
                            isConnected = true;
                        }
                        break;
                    case RTCIceConnectionState.Disconnected:
                        DisconnectPeer(peerId);
                        isConnected = false;
                        OnPeerDisconnected?.Invoke(peerId);
                        break;
                }
            };

            connection.OnDataChannel = channel => {
                if (_debugLog) Debug.Log($"New data channel for {peerId}: {channel.Label}");

                if (!_dataChannelListMap.ContainsKey(peerId)) {
                    _dataChannelListMap.Add(peerId, new() { null, null, null, null });
                }

                channel.OnMessage += (rawdata) => HandleDataChannel(rawdata);

                var channelList = _dataChannelListMap[peerId];
                switch (channel.Label) {
                    case "OrderedReliable":
                        channelList[0] = channel;
                        break;
                    case "OrderedUnreliable":
                        channelList[1] = channel;
                        break;
                    case "UnorderedReliable":
                        channelList[2] = channel;
                        break;
                    case "UnorderedUnreliable":
                        channelList[3] = channel;
                        break;
                }
            };

            _peerConnectionMap.Add(peerId, connection);
        }

        private IEnumerator HandleOffer(SignalingMessage message)
        {
            SetupPeerConnection(message.from, false);

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

        private void HandleIceCandidate(SignalingMessage message) {
            var candidateData = JsonUtility.FromJson<IceCandidateData>(message.body);
            var candidate = new RTCIceCandidate(new RTCIceCandidateInit {
                candidate = candidateData.candidate,
                sdpMid = candidateData.sdpMid,
                sdpMLineIndex = candidateData.sdpMLineIndex.ToNullable()
            });

            if (_isDescriptionReadyMap.ContainsKey(message.from) && _isDescriptionReadyMap[message.from]) {
                _peerConnectionMap[message.from].AddIceCandidate(candidate);
                Debug.Log($"Added Remote ICE Candidate: {candidate.Candidate}");
            }
            else {
                Debug.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");
            }

            if (_remoteIceCandidatesMap.ContainsKey(message.from)) {
                _remoteIceCandidatesMap[message.from].Add(candidate);
            }
            else {
                _remoteIceCandidatesMap.Add(message.from, new() { candidate });
            }
        }


        private void HandleDataChannel(byte[] rawdata) {
            int offset = 0;
            var endpoint = Utils.ParseData<DataEndPoint>(ref offset, rawdata);

            SendOption? option = null;

            switch (rawdata[offset] >> 4) {
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

            if (endpoint.receiver != _userId) {
                // Transfer data to destination if host
                if (IsHost && _dataChannelListMap.TryGetValue(endpoint.receiver, out var channels)) {
                    var channel = channels[(byte)option];
                    channel.Send(rawdata);
                }
                return;
            }

            // Destination is self

            if (!_handlerMap.TryGetValue(endpoint.sender, out var handler)) return;

            switch (rawdata[offset++] >> 6) {
            case (byte)DataType.Byte:
                handler.bytesHandler?.Invoke(endpoint.sender, rawdata[Utils.GetRemainingByteRange(offset)], option);
                break;
            case (byte)DataType.Signal: {
                    byte[] timestampBytes = rawdata[Utils.GetByteRange(ref offset, sizeof(long))];
                    long timestamp = BitConverter.ToInt64(timestampBytes, 0);
                    if (handler.signalHandlers.TryGetValue(Encoding.UTF8.GetString(rawdata[Utils.GetRemainingByteRange(offset)]), out var signalHandler))
                        signalHandler?.Invoke(endpoint.sender, timestamp, option);
                    break;
                }
            case (byte)DataType.Packet: {
                    byte[] timestampBytes = rawdata[Utils.GetByteRange(ref offset, sizeof(long))];
                    long timestamp = BitConverter.ToInt64(timestampBytes, 0);

                    string packetId = Utils.ParseData<string>(ref offset, rawdata);
                    Type packetType = PacketRegistry.GetPacketType(packetId);
                    if (packetType == null) {
                        throw new RpcUnknownIdException($"Received unknown packet ID: {packetId}");
                    }

                    byte[] packetData = rawdata[Utils.GetRemainingByteRange(offset)];

                    var data = _packetSerializer.Deserialize(packetData, packetType);

                    if (handler.packetHandlerWrappers.TryGetValue(packetId, out var packetHandlerWrapper)) {
                        packetHandlerWrapper?.Invoke(endpoint.sender, timestamp, data, option);
                    }
                }
                break;
            case (byte)DataType.RPC:
                HandleRpc(endpoint, ref offset, rawdata, option);
                break;
            }
        }

        #endregion

        #region Send
        private bool SendData(string userId, List<byte> data, SendOption option) {
            if (userId == _userId) return false;

            string destination = IsHost ? userId : Host;
            if (!_dataChannelListMap.TryGetValue(destination, out var channels)) return false;

            var channel = channels[(byte)option];
            if (channel == null) {
                Debug.LogError($"Channel for {option} is not ready");
                return false;
            }

            var endpoint = new DataEndPoint() {
                sender = _userId,
                receiver = userId
            };

            List<byte> dataWithEndPoint = new();
            Utils.AppendData(ref dataWithEndPoint, endpoint);
            dataWithEndPoint.AddRange(data);
            channel.Send(dataWithEndPoint.ToArray());
            return true;
        }

        public bool SendBytes(string userId, byte[] data, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Byte << 6;
            prefix &= (byte)((byte)option << 4);

            List<byte> _data = new() { prefix };
            
            _data.AddRange(data);

            return SendData(userId, _data, option);
        }

        public bool SendSignal(string userId, string signalName, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Signal << 6;
            prefix &= (byte)((byte)option << 4);

            byte[] rawdata = Encoding.UTF8.GetBytes(signalName);

            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            List<byte> _data = new() { prefix };

            _data.AddRange(timestamp);
            _data.AddRange(rawdata);

            return SendData(userId, _data, option);
        }

        public bool SendPacket<T>(string userId, T packet, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Packet << 6;
            prefix &= (byte)((byte)option << 4);

            byte[] rawdata = _packetSerializer.Serialize(packet, typeof(T));

            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            List<byte> _data = new() { prefix };

            _data.AddRange(timestamp);
            Utils.AppendData(ref _data, packet);
            _data.AddRange(rawdata);

            return SendData(userId, _data, option);
        }
        #endregion

        #region Handler

        public void RegisterBytesHandler(string userId, Action<string, byte[], SendOption?> handler) {
            if (!_handlerMap.ContainsKey(userId)) {
                _handlerMap[userId] = new();
            }

            _handlerMap[userId].bytesHandler += handler;
        }

        public bool UnregisterBytesHandler(string userId, Action<string, byte[], SendOption?> handler) {
            if (_handlerMap.TryGetValue(userId, out var group)) {
                group.bytesHandler -= handler;
                return true;
            }
            return false;
        }

        public void RegisterSignalHandler(string userId, string signalName, Action<string, long, SendOption?> handler) {
            if (!_handlerMap.ContainsKey(userId)) {
                _handlerMap[userId] = new();
            }

            _handlerMap[userId].signalHandlers[signalName] += handler;
        }

        public bool UnregisterSignalHandler(string userId, string signalName, Action<string, long, SendOption?> handler) {
            if (_handlerMap.TryGetValue(userId, out var group)) {
                group.signalHandlers[signalName] -= handler;
                return true;
            }
            return false;
        }

        public void RegisterPacketHandler<T>(string userId, Action<string, long, T, SendOption?> handler) {
            if (!_handlerMap.ContainsKey(userId)) {
                _handlerMap[userId] = new();
            }

            var group = _handlerMap[userId];
            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            Action<string, long, object, SendOption?> wrapper = (string pId, long timestamp, object packet, SendOption? option) => {
                handler(pId, timestamp, (T)packet, option);
            };

            group.packetHandlerCache[(packetId, handler)] = wrapper;

            group.packetHandlerWrappers[packetId] += wrapper;
        }

        public bool UnregisterPacketHandler<T>(string userId, Action<string, long, T, SendOption?> handler) {
            if (_handlerMap.TryGetValue(userId, out var group)) {
                string packetId = PacketRegistry.GetPacketId(typeof(T));
                if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

                if (group.packetHandlerCache.TryGetValue((packetId, handler), out var wrapper)) {
                    group.packetHandlerWrappers[packetId] -= wrapper;
                    group.packetHandlerCache.Remove((packetId, handler));
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region RPC

        private bool SendRpcResult(string userId, uint requestId, Type returnType, object result, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Return;

            List<byte> _data = new() { prefix };

            Utils.AppendData(ref _data, requestId);

            TypeWrapper typeWrapper = new(returnType);
            Utils.AppendData(ref _data, typeWrapper);

            if (returnType != typeof(void)) Utils.AppendData(returnType, ref _data, result);

            return SendData(userId, _data, option);
        }

        private bool SendNonStaticRpcByTarget(NetworkId networkId, RpcTarget target, List<byte> data, SendOption option) {
            // exclude self
            if (target._self) return true;

            if (target._owner) {
                if (networkId.ownerId == _userId) return true;
                return SendData(networkId.ownerId, data, option);
            }

            IEnumerable<string> targets = target.targets.Except(new[] { _userId });
            if (target._all) targets = Members.Except(new[] { _userId });

            bool result = true;
            foreach (var t in targets) {
                result &= SendData(t, data, option);
            }
            return result;
        }

        private void HandleRpc(DataEndPoint endpoint, ref int offset, byte[] rawdata, SendOption? option) {
            RpcType rpcType = (RpcType)(rawdata[0] & (0 | 0b1111));

            if (rpcType == RpcType.Call) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                NetworkId? networkId;
                if (Utils.CheckEmptySequence(ref offset, rawdata)) networkId = null;
                else networkId = Utils.ParseData<NetworkId>(ref offset, rawdata);

                string methodId = Utils.ParseData<string>(ref offset, rawdata);

                var methodInfo = RpcRegistry.GetRpcMethod(methodId);
                if (methodInfo == null) {
                    throw new RpcUnknownIdException($"Received unknown method ID: {methodId}");
                }

                List<object> args = new();
                List<Type> types = new();
                var methodArgTypes = methodInfo.GetParameters().Select(p => p.GetType()).ToList();

                while (offset < rawdata.Length) {
                    TypeWrapper argTypeWrapper = Utils.ParseData<TypeWrapper>(ref offset, rawdata);
                    Type argType = argTypeWrapper.Type;

                    if (argType == null) {
                        throw new RpcUnknownTypeException($"Procedure call with unknown type: {argTypeWrapper.TypeName}");
                    }

                    object arg = Utils.ParseData(argType, ref offset, rawdata);

                    types.Add(argType);
                    args.Add(arg);
                }

                bool fail = false;
                if (types.Count != methodArgTypes.Count) {
                    fail = true;
                }
                else {
                    for (int i = 0; i < types.Count; i++) {
                        if (types[i] != methodArgTypes[i]) {
                            fail = true;
                            break;
                        }
                    }
                }

                if (fail) throw new RpcArgumentException($"RpcMethod {methodInfo.DeclaringType.FullName}.{methodInfo.Name} cannot be called with the arguments {Utils.GetArgumentsTypeString(types)}. Expected {Utils.GetArgumentsTypeString(methodArgTypes)}");

                object result;
                if (methodInfo.IsStatic) {
                    result = methodInfo.Invoke(null, args.ToArray());
                    SendRpcResult(endpoint.sender, requestId, methodInfo.ReturnType, result, option ?? SendOption.OrderedReliable);
                }
                else {
                    if (!networkId.HasValue) throw new RpcUnknownIdException($"RpcMethod {methodInfo.DeclaringType.FullName}.{methodInfo.Name} is not a static method.");

                    if (_networkIdRegistry.TryFindRpcComponent(networkId.Value, out var component)) {
                        result = methodInfo.Invoke(component, args.ToArray());
                    }
                    else {
                        throw new RpcUnknownIdException($"Cannot find RpcComponent with NetworkId '{networkId}'");
                    }

                    if (networkId.Value.ownerId == _userId) {
                        SendRpcResult(endpoint.sender, requestId, methodInfo.ReturnType, result, option ?? SendOption.OrderedReliable);
                    }
                }        
            }
            else if (rpcType == RpcType.Return) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                var typewrapper = Utils.ParseData<TypeWrapper>(ref offset, rawdata);
                var type = typewrapper.Type;

                object result = null;
                if (type != typeof(void)) result = Utils.ParseData(type, ref offset, rawdata);
                _networkIdRegistry.RunCallback(requestId, result);
                _networkIdRegistry.ReleaseRequestId(requestId);
            }
            else if (rpcType == RpcType.Get) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                NetworkId? networkId;
                if (Utils.CheckEmptySequence(ref offset, rawdata)) networkId = null;
                else networkId = Utils.ParseData<NetworkId>(ref offset, rawdata);

                string propertyId = Utils.ParseData<string>(ref offset, rawdata);

                var propertyInfo = RpcRegistry.GetRpcProperty(propertyId);
                if (propertyInfo == null) {
                    throw new RpcUnknownIdException($"Received unknown property ID: {propertyId}");
                }

                object result;
                if (propertyInfo.GetMethod?.IsStatic == true) result = propertyInfo.GetValue(null);
                else {
                    if (_networkIdRegistry.TryFindRpcComponent(networkId.Value, out var component)) {
                        result = propertyInfo.GetValue(component);
                    }
                    else {
                        throw new RpcUnknownIdException($"Cannot find RpcBehaviour with NetworkId '{networkId}'");
                    }
                }
                
                SendRpcResult(endpoint.sender, requestId, propertyInfo.PropertyType, result, option ?? SendOption.OrderedReliable);
            }
            else if (rpcType == RpcType.Set) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                NetworkId? networkId;
                if (Utils.CheckEmptySequence(ref offset, rawdata)) networkId = null;
                else networkId = Utils.ParseData<NetworkId>(ref offset, rawdata);

                string propertyId = Utils.ParseData<string>(ref offset, rawdata);

                var propertyInfo = RpcRegistry.GetRpcProperty(propertyId);
                if (propertyInfo == null) {
                    throw new RpcUnknownIdException($"Received unknown property ID: {propertyId}");
                }

                TypeWrapper typeWrapper = Utils.ParseData<TypeWrapper>(ref offset, rawdata);
                Type type = typeWrapper.Type;
                object value = Utils.ParseData(type, ref offset, rawdata);

                if (propertyInfo.SetMethod?.IsStatic == true) {
                    propertyInfo.SetValue(null, value);

                    SendRpcResult(endpoint.sender, requestId, typeof(void), null, option ?? SendOption.OrderedReliable);
                } else {
                    if (_networkIdRegistry.TryFindRpcComponent(networkId.Value, out var component)) {
                        propertyInfo.SetValue(component, value);
                    }
                    else {
                        throw new RpcUnknownIdException($"Cannot find RpcBehaviour with NetworkId '{networkId}'");
                    }

                    if (networkId.Value.ownerId == _userId) {
                        SendRpcResult(endpoint.sender, requestId, typeof(void), null, option ?? SendOption.OrderedReliable);
                    }
                }
            }
            else if (rpcType == RpcType.Instantiate) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                var prefabKey = Utils.ParseData<string>(ref offset, rawdata);
                var instData = Utils.ParseData<RpcInstantiationData>(ref offset, rawdata);

                var networkIds = instData.GetNetworkIds();
                var initArgs = instData.GetRpcInitArgs();

                var prefab = RpcFeature.LoadPrefab(prefabKey);

                var rpcObjs = prefab.GetComponents<IBaseRpcObject>();
                foreach (var rpcObj in rpcObjs) {
                    var objType = rpcObj.GetType();
                    if (objType.IsGenericMethodParameter && objType.GetGenericTypeDefinition() == typeof(IRpcObject<>)) {
                        var genericType = objType.GetGenericArguments()[0];
                        var networkId = networkIds[genericType];

                        var instanceType = typeof(RpcInstance<>).MakeGenericType(genericType);
                        var instance = prefab.AddComponent(instanceType) as IRpcInstance;

                        if (initArgs.TryGet(genericType, out var args)) {
                            instance.Init(false, prefabKey, networkId, args);
                        } else {
                            throw new InvalidOperationException();
                        }
                    }
                }
            }
            else if (rpcType == RpcType.Destroy) {

            }
            else if (rpcType == RpcType.Error) {

            }
        }

        #region RPC Call Method
        internal bool CallRpcStaticMethod(string userId, SendOption option, RpcCall rpcCall, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Call;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendEmptySequence(ref _data);

            Utils.AppendData(ref _data, rpcCall.methodId);

            foreach (var arg in rpcCall.parameters) {
                var type = arg.GetType();
                var typewrapper = new TypeWrapper(type);
                Utils.AppendData(ref _data, typewrapper);

                Utils.AppendData(ref _data, arg);
            }

            return SendData(userId, _data, option);
        }

        internal bool CallRpcMethod<ClsTy>(NetworkId networkId, RpcTarget target, SendOption option, RpcCall rpcCall, Action<object> callback, ClsTy cls) {
            bool isOwner = networkId.ownerId == _userId;
            if (target._self || target._all || (target._owner && isOwner) || target.targets.Contains(_userId)) {
                var methodInfo = RpcRegistry.GetRpcMethod(rpcCall.methodId);
                var result = methodInfo.Invoke(cls, rpcCall.parameters);

                if (target._self || isOwner || target.targets.Count() == 1) {
                    callback?.Invoke(result);
                }

                if (target._self) return true;
            }

            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Call;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, rpcCall.methodId);

            foreach (var arg in rpcCall.parameters) {
                var type = arg.GetType();
                var typewrapper = new TypeWrapper(type);
                Utils.AppendData(ref _data, typewrapper);

                Utils.AppendData(ref _data, arg);
            }

            return SendNonStaticRpcByTarget(networkId, target, _data, option);
        }
        #endregion

        #region RPC Get Property

        internal bool GetRpcStaticProperty(string userId, string propertyId, SendOption option, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Get;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendEmptySequence(ref _data);

            Utils.AppendData(ref _data, propertyId);

            return SendData(userId, _data, option);
        }

        internal bool GetRpcProperty<ClsTy>(NetworkId networkId, string propertyId, SendOption option, Action<object> callback, ClsTy cls) {
            if (networkId.ownerId == _userId) {
                if (!RpcRegistry.CanReadRpcProperty(propertyId)) {
                    throw new Exception("something");
                }
                var propertyInfo = RpcRegistry.GetRpcProperty(propertyId);
                var result = propertyInfo.GetValue(cls);
                callback?.Invoke(result);
                return true;
            }

            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Get;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, propertyId);

            return SendData(networkId.ownerId, _data, option);
        }

        #endregion

        #region RPC Set Property
        internal bool SetRpcStaticProperty(string userId, string propertyId, object value, SendOption option, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Set;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendEmptySequence(ref _data);

            Utils.AppendData(ref _data, propertyId);

            var typewrapper = new TypeWrapper(value.GetType());
            Utils.AppendData(ref _data, typewrapper);

            Utils.AppendData(value.GetType(), ref _data, value);

            return SendData(userId, _data, option);
        }

        internal bool SetRpcProperty<ClsTy>(NetworkId networkId, RpcTarget target, string propertyId, object value, SendOption option, Action<object> callback, ClsTy cls) {
            bool isOwner = networkId.ownerId == _userId;
            if (target._self || target._all || (target._owner && isOwner) || target.targets.Contains(_userId)) {
                if (!RpcRegistry.CanWriteRpcProperty(propertyId)) {
                    throw new Exception("something");
                }
                var propertyInfo = RpcRegistry.GetRpcProperty(propertyId);
                propertyInfo.SetValue(cls, value);

                if (target._self || isOwner || target.targets.Count() == 1) {
                    callback?.Invoke(null);
                }

                if (target._self) return true;
            }

            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Set;

            //if (RpcRegistry.GetRpcProperty(propertyId) == null) throw new Exception($"Property '{propertyId}' is not a rpc property. Make sure it is decorated with [RpcProperty] attribute.");

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, propertyId);

            var typewrapper = new TypeWrapper(value.GetType());
            Utils.AppendData(ref _data, typewrapper);

            Utils.AppendData(value.GetType(), ref _data, value);

            return SendNonStaticRpcByTarget(networkId, target, _data, option);
        }

        #endregion

        #region RPC Instantiate

        internal bool InstantiateRpcObject(string prefabKey, RpcInstantiationTarget target, RpcInstantiationData instData, Action<object> callback, SendOption option) {
            var targets = target.targets.Except(new[] { _userId });
            if (target._all) targets = Members.Except(new[] { _userId });

            if (targets.Count() == 0) return true;

            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Instantiate;

            List<byte> _data = new() { prefix };

            var requestId = _networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            Utils.AppendData(ref _data, prefabKey);
            Utils.AppendData(ref _data, instData);

            bool result = true;
            foreach (var userId in targets) {
                result &= SendData(userId, _data, option);
            }

            return result;
        }

        #endregion

        #endregion

        void Update() {
            #if !UNITY_WEBGL || UNITY_EDITOR
            _signaling?.DispatchMessageQueue();
            #endif

            while (_incomingMessageQueue.TryDequeue(out var message)) {
                StartCoroutine(HandleSignalingMessage(message));
            }
        }

        private void CleanUp() {
            if (_signaling != null) LeaveLobby();
            _networkIdRegistry.Dispose();
        }

        void OnApplicationQuit() {
            CleanUp();
        }

        void OnDestroy() {
            CleanUp();
        }
    }
}