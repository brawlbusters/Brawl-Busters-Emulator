using BrawlBusters.Core.Security;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class TutorialHandler : IMessageHandler
{
    private const byte Finished = 0;

    public MsgCategory Category => MsgCategory.cTutorial;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        if (sub != Finished)
        {
            Log.Warn(session.Tag, $"cTutorial sub {sub} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        session.Accounts.Update(session.Account.Id, account => account.TutorialDone = true);
        session.RefreshAccount();
        Log.Info(session.Tag, "Tutorial finished");
        return GameFlow.EnterHomeAsync(session, cancellationToken);
    }
}

public sealed class ModeHandler : IMessageHandler
{
    private const byte RankedChannelType = 5;

    public MsgCategory Category => MsgCategory.cMode;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (ModeRequest)reader.ReadByte();
        switch (request)
        {
            case ModeRequest.EnterHome:
                return GameFlow.ReturnHomeAsync(session, cancellationToken);
            case ModeRequest.EnterInventory:
                return GameFlow.EnterInventoryAsync(session, cancellationToken);
            case ModeRequest.EnterStore:
                return GameFlow.EnterStoreAsync(session, cancellationToken);
            case ModeRequest.EnterRecords:
                return GameFlow.EnterRecordsAsync(session, cancellationToken);
            case ModeRequest.EnterCapsuleMachine:
                return GameFlow.EnterCapsuleMachineAsync(session, cancellationToken);
            case ModeRequest.EnterRanking:
                Log.Info(session.Tag, "Opening the leaderboard");
                return GameFlow.EnterRankingAsync(session, cancellationToken);
            case ModeRequest.EnterLadder:
            {
                // Ranked play has the level range of the client's ranked channels (channel type 5).
                session.RefreshAccount();
                byte needed = GameData.Instance.Channels.Values.Where(channel => channel.Type == RankedChannelType)
                    .Select(channel => channel.LevelMin).DefaultIfEmpty((byte)0).Min();
                if (session.Account.DisplayLevel < needed && !session.Account.Can(Permission.SeeAllChannels))
                {
                    Log.Info(session.Tag, $"Ladder screen refused: level {session.Account.DisplayLevel}, ranked play starts at {needed}");
                    return session.SendAsync(ModePacket.Refused(NetError.Menu_Ladder_LevelLimit), cancellationToken);
                }

                Log.Info(session.Tag, "Opening the ladder (ranked) screen");
                return GameFlow.EnterLadderAsync(session, cancellationToken);
            }
            case ModeRequest.EnterLobby:
                Log.Info(session.Tag, "Entering lobby");
                return GameFlow.EnterLobbyAsync(session, cancellationToken);
            case ModeRequest.EnterSingleLobby:
                Log.Info(session.Tag, "Entering single-play lobby");
                return GameFlow.EnterSingleLobbyAsync(session, cancellationToken);
            default:
                Log.Warn(session.Tag, $"cMode 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }
}

public sealed class SinglePlayHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cSinglePlay;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (SinglePlayRequest)reader.ReadByte();
        switch (request)
        {
            case SinglePlayRequest.Start:
            {
                ushort stage = reader.ReadUInt16();
                Log.Info(session.Tag, $"Single play: starting stage {stage}");
                return GameFlow.StartSingleStageAsync(session, stage, cancellationToken);
            }
            case SinglePlayRequest.Finished:
            {
                bool won = reader.ReadBool();
                Log.Info(session.Tag, $"Single play: stage {session.SingleStage} {(won ? "won" : "lost")}");
                return GameFlow.FinishSingleStageAsync(session, won, cancellationToken);
            }
            case SinglePlayRequest.Retry:
                return GameFlow.RetrySingleStageAsync(session, cancellationToken);
            case SinglePlayRequest.Exit:
                return GameFlow.ExitSingleStageAsync(session, cancellationToken);
            case SinglePlayRequest.Loaded:
                session.SingleStageStartedUtc = DateTime.UtcNow;
                return Task.CompletedTask;
            default:
                Log.Warn(session.Tag, $"cSinglePlay 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }
}

public sealed class RoomHandler : IMessageHandler
{
    private const byte Leave = 0x06;
    private const byte SetLevel = 0x07;
    private const byte ChangeTeam = 0x08;
    private const byte Kick = 0x09;
    private const byte CancelReady = 0x0D;
    private const byte GuestReady = 0x0A;
    private const byte Ready = 0x0C;
    private const byte Start = 0x0E;
    private const byte ReportPing = 0x0B;
    private const byte AdjustGame = 0x0F;
    private const byte FindLadderMatch = 0x12;
    private const byte CancelFindLadderMatch = 0x13;
    private const byte ObserverReady = 0x18;
    private const byte IntrudeGame = 0x14;
    private const byte ObserveGame = 0x15;
    private const byte Observe = 0x16;
    private const byte Play = 0x17;
    private const byte Report = 0x10;
    private const byte SetRule = 0x11;

    public MsgCategory Category => MsgCategory.cRoom;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        if (sub == Leave)
        {
            Log.Info(session.Tag, "Leaving room");
            return GameFlow.LeaveRoomAsync(session, afterMatch: false, cancellationToken);
        }

        Room? room = session.Room;
        if (room is null)
        {
            Log.Warn(session.Tag, $"cRoom 0x{sub:X2} while not in a room: {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        switch (sub)
        {
            case SetLevel:
                reader.ReadByte();
                return GameFlow.RoomLevelRequestedAsync(session, room, reader.ReadUInt16(), cancellationToken);
            case SetRule:
                return GameFlow.RoomRuleRequestedAsync(session, room, reader.ReadUInt16(), cancellationToken);
            case Kick:
                return GameFlow.RoomKickAsync(session, room, reader.ReadUInt32(), cancellationToken);
            case CancelReady:
                return GameFlow.RoomReadyCancelledAsync(session, room, cancellationToken);
            case ChangeTeam:
                return GameFlow.RoomTeamRequestedAsync(session, room, reader.ReadByte(), cancellationToken);
            case GuestReady:
                return GameFlow.RoomGuestReadyAsync(session, room, reader.ReadByte() != 0, cancellationToken);
            case Report:
                reader.ReadToEnd();
                return Task.CompletedTask;
            case Observe:
                return GameFlow.RoomObserveRequestedAsync(session, room, cancellationToken);
            case FindLadderMatch:
                return GameFlow.LadderFindMatchAsync(session, room, cancellationToken);
            case CancelFindLadderMatch:
                return GameFlow.LadderCancelFindAsync(session, room, cancellationToken);
            case ReportPing:
                return GameFlow.RoomPingAsync(session, room, reader.ReadUInt16(), cancellationToken);
            case AdjustGame:
                return GameFlow.RoomAdjustAsync(session, room, cancellationToken);
            case ObserverReady:
                Log.Info(session.Tag, $"Room {room.Id}: observer is {(reader.ReadByte() != 0 ? "ready" : "not ready")}");
                return Task.CompletedTask;
            case IntrudeGame:
                return GameFlow.RoomIntrudeAsync(session, room, asObserver: false, cancellationToken);
            case ObserveGame:
                return GameFlow.RoomIntrudeAsync(session, room, asObserver: true, cancellationToken);
            case Play:
                return GameFlow.RoomPlayRequestedAsync(session, room, cancellationToken);
            case Ready:
                Log.Info(session.Tag, $"Room {room.Id}: ready");
                return GameFlow.RoomReadyAsync(session, room, cancellationToken);
            case Start:
                Log.Info(session.Tag, $"Room {room.Id}: start");
                return GameFlow.RoomStartAsync(session, room, cancellationToken);
            default:
                Log.Warn(session.Tag, $"cRoom 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }
}

public sealed class GameHandler : IMessageHandler
{
    private const byte LeaveMatch = 0x06;
    private const byte LeaveResult = 0x07;
    private const byte LoadingDone = 0x08;
    private const byte Loaded = 0x09;
    private const byte Unknown5 = 0x05;

    public MsgCategory Category => MsgCategory.cGame;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        switch (sub)
        {
            case Loaded when session.Room is { } room:
                Log.Info(session.Tag, $"Room {room.Id}: map loaded");
                return GameFlow.GameLoadedAsync(session, room, cancellationToken);
            case LeaveResult:
            case LeaveMatch:
                Log.Info(session.Tag, sub == LeaveMatch ? "Leaving the match (Exit in the menu)" : "Leaving the match");
                return GameFlow.LeaveRoomAsync(session, afterMatch: true, cancellationToken);
            case LoadingDone:
            case Unknown5:
                Log.Debug(session.Tag, $"cGame 0x{sub:X2} (not answered): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
            default:
                Log.Warn(session.Tag, $"cGame 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }
}

public sealed class HostHandler : IMessageHandler
{
    private const byte MatchEvent = 0x0C;
    private const byte PlayerEnteredEvent = 0;
    private const byte MatchEnded = 0x13;
    private const byte MatchResults = 0x0D;
    private const byte MatchClosed = 0x0A;
    private const int EventOffset = 4;
    private const int PlayerEnteredLength = 9;

    public MsgCategory Category => MsgCategory.cHost;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        byte[] body = reader.ReadToEnd();

        if (sub == MatchEvent && body.Length == PlayerEnteredLength && body[EventOffset] == PlayerEnteredEvent
            && session.Room is { } room)
        {
            uint playerId = BitConverter.ToUInt32(body, EventOffset + 1);
            return GameFlow.GamePlayerEnteredAsync(session, room, playerId, cancellationToken);
        }

        if (sub == MatchEvent && body.Length > EventOffset && session.Room is { } current)
        {
            GameFlow.HostEventReported(session, current, body[EventOffset], body.AsSpan(EventOffset + 1).ToArray());
            return Task.CompletedTask;
        }

        if (sub == MatchEnded)
        {
            Log.Info(session.Tag, "Match ended");
            return GameFlow.GameEndedAsync(session, cancellationToken);
        }

        if (sub == MatchResults)
        {
            Log.Info(session.Tag, $"Match results: {Log.Hex(body)}");
            return session.Room is { } finished && body.Length > EventOffset
                ? GameFlow.MatchResultsAsync(session, finished, body.AsSpan(EventOffset).ToArray(), cancellationToken)
                : Task.CompletedTask;
        }

        if (sub == MatchClosed && session.Room is not null)
            return GameFlow.MatchClosedAsync(session, cancellationToken);

        return Task.CompletedTask;
    }
}

public sealed class LobbyHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cLobby;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (LobbyRequest)reader.ReadByte();
        switch (request)
        {
            case LobbyRequest.EnterChannel:
            {
                ushort channelId = reader.ReadUInt16();
                Log.Info(session.Tag, $"Entering channel {channelId}");
                return GameFlow.EnterChannelAsync(session, channelId, cancellationToken);
            }
            case LobbyRequest.CreateRoom:
            {
                string title = reader.ReadWideString();
                string password = reader.ReadString();
                var mode = (MatchMode)reader.ReadByte();
                byte maxPlayers = reader.ReadByte();
                // Four option bytes close the request; the create dialog only offers the last two (-intrusion,
                // -observation), a recorded request with observers allowed ends 00 00 00 01.
                byte[] options = reader.ReadToEnd();
                bool intrusion = options.Length >= 3 && options[^2] != 0;
                bool observation = options.Length < 1 || options[^1] != 0;
                Log.Info(session.Tag, $"Creating room '{title}': mode {(byte)mode}, max {maxPlayers}{(password.Length > 0 ? ", with password" : "")}"
                    + $"{(intrusion ? ", intrusion" : "")}{(observation ? ", observers" : "")} (options {Log.Hex(options)})");
                return GameFlow.CreateRoomAsync(session, title, password, mode, maxPlayers, cancellationToken, intrusion, observation);
            }
            case LobbyRequest.Refresh:
                return GameFlow.RefreshLobbyAsync(session, reader.ReadUInt16(), cancellationToken);
            case LobbyRequest.GmObserve:
            {
                ushort watched = reader.ReadUInt16();
                return GameFlow.GmObserveAsync(session, watched, cancellationToken);
            }
            case LobbyRequest.RoomInfo:
                return GameFlow.RoomInfoRequestedAsync(session, reader.ReadUInt16(), cancellationToken);
            case LobbyRequest.JoinRoom:
            {
                ushort roomId = reader.ReadUInt16();
                string password = reader.ReadString();
                reader.ReadToEnd();
                return GameFlow.JoinRoomAsync(session, roomId, password, cancellationToken);
            }
            default:
                Log.Warn(session.Tag, $"cLobby {request} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }
}
