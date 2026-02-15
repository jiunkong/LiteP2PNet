using System;
using MessagePack;

namespace LiteP2PNet {
    public class RpcArgumentException : Exception {
        public RpcArgumentException(string message) : base(message) { }
    }

    public class RpcUnknownTypeException : Exception {
        public RpcUnknownTypeException(string message) : base(message) { }
    }

    public class RpcUnknownIdException : Exception {
        public RpcUnknownIdException(string message) : base(message) { }
    }
}