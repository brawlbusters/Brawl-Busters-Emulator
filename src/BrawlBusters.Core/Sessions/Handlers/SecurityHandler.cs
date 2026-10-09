using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions.Handlers;

/// <summary>
/// cSecurity, both `u16 length, bytes`: 00 is what the client's Apex anti-cheat module wants to tell its own server
/// (PbSecurityWalker_C_Apex, 0x5D6BE0), 01 a report the client writes itself (0x59CB70). There is no Apex server
/// behind this emulator, so the data is recorded and nothing is answered - the client does not wait for a reply.
/// </summary>
public sealed class SecurityHandler : IMessageHandler
{
    private const byte ModuleData = 0x00;
    private const byte Report = 0x01;

    public MsgCategory Category => MsgCategory.cSecurity;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        byte[] data = reader.EndOfData ? [] : reader.ReadBytes(Math.Min(reader.ReadUInt16(), reader.Remaining));
        switch (sub)
        {
            case ModuleData:
                Log.Debug(LogChannel.Session, session.Tag, $"Anti-cheat module data, {data.Length} byte(s): {Log.Hex(data)}");
                break;
            case Report:
                Log.Warn(LogChannel.Session, session.Tag, $"Security report from the client: {System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0')} ({Log.Hex(data)})");
                break;
            default:
                Log.Warn(session.Tag, $"cSecurity 0x{sub:X2} (not implemented): {Log.Hex(data)}");
                break;
        }
        return Task.CompletedTask;
    }
}
