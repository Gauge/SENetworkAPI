using ProtoBuf;

namespace ModNetworkAPI
{
    [ProtoContract]
    internal class Command
    {
        [ProtoMember(1)]
        public ulong SteamId { get; set; }
        [ProtoMember(2)]
        public string CommandString { get; set; }
        [ProtoMember(3)]
        public string Message { get; set; }
        [ProtoMember(4)]
        public byte[] Data { get; set; }
    }
}
