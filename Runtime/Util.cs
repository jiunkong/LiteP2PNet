using System;
using System.Collections.Generic;

namespace LiteP2PNet {
    public static class Utils {
        public static Range GetByteRange(ref int offset, int size) {
            var range = offset..(offset + size);
            offset += size;
            return range;
        }

        public static Range GetRemainingByteRange(int offset) {
            return offset..^0;
        }
    }
     

    public class PairMap<T1, T2> {
    private readonly Dictionary<T1, T2> _map1 = new();
    private readonly Dictionary<T2, T1> _map2 = new();
    private readonly object _lock = new();

    public T2 this[T1 key1] {
        get {
            lock (_lock)
                return _map1[key1];
        }
        set {
            lock (_lock) {
                // 기존 쌍 정리
                if (_map1.TryGetValue(key1, out var oldKey2))
                    _map2.Remove(oldKey2);

                _map1[key1] = value;
                _map2[value] = key1;
            }
        }
    }

    public T1 this[T2 key2]
    {
        get {
            lock (_lock)
                return _map2[key2];
        }
        set {
            lock (_lock)
            {
                if (_map2.TryGetValue(key2, out var oldKey1))
                    _map1.Remove(oldKey1);

                _map2[key2] = value;
                _map1[value] = key2;
            }
        }
    }

    public bool TryAdd(T1 key1, T2 key2) {
        if (_map1.ContainsKey(key1) || _map2.ContainsKey(key2))
            return false;

        lock (_lock) {
            _map1[key1] = key2;
            _map2[key2] = key1;
        }
        return true;
    }

    public bool TryGetByFirst(T1 key1, out T2 value2) {
        lock (_lock) return _map1.TryGetValue(key1, out value2);
    }

    public bool TryGetBySecond(T2 key2, out T1 value1) {
        lock (_lock) return _map2.TryGetValue(key2, out value1);
    }

    public void RemoveByFirst(T1 key1) {
        lock (_lock) {
            if (_map1.TryGetValue(key1, out var key2)) {
                _map1.Remove(key1);
                _map2.Remove(key2);
            }
        }
    }

    public void RemoveBySecond(T2 key2) {
        lock (_lock) {
            if (_map2.TryGetValue(key2, out var key1)) {
                _map2.Remove(key2);
                _map1.Remove(key1);
            }
        }
    }

    public bool ContainsFirst(T1 key1) {
        lock(_lock) return _map1.ContainsKey(key1);
    }
    public bool ContainsSecond(T2 key2) {
        lock(_lock) return _map2.ContainsKey(key2);
    }
}
}