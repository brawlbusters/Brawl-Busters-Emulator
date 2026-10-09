using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class UserInfoHandler : IMessageHandler
{
    private const byte ChangeClass = 0x03;

    // cUserInfo 04 `u16 milliseconds`: the client's ping to the server, sent a few times after each change of screen.
    private const byte ReportPing = 0x04;

    public MsgCategory Category => MsgCategory.cUserInfo;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        if (reader.EndOfData) return;

        byte sub = reader.ReadByte();
        if (sub == ReportPing && reader.Remaining >= 2)
        {
            session.ServerPing = reader.ReadUInt16();
            return;
        }

        if (sub != ChangeClass || reader.EndOfData)
        {
            Log.Debug(session.Tag, $"cUserInfo 0x{sub:X2}: {Log.Hex(reader.ReadToEnd())}");
            return;
        }

        byte characterClass = reader.ReadByte();
        Account account = session.Account;
        if (account.Character is null || characterClass is < 1 or > Loadout.ClassCount || !account.OwnsClass(characterClass))
        {
            Log.Info(session.Tag, $"Change to class {characterClass} refused");
            await session.SendAsync(UserInfoPacket.CurrentClass(account.Character?.Class ?? 0), cancellationToken);
            return;
        }

        session.Accounts.Update(account.Id, stored =>
        {
            if (stored.Character is null) return;
            stored.UnlockedClasses = stored.OwnedClassMask();
            stored.Character.Class = characterClass;
        });
        session.RefreshAccount();

        Log.Info(session.Tag, $"Now playing class {characterClass}{(session.InMatch ? " (changed during the match)" : "")}");
        await session.SendAsync(UserInfoPacket.CurrentClass(characterClass), cancellationToken);
        await GameFlow.RoomClassChangedAsync(session, cancellationToken);
    }
}

public sealed class CommandHandler : IMessageHandler
{
    private const byte UnlockClass = 0x01;
    private const byte ReturnToRoom = 0x03;

    public MsgCategory Category => MsgCategory.cCommand;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        if (reader.EndOfData) return;

        byte sub = reader.ReadByte();
        if (sub == ReturnToRoom)
        {
            await GameFlow.ReturnToRoomAsync(session, cancellationToken);
            return;
        }

        if (sub != UnlockClass || reader.EndOfData)
        {
            Log.Debug(session.Tag, $"cCommand 0x{sub:X2}: {Log.Hex(reader.ReadToEnd())}");
            return;
        }

        byte characterClass = reader.ReadByte();
        bool unlocked = false;
        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? ticket = account.Items.FirstOrDefault(item => item.Type == ItemType.ClassUnlock);
            if (ticket is null || characterClass is < 1 or > Loadout.ClassCount || account.OwnsClass(characterClass)) return;

            if (ticket.Quantity > 1) ticket.Quantity--;
            else account.Items.Remove(ticket);
            account.UnlockedClasses = (byte)(account.OwnedClassMask() | (1 << characterClass));
            unlocked = true;
        });
        session.RefreshAccount();

        if (!unlocked)
        {
            Log.Info(session.Tag, $"Unlock of class {characterClass} refused: no Class Unlock item, or the class is already open");
            await session.SendAsync(InventoryPacket.Used(NetError.Inventory_UseItem), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Class {characterClass} unlocked (classes now 0x{session.Account.OwnedClassMask():X2})");
        await session.SendAsync(UserInfoPacket.OwnedClasses(session.Account.OwnedClassMask()), cancellationToken);
        await GameFlow.SendCanUnlockAsync(session, cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }
}

public sealed class ItemHandler : IMessageHandler
{
    private const byte Consume = 0x0A;

    public MsgCategory Category => MsgCategory.cItem;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        if (reader.EndOfData) return Task.CompletedTask;

        byte sub = reader.ReadByte();
        if (sub != Consume || reader.EndOfData)
        {
            Log.Warn(session.Tag, $"cItem 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        byte itemType = reader.ReadByte();
        int left = -1;
        if (itemType == ItemType.SlotChanger)
        {
            session.Accounts.Update(session.Account.Id, account =>
            {
                InventoryItem? item = account.Items.FirstOrDefault(candidate => candidate.Type == itemType && candidate.Quantity > 0);
                if (item is null) return;

                item.Quantity--;
                left = account.Items.Where(candidate => candidate.Type == itemType).Sum(candidate => candidate.Quantity);
                if (item.Quantity == 0) account.Items.Remove(item);
            });
            session.RefreshAccount();
        }

        if (left < 0) Log.Info(session.Tag, $"Item of type {itemType} used in the match, but none is owned");
        else Log.Info(session.Tag, $"Slot Changer used in the match ({left} left)");
        return Task.CompletedTask;
    }
}

