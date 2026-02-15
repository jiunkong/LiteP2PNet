using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiteP2PNet
{
    public static class UserExtension {
        public static bool TryCast<TUserProfile, TAccountState>(this IUser user, out User<TUserProfile, TAccountState> typed) where TUserProfile : class where TAccountState : class {
            if (user is User<TUserProfile, TAccountState> t) {
                typed = t;
                return true;
            }

            typed = null;
            return false;
        }
    }

    public interface IUser {
        public string id { get; }

        public string joinedLobbyId { get; }
        public string hostedLobbyId { get; }

        public void Apply(IUser user);
    }

    [Serializable]
    public class User<TUserProfile, TAccountState> : IUser where TUserProfile : class where TAccountState : class {
        public string id { get; internal set; }

        public string joinedLobbyId { get; internal set; }
        public string hostedLobbyId { get; internal set; }

        public TUserProfile profile { get; internal set; }
        public TAccountState account { get; internal set; }

        public void Apply(IUser user) {
            if (!user.TryCast<TUserProfile, TAccountState>(out var source)) return;

            id = source.id;
            joinedLobbyId = source.joinedLobbyId;
            hostedLobbyId = source.hostedLobbyId;
            profile = source.profile;
            account = source.account;
        }
    }

    public interface IUserService {
        public void Fetch(IUser user, Action callback = null);
        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state);
    }

    internal interface IUserServiceInternal : IUserService {
        void HandleFetchResponse(DataResponseDTO res);
    }

    public class UserService<TUserProfile, TAccountState> : IUserService, IUserServiceInternal where TUserProfile : class where TAccountState : class {
        private Network _network;
        
        private Dictionary<string, Action<IUser>> _userFetchCallbacks = new();

        public UserService(Network network) {
            _network = network;
        }

        public void Fetch(IUser user, Action callback = null) {
            _network.SendSignalingMessage(SignalingMsgType.RequestData, "server", new DataRequestDTO {
                type = "user",
                target = user.id
            });

            _userFetchCallbacks[user.id] = (source) => {
                user.Apply(source);
                callback?.Invoke();
            };
        }

        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state) {
            if (state == null) return;

            if (!_network.IsHost) UnityEngine.Debug.LogWarning("ApplyAccountState in UserService can only be called by the host");

            _network.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = "user-account",
                target = user.id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        void IUserServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_userFetchCallbacks.TryGetValue(res.target, out Action<IUser> callback)) {
                var source = JsonConvert.DeserializeObject<User<TUserProfile, TAccountState>>(res.data);
                callback.Invoke(source);
                _userFetchCallbacks.Remove(res.target);
            }
        }
    }
}