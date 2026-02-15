using System;
using System.Collections.Generic;
using MessagePack;

namespace LiteP2PNet {
    [MessagePackObject]
    public struct NetworkId: IEquatable<NetworkId> {
        [Key(0)]
        public string ownerId { get; private set; }
        [Key(1)]
        public ulong objectId { get; private set; }

        public NetworkId(string ownerId, ulong objectId) {
            this.ownerId = ownerId;
            this.objectId = objectId;
        }

        public bool Equals(NetworkId other) {
            return ownerId == other.ownerId && objectId == other.objectId;
        }
    }

    public class NetworkIdRegistry : IDisposable {
        private string _ownerId;
        private ulong _nextObjectId = 0;
        private uint _nextRequestId = 0;
        private SortedSet<uint> _releasedRequestIds = new();
        private List<Action<object>> _requestCallbacks = new();
        private bool disposed = false;

        private Dictionary<NetworkId, object> _objectMap = new();

        public NetworkIdRegistry(string ownerId) {
            _ownerId = ownerId;
            _requestCallbacks.Add(null);   
        }

        public NetworkId AllocateNetworkId(object component) {
            if (disposed) throw new ObjectDisposedException(nameof(NetworkIdRegistry));

            var id = new NetworkId(_ownerId, _nextObjectId++);
            _objectMap.Add(id, component);
            return id;
        }

        public void RegisterNetworkId(NetworkId id, object component) => _objectMap.Add(id, component);

        public object FindRpcComponent(NetworkId id) => _objectMap[id];

        public bool TryFindRpcComponent(NetworkId id, out object component) => _objectMap.TryGetValue(id, out component);

        public void ReleaseNetworkId(NetworkId id) => _objectMap.Remove(id);

        public void Dispose() {
            if (disposed) return;
            disposed = true;
        }

        public uint AllocateRequestId(Action<object> handler) {
            if (_releasedRequestIds.Count > 0) {
                var id = _releasedRequestIds.Min;
                _releasedRequestIds.Remove(id);
                _requestCallbacks[(int)id] = handler;
                return id;
            }
            else {
                _requestCallbacks.Add(handler);
                return _nextRequestId++;
            }
        }

        public void RunCallback(uint requestId, object result) {
            int id = (int)requestId;
            if (id < _requestCallbacks.Count) _requestCallbacks[id]?.Invoke(result);
        }

        public void ReleaseRequestId(uint requestId) {
            _releasedRequestIds.Add(requestId);
            int id = (int)requestId;
            if (id < _requestCallbacks.Count) {
                _requestCallbacks[id] = null;
            }
        }
    }
}