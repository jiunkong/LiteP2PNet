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
        public string Id { get; }

        public string JoinedLobbyId { get; }
        public string HostedLobbyId { get; }

        public void Apply(IUser user);
        public bool Equals(IUser other);
    }

    [Serializable]
    internal class UserDTO<TUserProfile, TAccountState> where TUserProfile : class where TAccountState : class {
        public string id { get; set; }
        public string joinedLobbyId { get; set; }
        public string hostedLobbyId { get; set; }
        public TUserProfile profile { get; set; }
        public TAccountState account { get; set; }
    }

    [Serializable]
    public class User<TUserProfile, TAccountState> : IUser where TUserProfile : class where TAccountState : class {
        public string Id { get; internal set; }

        public string JoinedLobbyId { get; internal set; }
        public string HostedLobbyId { get; internal set; }

        public TUserProfile Profile { get; internal set; }
        public TAccountState Account { get; internal set; }

        public void Apply(IUser user) {
            if (!user.TryCast<TUserProfile, TAccountState>(out var source)) return;

            Id = source.Id;
            JoinedLobbyId = source.JoinedLobbyId;
            HostedLobbyId = source.HostedLobbyId;
            Profile = source.Profile;
            Account = source.Account;
        }

        public static User<TUserProfile, TAccountState> FromJson(string json) => JsonConvert.DeserializeObject<User<TUserProfile, TAccountState>>(json);
        internal static User<TUserProfile, TAccountState> FromDTO(UserDTO<TUserProfile, TAccountState> dto) => new() {
            Id = dto.id,
            JoinedLobbyId = dto.joinedLobbyId,
            HostedLobbyId = dto.hostedLobbyId,
            Profile = dto.profile,
            Account = dto.account
        };

        public bool Equals(IUser other) => Id == other.Id;
    }

    public interface IUserService {
        public void Fetch(IUser user, Action callback = null);
        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state);
    }

    internal interface IUserServiceInternal : IUserService {
        void HandleFetchResponse(DataResponseDTO res);
        IUser Deserialize(string jsonUser);
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
                target = user.Id
            });

            _userFetchCallbacks[user.Id] = (source) => {
                user.Apply(source);
                callback?.Invoke();
            };
        }

        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state) {
            if (state == null) return;

            if (!_network.IsHost) UnityEngine.Debug.LogWarning("ApplyAccountState in UserService can only be called by the host");

            _network.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.UserAccount,
                target = user.Id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        void IUserServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_userFetchCallbacks.TryGetValue(res.target, out Action<IUser> callback)) {
                var source = User<TUserProfile, TAccountState>.FromJson(res.data);
                callback.Invoke(source);
                _userFetchCallbacks.Remove(res.target);
            }
        }

        public IUser Deserialize(string jsonUser) {
            return User<TUserProfile, TAccountState>.FromJson(jsonUser);
        }
    }
}