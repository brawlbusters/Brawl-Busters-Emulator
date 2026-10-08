using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions.Handlers;

public static class StandardRouter
{
    public static MessageRouter Create()
    {
        return new MessageRouter()
            .Add(new KeepAliveHandler())
            .Add(new UdpHandler())
            .Add(new IntroHandler())
            .Add(new TutorialHandler())
            .Add(new ModeHandler())
            .Add(new LobbyHandler())
            .Add(new SilentHandler(MsgCategory.cUserInfo))
            .Add(new SilentHandler(MsgCategory.cCommand))
            .Add(new RoomHandler())
            .Add(new GameHandler())
            .Add(new HostHandler())
            .Add(new SinglePlayHandler())
            .Add(new InventoryHandler())
            .Add(new StoreHandler())
            .Add(new RecordHandler())
            .Add(new RankHandler())
            .Add(new DevCommandHandler())
            .Add(new CapsuleMachineHandler())
            .Add(new UserMsgHandler());
    }
}
