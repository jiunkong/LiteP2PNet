using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MessagePack;

namespace LiteP2PNet {
    internal static class Utils {
        public static Range GetByteRange(ref int offset, int size) {
            var range = offset..(offset + size);
            offset += size;
            return range;
        }

        public static Range GetRemainingByteRange(int offset) {
            return offset..^0;
        }

        public static string GetArgumentsTypeString(List<Type> types) => $"({string.Join(", ", types.Select(t => t.Name))})";

        public static object ParseData(Type type, ref int offset, byte[] rawdata) {
            byte[] lenBytes = rawdata[GetByteRange(ref offset, sizeof(int))];
            int len = BitConverter.ToInt32(lenBytes, 0);
            byte[] bytes = rawdata[GetByteRange(ref offset, len)];
            return MessagePackSerializer.Deserialize(type, bytes);
        }

        public static void AppendData<T>(ref List<byte> seq, T data) {
            byte[] bytes = MessagePackSerializer.Serialize(data);
            byte[] bytesLen = BitConverter.GetBytes(bytes.Length);
            seq.AddRange(bytesLen);
            seq.AddRange(bytes);
        }

        public static void AppendData(Type type, ref List<byte> seq, object data) {
            byte[] bytes = MessagePackSerializer.Serialize(type, data);
            byte[] bytesLen = BitConverter.GetBytes(bytes.Length);
            seq.AddRange(bytesLen);
            seq.AddRange(bytes);
        }

        public static T ParseData<T>(ref int offset, byte[] rawdata) {
            byte[] lenBytes = rawdata[GetByteRange(ref offset, sizeof(int))];
            int len = BitConverter.ToInt32(lenBytes, 0);
            byte[] bytes = rawdata[GetByteRange(ref offset, len)];
            return MessagePackSerializer.Deserialize<T>(bytes);
        }

        public static bool CheckEmptySequence(ref int offset, byte[] rawdata) {
            byte[] lenBytes = rawdata[GetByteRange(ref offset, sizeof(int))];
            int len = BitConverter.ToInt32(lenBytes, 0);

            if (len == 0) {
                offset += sizeof(int);
                return true;
            }
            else return false;
        }
        
        public static void AppendEmptySequence(ref List<byte> seq) {
            byte[] bytesLen = BitConverter.GetBytes((int)0);
            seq.AddRange(bytesLen);
        }
    }
    
    [MessagePackObject]
    internal class TypeWrapper {
        [Key(0)]
        public string TypeName { get; set; }
        [IgnoreMember]
        public Type Type {
            get => TypeName == null ? null : Type.GetType(TypeName);
            set => TypeName = Network.useAssemblyQualifiedNameForTypes ? value.AssemblyQualifiedName : value.FullName;
        }

        public TypeWrapper(Type type) => Type = type;
    }

    internal class PairMap<T1, T2> {
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