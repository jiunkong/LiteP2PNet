using System;

namespace LiteP2PNet {
    [Serializable]
    public class SignalingMessage {
        public string type;
        public string from;
        public string to;
        public string body;
    }

    [Serializable]
    public class OfferAnswerData {
        public string sdp;
        public string type;
    }

    [Serializable]
    public class IceCandidateData {
        public string candidate;
        public string sdpMid;
        public NullableInt sdpMLineIndex;
    }

    [Serializable]
    public class ConnectionRequest {
        public string target;
        public string key;
    }

    [Serializable]
    public class UdpInfo {
        public string ip;
        public int port;
    }

    [Serializable]
    public class NullableInt {
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