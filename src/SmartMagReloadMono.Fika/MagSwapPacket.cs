using Fika.Core.Networking.LiteNetLib.Utils;

namespace SmartMagReloadMono.Fika
{
    /// <summary>
    /// 我们自己的同步包：告诉其他客户端"这次换弹把哪两只弹匣换位了"。
    /// 只带 id，不带地址——每端在自己的镜像里按 id 找物品再执行同样的交换。
    /// </summary>
    public struct MagSwapPacket : INetSerializable
    {
        public const byte KindSwap = 1;

        public byte Kind;
        public string Version;
        public string ProfileId;
        public string OldMagId;
        public string NewMagId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Kind);
            writer.Put(Version ?? string.Empty);
            writer.Put(ProfileId ?? string.Empty);
            writer.Put(OldMagId ?? string.Empty);
            writer.Put(NewMagId ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = reader.GetByte();
            Version = reader.GetString();
            ProfileId = reader.GetString();
            OldMagId = reader.GetString();
            NewMagId = reader.GetString();
        }
    }
}
