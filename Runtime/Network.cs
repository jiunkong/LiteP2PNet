using Unity.WebRTC;
using NativeWebSocket;
using System.Collections;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using MessagePack;
using System.Linq;

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

        private WebSocket _signaling;
        private Dictionary<string, RTCPeerConnection> _peerConnectionMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap = new();
        private ConcurrentQueue<SignalingMessage> _incomingMessageQueue = new();
        private Dictionary<string, bool> _isDescriptionReadyMap = new();

        public Action<string> onPeerConnected = null;
        public Action<string> onPeerDisconnected = null;

        private string _userId;
        private string _serverUrl;

        private List<RTCIceServer> _iceServers = new();

        public bool isConnectedToServer { get; private set; } = false;

        private bool _debugLog = false;

        private Dictionary<string, List<RTCDataChannel>> _dataChannelListMap = new();

        private class HandlerGroup {
            public Action<string, byte[], SendOption?> bytesHandler;
            public Dictionary<string, Action<string, long, SendOption?>> signalHandlers = new();
            public Dictionary<string, Action<string, long, object, SendOption?>> packetHandlerWrappers = new();
            public Dictionary<(string, Delegate), Action<string, long, object, SendOption?>> packetHandlerCache = new();
        }

        private static Func<object, Type, byte[]> packetSerializer = PacketRegistry.jsonPacketSerializer;

        private static Func<byte[], Type, object> packetDeserializer = PacketRegistry.jsonPacketDeserializer;

        private Dictionary<string, HandlerGroup> _handlerMap = new();

        private NetworkIdRegistry networkIdRegistry;

        public NetworkId AllocateNetworkId(RpcBehaviour rpcBehaviour) => networkIdRegistry.AllocateNetworkId(rpcBehaviour);
        public void ReleaseNetworkId(NetworkId networkId) => networkIdRegistry.ReleaseNetworkId(networkId);

        #region Packet Serialization
        public void UseJsonUtilityPacketSerializer() {
            packetSerializer = PacketRegistry.jsonPacketSerializer;
            packetDeserializer = PacketRegistry.jsonPacketDeserializer;
        }

        public void UseMessagePackPacketSerializer() {
            packetSerializer = PacketRegistry.msgpackPacketSerializer;
            packetDeserializer = PacketRegistry.msgpackPacketDeserializer;
        }

        public void UseCustomPacketSerializer(Func<object, Type, byte[]> serializer, Func<byte[], Type, object> deserializer) {
            packetSerializer = serializer;
            packetDeserializer = deserializer;
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

        #region Signaling

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
                var message = Encoding.UTF8.GetString(bytes);
                var signalingMessage = JsonUtility.FromJson<SignalingMessage>(message);
                _incomingMessageQueue.Enqueue(signalingMessage);
            };
        }

        public IEnumerator ConnectServerAsync(Dictionary<string, string> headers, string userIdKey = "user-id", float timeout = 10f) {
            headers.Add(userIdKey, _userId);
            _signaling = new WebSocket(_serverUrl, headers);

            _dataChannelListMap = new();
            _handlerMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageQueue = new();

            networkIdRegistry = new NetworkIdRegistry(_userId);

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
                    HandleUserChange(message);
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

        private void HandleUserChange(SignalingMessage message) {
            var userChangeData = JsonUtility.FromJson<UserChangeData>(message.body);
            if (userChangeData.type == "join") {
                onPeerConnected?.Invoke(userChangeData.target);
            } else if (userChangeData.type == "leave") {
                onPeerDisconnected?.Invoke(userChangeData.target);
            }
        }

        #endregion

        #region Peer Connection
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
            _dataChannelListMap.Remove(peerId);
            _handlerMap.Remove(peerId);
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

            int offset = 1;
            switch (rawdata[0] >> 6) {
            case (byte)DataType.Byte:
                handler.bytesHandler?.Invoke(peerId, rawdata[Utils.GetRemainingByteRange(offset)], option);
                break;
            case (byte)DataType.Signal: {
                    byte[] timestampBytes = rawdata[Utils.GetByteRange(ref offset, sizeof(long))];
                    long timestamp = BitConverter.ToInt64(timestampBytes, 0);
                    if (handler.signalHandlers.TryGetValue(Encoding.UTF8.GetString(rawdata[Utils.GetRemainingByteRange(offset)]), out var signalHandler))
                        signalHandler?.Invoke(peerId, timestamp, option);
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

                    var data = packetDeserializer(packetData, packetType);

                    if (handler.packetHandlerWrappers.TryGetValue(packetId, out var packetHandlerWrapper)) {
                        packetHandlerWrapper?.Invoke(peerId, timestamp, data, option);
                    }
                }
                break;
            case (byte)DataType.RPC:
                HandleRpc(peerId, ref offset, rawdata, option);
                break;
            }
        }

        #endregion

        #region Send
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

        public bool SendSignal(string peerId, string signalName, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Signal << 6;
            prefix &= (byte)((byte)option << 4);

            byte[] rawdata = Encoding.UTF8.GetBytes(signalName);

            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            List<byte> _data = new() { prefix };

            _data.AddRange(timestamp);
            _data.AddRange(rawdata);

            return SendData(peerId, _data.ToArray(), option);
        }

        public bool SendPacket<T>(string peerId, T packet, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.Packet << 6;
            prefix &= (byte)((byte)option << 4);

            byte[] rawdata = packetSerializer(packet, typeof(T));

            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            List<byte> _data = new() { prefix };

            _data.AddRange(timestamp);
            Utils.AppendData(ref _data, packet);
            _data.AddRange(rawdata);

            return SendData(peerId, _data.ToArray(), option);
        }
        #endregion

        #region Handler

        public void RegisterBytesHandler(string peerId, Action<string, byte[], SendOption?> handler) {
            if (!_handlerMap.ContainsKey(peerId)) {
                _handlerMap[peerId] = new();
            }

            _handlerMap[peerId].bytesHandler += handler;
        }

        public bool UnregisterBytesHandler(string peerId, Action<string, byte[], SendOption?> handler) {
            if (_handlerMap.TryGetValue(peerId, out var group)) {
                group.bytesHandler -= handler;
                return true;
            }
            return false;
        }

        public void RegisterSignalHandler(string peerId, string signalName, Action<string, long, SendOption?> handler) {
            if (!_handlerMap.ContainsKey(peerId)) {
                _handlerMap[peerId] = new();
            }

            _handlerMap[peerId].signalHandlers[signalName] += handler;
        }

        public bool UnregisterSignalHandler(string peerId, string signalName, Action<string, long, SendOption?> handler) {
            if (_handlerMap.TryGetValue(peerId, out var group)) {
                group.signalHandlers[signalName] -= handler;
                return true;
            }
            return false;
        }

        public void RegisterPacketHandler<T>(string peerId, Action<string, long, T, SendOption?> handler) {
            if (!_handlerMap.ContainsKey(peerId)) {
                _handlerMap[peerId] = new();
            }

            var group = _handlerMap[peerId];
            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            Action<string, long, object, SendOption?> wrapper = (string pId, long timestamp, object packet, SendOption? option) => {
                handler(pId, timestamp, (T)packet, option);
            };

            group.packetHandlerCache[(packetId, handler)] = wrapper;

            group.packetHandlerWrappers[packetId] += wrapper;
        }

        public bool UnregisterPacketHandler<T>(string peerId, Action<string, long, T, SendOption?> handler) {
            if (_handlerMap.TryGetValue(peerId, out var group)) {
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

        private bool SendRpcResult(string peerId, uint requestId, Type returnType, object result, SendOption option) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Return;

            List<byte> _data = new() { prefix };

            Utils.AppendData(ref _data, requestId);

            TypeWrapper typeWrapper = new(returnType);
            Utils.AppendData(ref _data, typeWrapper);

            if (returnType != typeof(void)) Utils.AppendData(returnType, ref _data, result);

            return SendData(peerId, _data.ToArray(), option);
        }

        private void HandleRpc(string peerId, ref int offset, byte[] rawdata, SendOption? option) {
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
                }
                else {
                    if (!networkId.HasValue) throw new RpcUnknownIdException($"RpcMethod {methodInfo.DeclaringType.FullName}.{methodInfo.Name} is not a static method.");
                    var obj = networkIdRegistry.FindRpcBehaviour(networkId.Value);
                    if (obj == null) throw new RpcUnknownIdException($"Cannot find RpcBehaviour with NetworkId '{networkId}'");
                    result = methodInfo.Invoke(obj, args.ToArray());
                }
                SendRpcResult(peerId, requestId, methodInfo.ReturnType, result, option ?? SendOption.OrderedReliable);
            }
            else if (rpcType == RpcType.Return) {
                var requestId = Utils.ParseData<uint>(ref offset, rawdata);

                var typewrapper = Utils.ParseData<TypeWrapper>(ref offset, rawdata);
                var type = typewrapper.Type;

                object result = null; 
                if (type != typeof(void)) result = Utils.ParseData(type, ref offset, rawdata);
                networkIdRegistry.RunCallback(requestId, result);
                networkIdRegistry.ReleaseRequestId(requestId);
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
                    var obj = networkIdRegistry.FindRpcBehaviour(networkId.Value);
                    if (obj == null) throw new RpcUnknownIdException($"Cannot find RpcBehaviour with NetworkId '{networkId}'");
                    result = propertyInfo.GetValue(obj);
                }
                
                SendRpcResult(peerId, requestId, propertyInfo.PropertyType, result, option ?? SendOption.OrderedReliable);
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

                if (propertyInfo.SetMethod?.IsStatic == true) propertyInfo.SetValue(null, value);
                else {
                    var obj = networkIdRegistry.FindRpcBehaviour(networkId.Value);
                    if (obj == null) throw new RpcUnknownIdException($"Cannot find RpcBehaviour with NetworkId '{networkId}'");
                    propertyInfo.SetValue(obj, value);
                }
                
                SendRpcResult(peerId, requestId, typeof(void), null, option ?? SendOption.OrderedReliable);
            }
            else if (rpcType == RpcType.Instanciate) {

            }
            else if (rpcType == RpcType.Destroy) {

            }
            else if (rpcType == RpcType.Error) {

            }
        }

        #region RPC Call Method
        public bool CallRpcStaticMethod<T>(string peerId, SendOption option, RpcCall rpcCall, Action<T> callback)
            => CallRpcMethod(peerId, networkId: null, option, rpcCall, callback);
        public async Task<T> CallRpcStaticMethodAsync<T>(string peerId, SendOption option, RpcCall rpcCall)
            => await CallRpcMethodAsync<T>(peerId, networkId: null, option, rpcCall);
        public IEnumerator CallRpcStaticMethod<T>(string peerId, SendOption option, RpcCall rpcCall) {
            yield return CallRpcMethod<T>(peerId, networkId: null, option, rpcCall);
        }

        public IEnumerator CallRpcMethod<T>(string peerId, RpcBehaviour rpcBehaviour, SendOption option, RpcCall rpcCall) {
            yield return CallRpcMethod<T>(peerId, rpcBehaviour.networkId, option, rpcCall);
        }
        public IEnumerator CallRpcMethod<T>(string peerId, NetworkId? networkId, SendOption option, RpcCall rpcCall) {
            T result;
            bool done = false;
            CallRpcMethod(peerId, networkId, option, rpcCall, (T obj) => {
                result = obj;
                done = true;
            });
            yield return new WaitUntil(() => done);
        }

        public async Task<T> CallRpcMethodAsync<T>(string peerId, RpcBehaviour rpcBehaviour, SendOption option, RpcCall rpcCall) {
            var tcs = new TaskCompletionSource<T>();
            CallRpcMethod(peerId, rpcBehaviour, option, rpcCall, (T obj) => tcs.SetResult(obj));
            return await tcs.Task;
        }
        public async Task<T> CallRpcMethodAsync<T>(string peerId, NetworkId? networkId, SendOption option, RpcCall rpcCall) {
            var tcs = new TaskCompletionSource<T>();
            CallRpcMethod(peerId, networkId, option, rpcCall, (T obj) => tcs.SetResult(obj));
            return await tcs.Task;
        }

        public bool CallRpcMethod<T>(string peerId, RpcBehaviour rpcBehaviour, SendOption option, RpcCall rpcCall, Action<T> callback)
            => CallRpcMethod(peerId, rpcBehaviour.networkId, option, rpcCall, (object obj) => callback((T)obj));
        public bool CallRpcMethod<T>(string peerId, NetworkId? networkId, SendOption option, RpcCall rpcCall, Action<T> callback)
            => CallRpcMethod(peerId, networkId, option, rpcCall, (object obj) => callback((T)obj));

        private bool CallRpcMethod(string peerId, NetworkId? networkId, SendOption option, RpcCall rpcCall, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Call;

            if (RpcRegistry.GetRpcMethod(rpcCall.methodId) == null) throw new Exception($"Method '{rpcCall.methodId}' is not a rpc method. Make sure it is decorated with [RpcMethod] attribute.");

            List<byte> _data = new() { prefix };

            var requestId = networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            if (networkId == null) Utils.AppendEmptySequence(ref _data);
            else Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, rpcCall.methodId);

            foreach (var arg in rpcCall.parameters) {
                var type = arg.GetType();
                var typewrapper = new TypeWrapper(type);
                Utils.AppendData(ref _data, typewrapper);

                Utils.AppendData(ref _data, arg);
            }

            return SendData(peerId, _data.ToArray(), option);
        }
        #endregion

        #region RPC Get Property

        public bool GetRpcStaticProperty<T>(string peerId, SendOption option, string propertyId, Action<T> callback)
            => GetRpcProperty(peerId, networkId: null, propertyId, option, callback);
        public async Task<T> GetRpcStaticPropertyAsync<T>(string peerId, SendOption option, string propertyId)
            => await GetRpcPropertyAsync<T>(peerId, networkId: null, propertyId, option);
        public IEnumerator GetRpcStaticProperty<T>(string peerId, SendOption option, string propertyId) {
            yield return GetRpcProperty<T>(peerId, networkId: null, propertyId, option);
        }

        public IEnumerator GetRpcProperty<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, SendOption option)
            => GetRpcProperty<T>(peerId, rpcBehaviour.networkId, propertyId, option);
        public IEnumerator GetRpcProperty<T>(string peerId, NetworkId? networkId, string propertyId, SendOption option) {
            T result;
            bool done = false;
            GetRpcProperty(peerId, networkId, propertyId, option, (T obj) => {
                result = obj;
                done = true;
            });
            yield return new WaitUntil(() => done);
        }

        public async Task<T> GetRpcPropertyAsync<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, SendOption option)
            => await GetRpcPropertyAsync<T>(peerId, rpcBehaviour.networkId, propertyId, option);
        public async Task<T> GetRpcPropertyAsync<T>(string peerId, NetworkId? networkId, string propertyId, SendOption option) {
            var tcs = new TaskCompletionSource<T>();
            GetRpcProperty(peerId, networkId, propertyId, option, (T obj) => tcs.SetResult(obj));
            return await tcs.Task;
        }

        public bool GetRpcProperty<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, SendOption option, Action<T> callback)
            => GetRpcProperty(peerId, rpcBehaviour.networkId, propertyId, option, (object obj) => callback((T)obj));
        public bool GetRpcProperty<T>(string peerId, NetworkId? networkId, string propertyId, SendOption option, Action<T> callback)
            => GetRpcProperty(peerId, networkId, propertyId, option, (object obj) => callback((T)obj));

        private bool GetRpcProperty(string peerId, NetworkId? networkId, string propertyId, SendOption option, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Get;

            if (RpcRegistry.GetRpcProperty(propertyId) == null) throw new Exception($"Property '{propertyId}' is not a rpc property. Make sure it is decorated with [RpcProperty] attribute.");

            List<byte> _data = new() { prefix };

            var requestId = networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            if (networkId == null) Utils.AppendEmptySequence(ref _data);
            else Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, propertyId);

            return SendData(peerId, _data.ToArray(), option);
        }

        #endregion

        #region RPC Set Property
        public bool SetRpcStaticProperty<T>(string peerId, SendOption option, string propertyId, T value, Action callback)
            => SetRpcProperty(peerId, networkId: null, propertyId, value, option, callback);
        public async Task SetRpcStaticPropertyAsync<T>(string peerId, SendOption option, string propertyId, T value)
            => await SetRpcPropertyAsync(peerId, networkId: null, propertyId, value, option);
        public IEnumerator SetRpcStaticProperty<T>(string peerId, SendOption option, string propertyId, T value) {
            yield return SetRpcProperty(peerId, networkId: null, propertyId, value, option);
        }

        public IEnumerator SetRpcProperty<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, T value, SendOption option)
            => SetRpcProperty(peerId, rpcBehaviour.networkId, propertyId, value, option);
        public IEnumerator SetRpcProperty<T>(string peerId, NetworkId? networkId, string propertyId, T value, SendOption option) {
            bool done = false;
            SetRpcProperty(peerId, networkId, propertyId, value, option, () => done = true);
            yield return new WaitUntil(() => done);
        }

        public async Task SetRpcPropertyAsync<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, T value, SendOption option)
            => await SetRpcPropertyAsync(peerId, rpcBehaviour.networkId, propertyId, value, option);
        public async Task SetRpcPropertyAsync<T>(string peerId, NetworkId? networkId, string propertyId, T value, SendOption option) {
            var tcs = new TaskCompletionSource<object>();
            SetRpcProperty(peerId, networkId, propertyId, value, option, () => tcs.SetResult(null));
            await tcs.Task;
        }

        public bool SetRpcProperty<T>(string peerId, RpcBehaviour rpcBehaviour, string propertyId, T value, SendOption option, Action callback)
            => SetRpcProperty(peerId, rpcBehaviour.networkId, propertyId, value, option, (object obj) => callback());
        public bool SetRpcProperty<T>(string peerId, NetworkId? networkId, string propertyId, T value, SendOption option, Action callback)
            => SetRpcProperty(peerId, networkId, propertyId, value, option, (object obj) => callback());

        private bool SetRpcProperty(string peerId, NetworkId? networkId, string propertyId, object value, SendOption option, Action<object> callback) {
            byte prefix = 0;
            prefix &= (byte)DataType.RPC << 6;
            prefix &= (byte)((byte)option << 4);
            prefix &= (byte)RpcType.Set;

            if (RpcRegistry.GetRpcProperty(propertyId) == null) throw new Exception($"Property '{propertyId}' is not a rpc property. Make sure it is decorated with [RpcProperty] attribute.");

            List<byte> _data = new() { prefix };

            var requestId = networkIdRegistry.AllocateRequestId(callback);
            Utils.AppendData(ref _data, requestId);

            if (networkId == null) Utils.AppendEmptySequence(ref _data);
            else Utils.AppendData(ref _data, networkId);

            Utils.AppendData(ref _data, propertyId);

            var typewrapper = new TypeWrapper(value.GetType());
            Utils.AppendData(ref _data, typewrapper);

            Utils.AppendData(value.GetType(), ref _data, value);

            return SendData(peerId, _data.ToArray(), option);
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
            if (_signaling != null) DisconnectServer();
            networkIdRegistry.Dispose();
        }

        void OnApplicationQuit() {
            CleanUp();
        }

        void OnDestroy() {
            CleanUp();
        }
    }
}