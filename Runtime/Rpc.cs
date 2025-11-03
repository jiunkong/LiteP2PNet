using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteP2PNet {
    [AttributeUsage(AttributeTargets.Method)]
    public class RpcMethod : Attribute {
        public string Id { get; }
        public RpcMethod(string id = null) {
            Id = id;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class RpcProperty : Attribute {
        public string Id { get; }
        public bool? CanRead { get; }
        public bool? CanWrite { get; }
        public RpcProperty(string id = null, bool? canRead = null, bool? canWrite = null) {
            Id = id;
            CanRead = canRead;
            CanWrite = canWrite;
        }
    }

    public static class RpcRegistry {
        private static readonly PairMap<string, MethodInfo> _rpcMethods = new();
        private static readonly PairMap<string, PropertyInfo> _rpcProperties = new();
        private static readonly Dictionary<string, (bool, bool)> _rpcPropertyPermissions = new();

        private static string GetMethodFullName(MethodInfo method) => $"{method.DeclaringType.FullName}.{method.Name}";
        private static string GetPropertyFullName(PropertyInfo property) => $"{property.DeclaringType.FullName}.{property.Name}";

        static RpcRegistry() {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes());

            foreach (var type in types) {
                var methods = type.GetMethods().Where(m => m.IsDefined(typeof(RpcMethod)));
                var properties = type.GetProperties().Where(p => p.IsDefined(typeof(RpcProperty)));

                foreach (var method in methods) {
                    var attr = method.GetCustomAttribute<RpcMethod>();
                    string fullname = GetMethodFullName(method);
                    string id = attr.Id ?? fullname;

                    if (_rpcMethods.ContainsFirst(id)) {
                        throw new InvalidOperationException(
                            $"Packet ID '{id}' is already registered by method '{GetMethodFullName(_rpcMethods[id])}'. "
                        );
                    }

                    _rpcMethods[id] = method;
                }

                foreach (var property in properties) {
                    var attr = property.GetCustomAttribute<RpcProperty>();
                    string fullname = GetPropertyFullName(property);
                    string id = attr.Id ?? fullname;

                    if (_rpcProperties.ContainsFirst(id)) {
                        throw new InvalidOperationException(
                            $"Packet ID '{id}' is already registered by property '{GetPropertyFullName(_rpcProperties[id])}'. "
                        );
                    }

                    _rpcProperties[id] = property;

                    bool canRead = property.CanRead;
                    bool canWrite = property.CanWrite;

                    if (attr.CanRead != null) canRead &= attr.CanRead.Value;
                    if (attr.CanWrite != null) canWrite &= attr.CanWrite.Value;

                    _rpcPropertyPermissions[id] = (canRead, canWrite);
                }
            }
        }

        public static MethodInfo GetRpcMethod(string id) => _rpcMethods.TryGetByFirst(id, out var method) ? method : null;
        public static PropertyInfo GetRpcProperty(string id) => _rpcProperties.TryGetByFirst(id, out var property) ? property : null;

        public static string GetRpcMethodId(MethodInfo method) => _rpcMethods.TryGetBySecond(method, out var id) ? id : null;
        public static string GetRpcPropertyId(PropertyInfo property) => _rpcProperties.TryGetBySecond(property, out var id) ? id : null;

        public static bool CanReadRpcProperty(string id) => _rpcPropertyPermissions[id].Item1;
        public static bool CanWriteRpcProperty(string id) => _rpcPropertyPermissions[id].Item2;
    }
}