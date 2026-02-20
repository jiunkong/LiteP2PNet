using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEditor.UI;

/**
[DataType(2), SendOption(2), None(4)] [Data]
**/

namespace LiteP2PNet {
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConnectionFailedReason {
        IceConnectionFailed,

        [EnumMember(Value = "USER_NOT_FOUND")]
        UserNotFound,
        [EnumMember(Value = "LOBBY_NOT_FOUND")]
        LobbyNotFound,
        [EnumMember(Value = "LOBBY_FULL")]
        LobbyIsFull,
        [EnumMember(Value = "HOST_NOT_FOUND")]
        HostNotFound,     
        [EnumMember(Value = "BAD_CONNECTION")]
        BadConnection,
    }

    public enum SendOption : byte {
        OrderedReliable = 0b00,
        OrderedUnreliable = 0b01,
        UnorderedReliable = 0b10,
        UnorderedUnreliable = 0b11
    }

    internal enum DataType : byte {
        Byte = 0b00,
        Signal = 0b01,
        Packet = 0b10,
        RPC = 0b11,
    }

    internal enum RpcType : byte {
        Call = 0b0000,
        Return = 0b0001,
        Get = 0b0010,
        Set = 0b0011,
        Instantiate = 0b0100,
        Destroy = 0b0101,
        Error = 0b1111
    }

    [MessagePackObject]
    public struct RpcCall {
        [Key(0)]
        public string methodId;
        [Key(1)]
        public object[] parameters;

        public RpcCall(string methodId, params object[] parameters) {
            this.methodId = methodId;
            this.parameters = parameters;
        }
    }

    [MessagePackObject]
    public struct DataEndPoint {
        [Key(0)]
        public string sender;
        [Key(1)]
        public string receiver;
    }

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum SignalingMsgType {
        [EnumMember(Value = "offer")]
        Offer,
        [EnumMember(Value = "answer")]
        Answer,
        [EnumMember(Value = "ice-candidate")]
        IceCandidate,
        [EnumMember(Value = "lobby-update")]
        LobbyUpdate,
        [EnumMember(Value = "connection-failed")]
        ConnectionFailed,
        [EnumMember(Value = "request-data")]
        RequestData,
        [EnumMember(Value = "response-data")]
        ResponseData,
        [EnumMember(Value = "apply-data")]
        ApplyData,
        [EnumMember(Value = "data-update")]
        DataUpdate
    }
    [Serializable]
    internal class SignalingMessage {
        public SignalingMsgType type;
        public string from;
        public string to;
        public string body;
    }

    [Serializable]
    internal class OfferAnswerData {
        public string sdp;
        public string type;
    }

    [Serializable]
    internal class IceCandidateData {
        public string candidate;
        public string sdpMid;
        public NullableInt sdpMLineIndex;
    }

    [Serializable]
    internal class LobbyUpdateDTO {
        public string type;
        public string target;
        public string lobby;
    }

    [Serializable]
    internal class ConnectionFailedDTO {
        public ConnectionFailedReason reason;
    }

    [Serializable]
    internal class DataRequestDTO {
        public string type;
        public string target;
    }

    [Serializable]
    internal class DataResponseDTO {
        public string type;
        public string target;
        public bool success;
        public string data;
    }

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum DataChangeType {
        [EnumMember(Value = "user-account")]
        UserAccount,
        [EnumMember(Value = "lobby-metadata")]
        LobbyMetadata,
        [EnumMember(Value = "lobby-state")]
        LobbyState
    }

    [Serializable]
    internal class DataApplyDTO {
        public DataChangeType type;
        public string target;
        public string data;
    }

    [Serializable]
    internal class DataUpdateDTO {
        public DataChangeType type;
        public string data;
    }

    [Serializable]
    internal class LobbyMetadataUpdateDTO {
        public string name;
        public int? maxPlayers;
        public bool? isPrivate;
        public bool? isPlaying;
    }

    public struct StunServer {
        public string[] urls;
    }

    public struct TurnServer {
        public string[] urls;
        public string username;
        public string credential;
    }

    [MessagePackObject]
    public class RpcInstantiationData {
        [Key(0)]
        public Dictionary<TypeWrapper, (TypeWrapper, byte[])[]> initArgs = new();
        [Key(1)]
        public Dictionary<TypeWrapper, NetworkId> networkIds = new();

        public RpcInstantiationData() { }

        public RpcInstantiationData(Dictionary<Type, NetworkId> networkIds, RpcInitArgs args) {
            this.networkIds = networkIds.ToDictionary(x => new TypeWrapper(x.Key), x => x.Value);

            foreach(var key in args.initArgs.Keys) {
                var keyWrapper = new TypeWrapper(key);
                var argsWrapper = args.initArgs[key].Select(
                    x => {
                        var t = x.GetType();
                        return (new TypeWrapper(t), MessagePackSerializer.Serialize(t, x));
                    }
                ).ToArray();
                initArgs[keyWrapper] = argsWrapper;
            }
        }

        public RpcInitArgs GetRpcInitArgs() {
            var result = new RpcInitArgs();
            foreach(var key in initArgs.Keys) {
                var args = initArgs[key].Select(
                    x => MessagePackSerializer.Deserialize(x.Item1.Type, x.Item2)
                );
                result.Add(key.Type, args.ToArray());
            }
            return result;
        }

        public Dictionary<Type, NetworkId> GetNetworkIds() => networkIds.ToDictionary(x => x.Key.Type, x => x.Value);
    }
}