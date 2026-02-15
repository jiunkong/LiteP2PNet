using System;
using System.Collections.Generic;
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
        public string id { get; }
        public string name { get; }
        public DateTimeOffset createdAt { get; }
        public string hostId { get; }

        public int currentPlayers { get; }
        public int maxPlayers { get; }
        
        public bool isPlaying { get; }

        public bool isPrivate { get; }

        public IUser host { get; }
        public IReadOnlyList<IUser> members { get; }

        public void Apply(ILobby lobby);
    }

    [Serializable]
    public class Lobby<TLobbyState> : ILobby where TLobbyState : class {
        public string id { get; internal set; }
        public string name { get; internal set; }
        public DateTimeOffset createdAt { get; internal set; }
        public string hostId { get; internal set; }
        public IUser host { get; internal set; }

        public IUser[] members { get; internal set; }

        public TLobbyState state { get; internal set; }

        public int currentPlayers { get; internal set; }
        public int maxPlayers { get; internal set; }

        public bool isPlaying { get; internal set; }
        public bool isPrivate { get; internal set; }

        IReadOnlyList<IUser> ILobby.members => members;

        public void Apply(ILobby lobby) {
            if (!lobby.TryCast(out Lobby<TLobbyState> source)) return;

            id = source.id;
            name = source.name;
            createdAt = source.createdAt;
            hostId = source.hostId;
            host = source.host;
            members = source.members;
            state = source.state;
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

    public class LobbyService<TLobbyState> : ILobbyService, ILobbyServiceInternal where TLobbyState : class {
        private Network _network;
        private Dictionary<string, Action<ILobby>> _lobbyFetchCallbacks = new();

        public LobbyService(Network network) {
            _network = network;
        }

        public void ApplyMetadata(ILobby lobby, string name = null, bool? isPlaying = null, bool? isPrivate = null, int? maxPlayers = null) {
            if (name == null && isPlaying == null && isPrivate == null && maxPlayers == null) return;

            if (!_network.IsHost) UnityEngine.Debug.LogWarning("ApplyMetadata in LobbyService can only be called by the host");

            _network.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = "lobby-metadata",
                target = lobby.id,
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
                type = "lobby-state",
                target = lobby.id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        public ILobby Deserialize(string jsonLobby) {
            return JsonConvert.DeserializeObject<Lobby<TLobbyState>>(jsonLobby);
        }

        public void Fetch(ILobby lobby, Action callback = null) {
            _network.SendSignalingMessage(SignalingMsgType.RequestData, "server", new DataRequestDTO {
                type = "lobby",
                target = lobby.id
            });

            _lobbyFetchCallbacks[lobby.id] = (source) => {
                lobby.Apply(source);
                callback?.Invoke();
            };
        }

        void ILobbyServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_lobbyFetchCallbacks.TryGetValue(res.target, out Action<ILobby> callback)) {
                var source = JsonConvert.DeserializeObject<Lobby<TLobbyState>>(res.data);
                callback.Invoke(source);
                _lobbyFetchCallbacks.Remove(res.target);
            }
        }
    }
}