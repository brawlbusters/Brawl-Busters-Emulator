using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static partial class GameFlow
{
    public static async Task StartAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Account account = session.Account;

        await session.SendAsync(ModePacket.Build(GameMode.Intro), cancellationToken);
        await session.SendAsync(KeepAlivePacket.Idle(), cancellationToken);

        if (!account.HasNickname) return;
        await session.SendAsync(UserStartPacket.Build(account.Id, account.LoginId, account.Nickname), cancellationToken);

        if (!account.HasCharacter) return;
        await SendPlayerRecordAsync(session, cancellationToken);

        if (account.TutorialDone) await EnterHomeAsync(session, cancellationToken);
        else await session.SendAsync(ModePacket.Build(GameMode.Tutorial), cancellationToken);
    }

    public static Task NicknameCreatedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Account account = session.Account;
        return session.SendAsync(UserStartPacket.Build(account.Id, account.LoginId, account.Nickname), cancellationToken);
    }

    public static async Task CharacterCreatedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await SendPlayerRecordAsync(session, cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.Tutorial), cancellationToken);
    }

    public static async Task EnterHomeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Home), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialAfterTutorial(), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialOnLobby(AllEquippedTables(session.Account)), cancellationToken);
        await SendChannelsAsync(session, cancellationToken);
    }

    public static async Task EnterSingleLobbyAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.SingleLobby), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
    }

    public static async Task StartSingleStageAsync(ClientSession session, ushort stage, CancellationToken cancellationToken)
    {
        session.SingleStage = stage;
        session.SingleStageFinished = false;
        if (!GameData.Instance.SingleStages.ContainsKey(stage))
            Log.Warn(session.Tag, $"Single play: stage {stage} is not in the stage table (no reward will be paid)");
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.SingleGame), cancellationToken);
        await session.SendAsync(LobbyPacket.SinglePlayStart(stage), cancellationToken);
    }

    public static Task RetrySingleStageAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Log.Info(session.Tag, $"Single play: retrying stage {session.SingleStage}");
        session.SingleStageFinished = false;
        return Task.CompletedTask;
    }

    public static async Task FinishSingleStageAsync(ClientSession session, bool won, CancellationToken cancellationToken)
    {
        if (!won || session.SingleStageFinished) return;
        session.SingleStageFinished = true;

        ushort stage = session.SingleStage;
        GameData data = GameData.Instance;
        data.SingleStages.TryGetValue(stage, out SingleStage? info);

        InventoryItem? rewardItem = null;
        bool firstClear = false;
        session.Accounts.Update(session.Account.Id, account =>
        {
            List<ushort> cleared = account.ClearedStages();
            firstClear = !cleared.Contains(stage);
            if (firstClear) cleared.Add(stage);
            if (info is null) return;

            account.Experience += firstClear ? info.FirstExp : info.RepeatExp;
            account.Gold += firstClear ? info.FirstGold : info.RepeatGold;
            account.Level = data.LevelForExp(account.Experience, account.Level);

            if (firstClear && info.FirstItem != 0 && data.Packages.Fixed.TryGetValue(info.FirstItem, out FixedItem? reward))
            {
                rewardItem = new InventoryItem
                {
                    Slot = account.FreeSlot(),
                    ItemId = reward.ItemId,
                    Type = reward.Type,
                    Options = (ushort[])reward.Options.Clone(),
                    Quantity = reward.Count,
                    State = reward.State,
                    Expiry = reward.Expire > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)reward.Expire : uint.MaxValue,
                };
                account.Items.Add(rewardItem);
            }
        });
        session.RefreshAccount();
        Log.Info(session.Tag, $"Single play: stage {stage} cleared ({(firstClear ? "first time" : "again")})");

        await session.SendAsync(
            UserInfoPacket.ExpAndGold((uint)session.Account.Experience, (uint)session.Account.Gold), cancellationToken);
        if (rewardItem is not null)
            await session.SendAsync(InventoryPacket.Added([rewardItem]), cancellationToken);
    }

    public static async Task ExitSingleStageAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(UserInfoPacket.PartialEmpty(), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.SingleLobby), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
    }


    private static PacketWriter SingleProgress(ClientSession session)
        => LobbyPacket.SinglePlayState(session.Account.ClearedStages());

    public static async Task EnterLobbyAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.Lobby), cancellationToken);
        await session.SendAsync(LobbyPacket.Opened(), cancellationToken);
    }

    public static async Task ReturnHomeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Home), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialAfterTutorial(), cancellationToken);
    }

    public static async Task EnterStoreAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Store), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    public static async Task EnterInventoryAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Inventory), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialEmpty(), cancellationToken);
    }

    public static Task EnterRecordsAsync(ClientSession session, CancellationToken cancellationToken)
        => session.SendAsync(ModePacket.Build(GameMode.Records), cancellationToken);

    public static async Task EnterRankingAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Ranking), cancellationToken);

        await session.SendAsync(Handlers.RankHandler.OwnStandingPacket(session), cancellationToken);
    }

    public static Task EnterLadderAsync(ClientSession session, CancellationToken cancellationToken)
        => session.SendAsync(ModePacket.Build(GameMode.Ladder), cancellationToken);

    public static async Task EnterCapsuleMachineAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.CapsuleMachine), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    public static async Task EnterChannelAsync(ClientSession session, ushort channelId, CancellationToken cancellationToken)
    {
        session.ChannelId = channelId;
        await session.SendAsync(ServerPacket.ChannelChanged(channelId), cancellationToken);
        await SendChannelContentsAsync(session, cancellationToken);
    }

    public static async Task RefreshLobbyAsync(ClientSession session, ushort channelId, CancellationToken cancellationToken)
    {
        session.ChannelId = channelId;
        await SendChannelsAsync(session, cancellationToken);
        await session.SendAsync(ServerPacket.ChannelChanged(channelId), cancellationToken);
        await SendChannelContentsAsync(session, cancellationToken);
    }

    public static byte[] AllEquippedTables(Account account)
    {
        byte[] tables = new byte[Loadout.ClassCount * Loadout.EquippedTableSize];
        for (int i = 0; i < Loadout.ClassCount; i++)
            Loadout.EquippedTable(account, i).CopyTo(tables, i * Loadout.EquippedTableSize);
        return tables;
    }

    private static Task SendPlayerRecordAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Account account = session.Account;
        CharacterShape shape = account.Character ?? new CharacterShape();
        return session.SendAsync(
            UserInfoPacket.Full(
                account.Nickname,
                shape.Class,
                Loadout.ClassSlots(account),
                AllEquippedTables(account),
                HolePunchServer.FindPlayerEndPoint(account.Id),
                HolePunchServer.FindPlayerLocalEndPoint(account.Id),
                account.DisplayLevel,
                account.GemRank,
                (uint)account.Experience,
                (uint)account.Gold,
                (uint)account.Cash,
                (uint)account.Gem),
            cancellationToken);
    }

    private static async Task SendChannelsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelInfo> channels = ChannelDirectory.Build(session.Settings);
        await session.SendAsync(ServerPacket.ChannelList(channels), cancellationToken);
        await session.SendAsync(ServerPacket.ChannelStates(channels), cancellationToken);
    }

    private static async Task SendChannelContentsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ushort players = (ushort)(1 + BotDirector.InLobby(session.ChannelId));
        await session.SendAsync(LobbyPacket.PlayerCount(players), cancellationToken);
        await session.SendAsync(LobbyPacket.RoomList(RoomRegistry.Instance.InChannel(session.ChannelId)), cancellationToken);
    }
}
