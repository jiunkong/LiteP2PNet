using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace LiteP2PNet {
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class)]
    public class RpcIdentifier : Attribute {
        public string Id { get; }
        public RpcIdentifier(string id) {
            Id = id;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class RpcMethod : Attribute {}

    [AttributeUsage(AttributeTargets.Property)]
    public class RpcProperty : Attribute {
        public bool? CanRead { get; }
        public bool? CanWrite { get; }
        public RpcProperty(bool? canRead = null, bool? canWrite = null) {
            CanRead = canRead;
            CanWrite = canWrite;
        }
    }

    public class RemoteRpcInstance<T> : MonoBehaviour where T : MonoBehaviour, IRpcObject<T> {
        public string prefabKey { get; private set; }
        public NetworkId networkId { get; private set; }
        public object[] initArgs { get; private set; }

        internal void Init(string prefabKey, NetworkId networkId, object[] initArgs) {
            this.prefabKey = prefabKey;
            this.networkId = networkId;
            this.initArgs = initArgs;
        }
    }

    public interface IPrefabLoader {
        public GameObject Load(string prefabKey);
    }

    public class ResourcesPrefabLoader : IPrefabLoader {
        public GameObject Load(string prefabKey) => Resources.Load<GameObject>(prefabKey);
    }

    public static class RpcFeature {
        private static IPrefabLoader _prefabLoader = new ResourcesPrefabLoader();

        public static void SetPrefabLoader(IPrefabLoader prefabLoader) => _prefabLoader = prefabLoader;

        #region Call
        public static bool CallStaticMethod<RetTy>(string userId, RpcCall rpcCall, Action<RetTy> callback, SendOption option)
            => Network.Instance.CallRpcStaticMethod(userId, option, rpcCall, (object obj) => callback((RetTy)obj));

        public static async Task<RetTy> CallRpcStaticMethodAsync<RetTy>(string userId, RpcCall rpcCall, SendOption option) {
            var tcs = new TaskCompletionSource<RetTy>();
            Network.Instance.CallRpcStaticMethod(userId, option, rpcCall, (object obj) => tcs.SetResult((RetTy)obj));
            return await tcs.Task;
        }

        public static IEnumerator CallRpcStaticMethod<RetTy>(string userId, SendOption option, RpcCall rpcCall) {
            RetTy result;
            bool done = false;
            Network.Instance.CallRpcStaticMethod(userId, option, rpcCall, (object obj) => {
                result = (RetTy)obj;
                done = true;
            });
            yield return new WaitUntil(() => done);
        }
        #endregion

        #region Get
        public static bool GetStaticProperty<RetTy>(string userId, string propertyId, Action<RetTy> callback, SendOption option)
            => Network.Instance.GetRpcStaticProperty(userId, propertyId, option, (object obj) => callback((RetTy)obj));

        public static async Task<RetTy> GetStaticPropertyAsync<RetTy>(string userId, string propertyId, SendOption option) {
            var tcs = new TaskCompletionSource<RetTy>();
            Network.Instance.GetRpcStaticProperty(userId, propertyId, option, (object obj) => tcs.SetResult((RetTy)obj));
            return await tcs.Task;
        }

        public static IEnumerator GetStaticProperty<RetTy>(string userId, string propertyId, SendOption option) {
            RetTy result;
            bool done = false;
            Network.Instance.GetRpcStaticProperty(userId, propertyId, option, (object obj) => {
                result = (RetTy)obj;
                done = true;
            });
            yield return new WaitUntil(() => done);
        }
        #endregion

        #region Set
        public static bool SetStaticProperty<ValTy>(string userId, string propertyId, ValTy value, Action callback, SendOption option)
            => Network.Instance.SetRpcStaticProperty(userId, propertyId, value, option, (_) => callback());
        public static async Task SetStaticPropertyAsync<ValTy>(string userId, string propertyId, ValTy value, SendOption option) {
            var tcs = new TaskCompletionSource<object>();
            Network.Instance.SetRpcStaticProperty(userId, propertyId, value, option, (_) => tcs.SetResult(null));
            await tcs.Task;
        }
        public static IEnumerator SetStaticProperty<ValTy>(string userId, string propertyId, ValTy value, SendOption option) {
            bool done = false;
            Network.Instance.SetRpcStaticProperty(userId, propertyId, value, option, (_) => done = true);
            yield return new WaitUntil(() => done);
        }
        #endregion

        // public static GameObject Instantiate(string prefabKey, RpcTarget target, SendOption option) {
        //     var prefab = _prefabLoader.Load(prefabKey);

        //     var rpcObjs = prefab.GetComponents<IBaseRpcObject>();

        //     foreach (var rpc in rpcObjs) {
        //         var type = rpc.GetType();
        //         if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRpcObject<>)) {
        //             type.GetProperty("Rpc").GetValue(rpc)

        //             var instType = typeof(RemoteRpcInstance<>).MakeGenericType(type.GetGenericArguments()[0]);
        //             var inst = prefab.AddComponent(instType);
        //             instType.GetMethod("Init").Invoke(inst, new object[] { prefabKey,  })
        //         }
        //     }

        //     var obj = GameObject.Instantiate(prefab);


        //     // send signal



        //     return;
        // }
    }

    public class RpcFeature<T> : MonoBehaviour where T : MonoBehaviour, IRpcObject<T> {
        internal T _component { get; private set; }
        public NetworkId networkId { get; private set; }

        internal Type _instanceType => typeof(RemoteRpcInstance<T>);

        private void Init(T component) {
            _component = component;

            if (component.gameObject.TryGetComponent<RemoteRpcInstance<T>>(out var inst)) {
                networkId = inst.networkId;
                Network.Instance.RegisterNetworkId(networkId, component);
            }
            else {
                networkId = Network.Instance.AllocateNetworkId(component);
            }
        }

        void OnDestroy() {
            Network.Instance.ReleaseNetworkId(networkId);
        }

        public static RpcFeature<T> Configure(T component) {
            var gameObject = component.gameObject;

            if (!gameObject.TryGetComponent<RpcFeature<T>>(out var feature)) {
                feature = gameObject.AddComponent<RpcFeature<T>>();
                feature.Init(component);
            }

            return feature;
        }

        #region Call
        public IEnumerator CallMethod<RetTy>(RpcTarget target, RpcCall rpcCall, SendOption option) {
            RetTy result;
            bool done = false;
            Network.Instance.CallRpcMethod(networkId, target, option, rpcCall, (object obj) => {
                result = (RetTy)obj;
                done = true;
            }, _component);
            yield return new WaitUntil(() => done);
        }

        public async Task<RetTy> CallMethodAsync<RetTy>(RpcTarget target, RpcCall rpcCall, SendOption option) {
            var tcs = new TaskCompletionSource<RetTy>();
            Network.Instance.CallRpcMethod(networkId, target, option, rpcCall, (object obj) => tcs.SetResult((RetTy)obj), _component);
            return await tcs.Task;
        }

        public bool CallMethod<RetTy>(RpcTarget target, RpcCall rpcCall, Action<RetTy> callback, SendOption option)
            => Network.Instance.CallRpcMethod(networkId, target, option, rpcCall, (object obj) => callback((RetTy)obj), _component);

        #endregion

        #region Get
        public IEnumerator GetProperty<RetTy>(string propertyId, SendOption option) {
            RetTy result;
            bool done = false;
            Network.Instance.GetRpcProperty(networkId, propertyId, option, (object obj) => {
                result = (RetTy)obj;
                done = true;
            }, _component);
            yield return new WaitUntil(() => done);
        }

        public async Task<RetTy> GetPropertyAsync<RetTy>(string propertyId, SendOption option) {
            var tcs = new TaskCompletionSource<RetTy>();
            Network.Instance.GetRpcProperty(networkId, propertyId, option, (object obj) => tcs.SetResult((RetTy)obj), _component);
            return await tcs.Task;
        }

        public bool GetProperty<RetTy>(string propertyId, Action<T> callback, SendOption option)
            => Network.Instance.GetRpcProperty(networkId, propertyId, option, (object obj) => callback((T)obj), _component);
        #endregion

        #region Set
        public IEnumerator SetProperty<ValTy>(RpcTarget target, string propertyId, ValTy value, SendOption option) {
            bool done = false;
            Network.Instance.SetRpcProperty(networkId, target, propertyId, value, option, (_) => done = true, _component);
            yield return new WaitUntil(() => done);
        }

        public async Task SetPropertyAsync<ValTy>(RpcTarget targert, string propertyId, ValTy value, SendOption option) {
            var tcs = new TaskCompletionSource<object>();
            Network.Instance.SetRpcProperty(networkId, targert, propertyId, value, option, (_) => tcs.SetResult(null), _component);
            await tcs.Task;
        }

        public bool SetProperty<ValTy>(RpcTarget target, string propertyId, ValTy value, Action callback, SendOption option)
            => Network.Instance.SetRpcProperty(networkId, target, propertyId, value, option, (_) => callback(), _component);
        #endregion

    }
    
    public interface IBaseRpcObject {}

    public interface IRpcObject<T> : IBaseRpcObject where T : MonoBehaviour, IRpcObject<T> {
        public RpcFeature<T> Rpc { get; protected set; }
    }

    public struct RpcTarget {
        public string[] targets;
        internal bool _all;
        internal bool _owner;
        internal bool _self;

        public static RpcTarget All = new() { _all = true };
        public static RpcTarget Owner = new() { _owner = true };
        public static RpcTarget Self = new() { _self = true };

        public RpcTarget(params string[] targets) {
            this.targets = targets;
            _all = false;
            _owner = false;
            _self = false;
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
                    string id;
                    if (method.IsDefined(typeof(RpcIdentifier))) {
                        id = method.GetCustomAttribute<RpcIdentifier>().Id;
                    } else {
                        id = GetMethodFullName(method);
                    }

                    if (_rpcMethods.ContainsFirst(id)) {
                        throw new InvalidOperationException(
                            $"Packet ID '{id}' is already registered by method '{GetMethodFullName(_rpcMethods[id])}'. "
                        );
                    }

                    _rpcMethods[id] = method;
                }

                foreach (var property in properties) {
                    var attr = property.GetCustomAttribute<RpcProperty>();
                    string id;
                    if (property.IsDefined(typeof(RpcIdentifier))) {
                        id = property.GetCustomAttribute<RpcIdentifier>().Id;
                    }
                    else {
                        id = GetPropertyFullName(property);
                    }
                    
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