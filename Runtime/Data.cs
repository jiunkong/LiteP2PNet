using System;
using System.Collections.Generic;
using System.Linq;
using MessagePack;

/**
[DataType(2), SendOption(2), None(4)] [Data]
**/

namespace LiteP2PNet {
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
        public string sender;
        public string receiver;
    }

    // [MessagePackObject]
    // public struct RpcInstanciateInfo {
    //     [Key(0)]
    //     public string prefab;
    //     [Key(1)]
    //     public string 
    // }

    [Serializable]
    internal class SignalingMessage {
        public string type;
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
    internal class LobbyUpdateData {
        public string type;
        public string target;
        public string hostId;
        public string[] members;
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
        Dictionary<TypeWrapper, (TypeWrapper, byte[])[]> initArgs = new();
        [Key(1)]
        Dictionary<TypeWrapper, NetworkId> networkIds = new();

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