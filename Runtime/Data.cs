using System;

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
    internal class NullableInt {
        public int value;
        public bool hasValue;

        public NullableInt(int? v) {
            if (v == null) {
                hasValue = false;
                value = 0;
            }
            else {
                value = v.Value;
                hasValue = true;
            }
        }

        public int? ToNullable() {
            return hasValue ? value : null;
        }
    }
}