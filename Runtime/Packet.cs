using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace LiteP2PNet {
    public interface IPacketSerializer {
        public byte[] Serialize(object obj, Type type);
        public object Deserialize(byte[] bytes, Type type);
    }

    public class JsonPacketSerializer : IPacketSerializer {

        public object Deserialize(byte[] bytes, Type type) {
            string json = Encoding.UTF8.GetString(bytes);
            return JsonConvert.DeserializeObject(json, type);
        }

        public byte[] Serialize(object obj, Type type) {
            string json = JsonConvert.SerializeObject(obj);
            return Encoding.UTF8.GetBytes(json);
        }
    }

    public static class PacketRegistry {
        private static readonly PairMap<string, Type> _packetTypes = new();

        static PacketRegistry() {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes()).Where(t => t.IsDefined(typeof(Packet)));

            foreach (var type in types) {
                var packetAttr = type.GetCustomAttribute<Packet>();
                string id = packetAttr.Id ?? type.FullName;

                if (_packetTypes.ContainsFirst(id)) {
                    throw new InvalidOperationException(
                        $"Packet ID '{id}' is already registered by type '{_packetTypes[id].FullName}'. "
                    );
                }

                _packetTypes[id] = type;
            }
        }

        public static Type GetPacketType(string id)
            => _packetTypes.TryGetByFirst(id, out var type) ? type : null;

        public static string GetPacketId(Type type)
            => _packetTypes.TryGetBySecond(type, out var id) ? id : null;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class Packet : Attribute {
        public string Id { get; }
        public Packet(string id = null) {
            Id = id;
        }
    }
}