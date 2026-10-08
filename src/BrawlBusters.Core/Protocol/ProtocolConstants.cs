namespace BrawlBusters.Core.Protocol;

public static class ProtocolConstants
{
    public const ushort PacketVersion = 105;

    public const string LobbyGreeting = "PlanB_Lobby";

    public const string ChatGreeting = "PlanB_Chat";

    public const int GreetingLength = 12;

    public const byte ClientLoginMarker = 0xCC;

    public const string ClientWriteStream = "TCP default write stream";
    public const string ClientReadStream = "TCP default read stream";

    public const byte SessionSingle = 0x0B;

    public const byte SessionBatch = 0x0C;
}
