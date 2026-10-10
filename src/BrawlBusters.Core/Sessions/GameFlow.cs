using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Sessions;

public static partial class GameFlow
{
    public static async Task StartAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Account account = session.Account;

        if (session.ServerChangeChannel is { } arrivedFor)
        {
            Log.Info(LogChannel.Lobby, session.Tag, $"Server change complete: now on port {session.LocalPort} for channel {arrivedFor}");
            await session.SendAsync(ServerPacket.ServerChangeComplete(), cancellationToken);
            session.ChannelId = arrivedFor;
            await session.SendAsync(ServerPacket.ChannelChanged(arrivedFor), cancellationToken);
            await SendChannelContentsAsync(session, cancellationToken);
            return;
        }

        // The Intro screen is where a character is made. Entering it makes the client build its character-creation
        // sequence (dispatcher 0x5C70B0 raises event 30801; the sequence asks for a nickname as its first step,
        // 0x62CAFE). A player who already has a character must not be sent through it: that sequence was seen to
        // come up later, in the middle of a ranked match, as a "enter your nickname" box. The messages that follow
        // (sUserStart, sUserInfo, sMode) are handled by the client's global handlers, in any screen.
        if (!account.HasNickname || !account.HasCharacter || !session.Settings.SkipIntroScreenForExistingCharacters)
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
        if (session.Account.HasCharacter && session.Account.LevelRewardsUpTo < session.Account.DisplayLevel)
        {
            List<InventoryItem> granted = [];
            session.Accounts.Update(session.Account.Id, account => granted = Rewards.GrantForLevels(account));
            session.RefreshAccount();
            if (granted.Count > 0)
            {
                Log.Info(session.Tag, $"Level rewards up to level {session.Account.DisplayLevel}: {granted.Count} item(s) - {string.Join(", ", granted.Select(item => item.ItemId))}");
                await session.SendAsync(InventoryPacket.Added(granted), cancellationToken);
                await SendCanUnlockAsync(session, cancellationToken);
            }
        }

        await session.SendAsync(ModePacket.Build(GameMode.Home), cancellationToken);
        if (session.Account.HasCharacter) await ItemExpiry.SweepAsync(session, detail: true, cancellationToken);
        await session.SendAsync(UserInfoPacket.HomeEntered(HomeStats.Of(session.Account), MissionBlock(session)), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialOnLobby(AllEquippedTables(session.Account)), cancellationToken);
        await SendChannelsAsync(session, cancellationToken);
    }

    public static async Task EnterSingleLobbyAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.SingleLobby), cancellationToken);
        await session.SendAsync(SingleProgress(session), cancellationToken);
    }

    public static async Task StartSingleStageAsync(ClientSession session, ushort stage, CancellationToken cancellationToken)
    {
        if (!GameData.Instance.SingleStages.ContainsKey(stage))
        {
            Log.Warn(session.Tag, $"Single play: stage {stage} is not in the stage table - refused");
            await session.SendAsync(LobbyPacket.SinglePlayError((byte)NetError.SinglePlay_InvalidID), cancellationToken);
            return;
        }

        session.SingleStage = stage;
        session.SingleStageFinished = false;
        session.SingleStageStartedUtc = DateTime.UtcNow;
        await session.SendAsync(SingleProgress(session), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.SingleGame), cancellationToken);
        await session.SendAsync(LobbyPacket.SinglePlayStart(stage), cancellationToken);
    }

    public static Task RetrySingleStageAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Log.Info(session.Tag, $"Single play: retrying stage {session.SingleStage}");
        session.SingleStageFinished = false;
        session.SingleStageStartedUtc = DateTime.UtcNow;
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
        int clearSeconds = (int)Math.Clamp((DateTime.UtcNow - session.SingleStageStartedUtc).TotalSeconds, 0, 3600);
        session.Accounts.Update(session.Account.Id, account =>
        {
            List<ushort> cleared = account.ClearedStages();
            firstClear = !cleared.Contains(stage);
            if (firstClear) cleared.Add(stage);
            account.Records.SingleStageCleared(stage, clearSeconds);
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
        await session.SendAsync(ModePacket.Build(GameMode.Lobby), cancellationToken);
        await session.SendAsync(LobbyPacket.Opened(), cancellationToken);
    }

    public static async Task ReturnHomeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Home), cancellationToken);
        await session.SendAsync(UserInfoPacket.HomeEntered(HomeStats.Of(session.Account), MissionBlock(session)), cancellationToken);
    }

    public static async Task EnterStoreAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Store), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    public static async Task EnterInventoryAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Inventory), cancellationToken);
        if (await ItemExpiry.SweepAsync(session, detail: false, cancellationToken))
            await session.SendAsync(UserInfoPacket.PartialOnLobby(AllEquippedTables(session.Account)), cancellationToken);
        else
            await session.SendAsync(UserInfoPacket.PartialEmpty(), cancellationToken);
    }

    public static Task EnterRecordsAsync(ClientSession session, CancellationToken cancellationToken)
        => session.SendAsync(ModePacket.Build(GameMode.Records), cancellationToken);

    public static async Task EnterRankingAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.Ranking), cancellationToken);

        await session.SendAsync(Handlers.RankHandler.OwnStandingPacket(session), cancellationToken);
        await session.SendAsync(Handlers.RankHandler.UpdateTimePacket(), cancellationToken);
    }

    public static async Task EnterLadderAsync(ClientSession session, CancellationToken cancellationToken)
    {
        LadderForget(session);
        await session.SendAsync(ModePacket.Build(GameMode.Ladder), cancellationToken);
        await session.SendAsync(Handlers.LadderPacket.Data(session.Account, SyncLadderGrade(session)), cancellationToken);
    }

    public static async Task EnterCapsuleMachineAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(ModePacket.Build(GameMode.CapsuleMachine), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    /// <summary>Why this player may not enter the channel, as the client's own error; null when he may.</summary>
    private static NetError? ChannelRefusal(ClientSession session, ushort channelId)
    {
        if (channelId == session.ChannelId) return null;

        if (ChannelDirectory.Channels.Count > 0
            && (!ChannelDirectory.Exists(channelId) || !ChannelDirectory.MayEnter(session.Account, channelId)))
            return NetError.Lobby_ChangeChannel;

        if (ChannelDirectory.LevelRefusal(session.Account, channelId) is { } wrongLevel) return wrongLevel;

        bool full = BotDirector.StatusOf(channelId, SessionRegistry.InChannel(channelId)) == ChannelStatus.Max;
        return full && !session.Account.Can(Permission.EnterFullChannel) ? NetError.Lobby_ChannelFull : null;
    }

    /// <summary>
    /// The channel a player who is in none yet is put into: among those he may enter and that are not full, the one
    /// with the most players - so people meet instead of each sitting alone in the channel their client asked for.
    /// The asked-for channel wins a tie; 0 when no channel is open to him.
    /// </summary>
    private static ushort SuitableChannel(ClientSession session, ushort asked)
        => ChannelDirectory.Channels
            .Select((channel, order) => (channel.Id, Order: order))
            .Where(channel => ChannelRefusal(session, channel.Id) is null)
            .OrderByDescending(channel => SessionRegistry.InChannel(channel.Id))
            .ThenByDescending(channel => channel.Id == asked)
            .ThenBy(channel => channel.Order)
            .Select(channel => channel.Id)
            .FirstOrDefault();

    public static async Task EnterChannelAsync(ClientSession session, ushort channelId, CancellationToken cancellationToken)
    {
        if (session.ChannelId == 0 && session.Settings.AutoAssignChannel && ChannelDirectory.Channels.Count > 0
            && SuitableChannel(session, channelId) is var suitable and not 0 && suitable != channelId)
        {
            Log.Info(LogChannel.Lobby, session.Tag, $"First channel: {suitable} instead of the requested {channelId} ({SessionRegistry.InChannel(suitable)} player(s) there)");
            channelId = suitable;
        }

        if (ChannelRefusal(session, channelId) is { } refusal)
        {
            Log.Info(LogChannel.Lobby, session.Tag, $"Channel {channelId} refused (level {session.Account.DisplayLevel}): {refusal}");
            await session.SendAsync(LobbyPacket.Error(refusal), cancellationToken);

            // A player who is in no channel yet is put into the first one he may enter, so he is never left without a lobby.
            ushort open = session.ChannelId != 0
                ? (ushort)0
                : ChannelDirectory.Channels.Select(channel => channel.Id).FirstOrDefault(id => ChannelRefusal(session, id) is null);
            if (open == 0) return;
            channelId = open;
        }

        if (ChannelDirectory.PortOf(channelId) is { } port && port != session.LocalPort)
        {
            if (!session.Settings.LobbyPorts.Contains(port))
            {
                Log.Warn(LogChannel.Lobby, session.Tag, $"Channel {channelId} is configured for port {port}, which is not a lobby port - server change failed");
                await session.SendAsync(ServerPacket.ServerChangeFailed(), cancellationToken);
                return;
            }

            uint key = ServerChanges.Begin(session.Account.Id, channelId);
            var server = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(session.Settings.PublicAddress), port);
            Log.Info(LogChannel.Lobby, session.Tag, $"Channel {channelId} lives on port {port}: server change started (from port {session.LocalPort})");
            await session.SendAsync(ServerPacket.ServerChange(channelId, server, key), cancellationToken);
            return;
        }

        session.ChannelId = channelId;
        await session.SendAsync(ServerPacket.ChannelChanged(channelId), cancellationToken);
        await SendChannelContentsAsync(session, cancellationToken);
    }

    public static async Task RefreshLobbyAsync(ClientSession session, ushort channelId, CancellationToken cancellationToken)
    {
        // The client also changes channel with this request (cLobby 04 with another channel id), so the same rules apply.
        if (ChannelRefusal(session, channelId) is { } refusal)
        {
            ushort fallback = session.ChannelId != 0
                ? session.ChannelId
                : ChannelDirectory.Channels.Select(channel => channel.Id).FirstOrDefault(id => ChannelRefusal(session, id) is null);
            Log.Info(LogChannel.Lobby, session.Tag, $"Channel {channelId} refused (level {session.Account.DisplayLevel}): {refusal} - staying in {fallback}");
            await session.SendAsync(LobbyPacket.Error(refusal), cancellationToken);
            if (fallback == 0) return;
            channelId = fallback;
        }

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

    /// <summary>Brings the stored gem rank in line with the current rank boundaries; returns the boundaries.</summary>
    private static ushort[] SyncLadderGrade(ClientSession session)
    {
        ushort[] table = LadderGrades.Table(session);
        byte grade = LadderGrades.GradeOf(session.Account.LadderPoints, table);
        if (grade != session.Account.GemRank)
        {
            session.Accounts.Update(session.Account.Id, account => account.GemRank = grade);
            session.RefreshAccount();
        }
        return table;
    }

    public static byte[] Flags(ClientSession session)
    {
        byte[] flags = new byte[UserInfoPacket.FlagsSize];
        Account account = session.Account;
        flags[UserInfoPacket.GradeFlag] = (byte)account.Grade;
        if (account.Can(Permission.ObserveMatches)) flags[UserInfoPacket.GameMasterFlag] = 1;
        var settings = session.Settings;
        flags[UserInfoPacket.NewTagsFlag] = (byte)((settings.NewTagMyLocker ? 1 : 0) | (settings.NewTagSinglePlay ? 2 : 0) | (settings.NewTagLadder ? 4 : 0));
        if (account.Items.Any(item => item.Type == ItemType.ClassUnlock)) flags[UserInfoPacket.CanUnlockClassFlag] = 1;
        return flags;
    }

    public static Task SendCanUnlockAsync(ClientSession session, CancellationToken cancellationToken)
        => session.SendAsync(UserInfoPacket.FlagChanged(UserInfoPacket.CanUnlockClassFlag, Flags(session)[UserInfoPacket.CanUnlockClassFlag]), cancellationToken);

    private static byte[] MissionBlock(ClientSession session)
    {
        bool assigned = false;
        session.Accounts.Update(session.Account.Id, account => assigned = DailyMissions.Ensure(account));
        if (assigned)
        {
            session.RefreshAccount();
            Log.Info(session.Tag, $"Daily missions: {string.Join(", ", session.Account.Missions.Select(mission => mission.Id))}");
        }
        return DailyMissions.Block(session.Account);
    }

    private static Task SendPlayerRecordAsync(ClientSession session, CancellationToken cancellationToken)
    {
        SyncLadderGrade(session);
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
                LadderRating.FromPoints(account.LadderPoints),
                account.OwnedClassMask(),
                Flags(session),
                MissionBlock(session),
                homeStats: HomeStats.Of(account)),
            cancellationToken);
    }

    public static async Task PushChannelStatesAsync()
    {
        foreach (ClientSession session in SessionRegistry.InLobbies())
        {
            try
            {
                await session.SendAsync(ServerPacket.ChannelStates(ChannelDirectory.Build(session.Settings, session.Account)), CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task SendChannelsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelInfo> channels = ChannelDirectory.Build(session.Settings, session.Account);
        if (session.Settings.SeparateChannelsByCountry)
            await session.SendAsync(GlobalSyncPacket.State(GlobalSyncPacket.SeparateChannelsByCountry), cancellationToken);
        await session.SendAsync(ServerPacket.ChannelList(channels), cancellationToken);
        await session.SendAsync(ServerPacket.ChannelStates(channels), cancellationToken);
    }

    /// <summary>The number the lobby of a channel shows: the players in it, plus the simulated crowd when that is switched on.</summary>
    public static ushort LobbyPlayerCount(ushort channelId)
        => (ushort)(Math.Max(1, SessionRegistry.InChannel(channelId)) + BotDirector.InLobby(channelId));

    private static async Task SendChannelContentsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ushort players = LobbyPlayerCount(session.ChannelId);
        session.LobbyPlayers = players;
        await session.SendAsync(LobbyPacket.PlayerCount(players), cancellationToken);
        IReadOnlyList<Room> rooms = RoomRegistry.Instance.InChannel(session.ChannelId);
        await session.SendAsync(LobbyPacket.RoomList(rooms), cancellationToken);
        LobbyFeed.Remember(session, rooms);
    }
}
