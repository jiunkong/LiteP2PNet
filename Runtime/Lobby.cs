using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LiteP2PNet
{
    public static class LobbyExtension {
        public static bool TryCast<TLobbyState>(this ILobby lobby, out Lobby<TLobbyState> typed) where TLobbyState : class {
            if (lobby is Lobby<TLobbyState> t) {
                typed = t;
                return true;
            }
            
            typed = null;
            return false;
        }
    }

    public interface ILobby {
        public string Id { get; }
        public string Name { get; }
        public DateTimeOffset CreatedAt { get; }
        public string HostId { get; }

        public int CurrentPlayers { get; }
        public int MaxPlayers { get; }
        
        public bool IsPlaying { get; }

        public bool IsPrivate { get; }

        public IUser Host { get; }
        public IReadOnlyList<IUser> Members { get; }

        public void Apply(ILobby lobby);
    }

    [Serializable]
    internal class LobbyDTO<TLobbyState, TUserProfile, TAccountState> where TLobbyState : class where TUserProfile : class where TAccountState : class {
        public string id { get; set; }
        public string name { get; set; }
        public DateTimeOffset createdAt { get; set; }
        public string hostId { get; set; }
        public UserDTO<TUserProfile, TAccountState> host { get; set; }

        public UserDTO<TUserProfile, TAccountState>[] members { get; set; }

        public TLobbyState state { get; set; }

        public int currentPlayers { get; set; }
        public int maxPlayers { get; set; }
        public bool isPlaying { get; set; }
        public bool isPrivate { get; set; }
    }

    [Serializable]
    public class Lobby<TLobbyState> : ILobby where TLobbyState : class {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public DateTimeOffset CreatedAt { get; internal set; }
        public string HostId { get; internal set; }
        public IUser Host { get; internal set; }

        public IUser[] Members { get; internal set; }

        public TLobbyState State { get; internal set; }

        public int CurrentPlayers { get; internal set; }
        public int MaxPlayers { get; internal set; }

        public bool IsPlaying { get; internal set; }
        public bool IsPrivate { get; internal set; }

        IReadOnlyList<IUser> ILobby.Members => Members;

        public void Apply(ILobby lobby) {
            if (!lobby.TryCast(out Lobby<TLobbyState> source)) return;

            Id = source.Id;
            Name = source.Name;
            CreatedAt = source.CreatedAt;
            HostId = source.HostId;
            Host = source.Host;
            Members = source.Members;
            State = source.State;
        }

        public static Lobby<TLobbyState> FromJson<TUserProfile, TAccountState>(string json) where TUserProfile : class where TAccountState : class {
            var dto = JsonConvert.DeserializeObject<LobbyDTO<TLobbyState, TUserProfile, TAccountState>>(json);
            return new Lobby<TLobbyState> {
                Id = dto.id,
                Name = dto.name,
                CreatedAt = dto.createdAt,
                HostId = dto.hostId,
                Host = User<TUserProfile, TAccountState>.FromDTO(dto.host),
                Members = dto.members.Select(User<TUserProfile, TAccountState>.FromDTO).ToArray(),
                State = dto.state,
                CurrentPlayers = dto.currentPlayers,
                MaxPlayers = dto.maxPlayers,
                IsPlaying = dto.isPlaying,
                IsPrivate = dto.isPrivate
            };
        }
    }

    public interface ILobbyService {
        public void ApplyMetadata(ILobby lobby, string name = null, bool? isPlaying = null, bool? isPrivate = null, int? maxPlayers = null);
        public void ApplyState<TLobbyStatePatch>(ILobby lobby, TLobbyStatePatch state);
        public void Fetch(ILobby lobby, Action callback = null);
    }

    internal interface ILobbyServiceInternal : ILobbyService {
        void HandleFetchResponse(DataResponseDTO res);
        ILobby Deserialize(string jsonLobby);
    }

    public class LobbyService<TLobbyState, TUserProfile, TAccountState> : ILobbyService, ILobbyServiceInternal where TLobbyState : class where TUserProfile : class where TAccountState : class {
        private Network _network;
        private Dictionary<string, Action<ILobby>> _lobbyFetchCallbacks = new();

        public LobbyService(Network network) {
            _network = network;
        }

        public void ApplyMetadata(ILobby lobby, string name = null, bool? isPlaying = null, bool? isPrivate = null, int? maxPlayers = null) {
            if (name == null && isPlaying == null && isPrivate == null && maxPlayers == null) return;

            if (!_network.IsHost) UnityEngine.Debug.LogWarning("ApplyMetadata in LobbyService can only be called by the host");

            _network.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.LobbyMetadata,
                target = lobby.Id,
                data = JsonConvert.SerializeObject(new LobbyMetadataUpdateDTO {
                    name = name,
                    isPlaying = isPlaying,
                    isPrivate = isPrivate,
                    maxPlayers = maxPlayers
                })
            });
        }

        public void ApplyState<TLobbyStatePatch>(ILobby lobby, TLobbyStatePatch state) {
            if (state == null) return;

            if (!_network.IsHost) UnityEngine.Debug.LogWarning("ApplyState in LobbyService can only be called by the host");
            
            _network.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.LobbyState,
                target = lobby.Id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        public ILobby Deserialize(string jsonLobby) {;
            return Lobby<TLobbyState>.FromJson<TUserProfile, TAccountState>(jsonLobby);
        }

        public void Fetch(ILobby lobby, Action callback = null) {
            _network.SendSignalingMessage(SignalingMsgType.RequestData, "server", new DataRequestDTO {
                type = "lobby",
                target = lobby.Id
            });

            _lobbyFetchCallbacks[lobby.Id] = (source) => {
                lobby.Apply(source);
                callback?.Invoke();
            };
        }

        void ILobbyServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_lobbyFetchCallbacks.TryGetValue(res.target, out Action<ILobby> callback)) {
                var source = Deserialize(res.data);
                callback.Invoke(source);
                _lobbyFetchCallbacks.Remove(res.target);
            }
        }
    }
}