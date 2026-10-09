using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// Serialises every change of shared game state (rooms, channels, matches, bots).
/// <para>
/// Sockets are read and written on many threads at once, but a message is only acted on while its session
/// holds this gate. Sending never blocks (each connection has its own outgoing queue), so the gate is held
/// for the time it takes to compute a reply and nothing else - one session cannot see or disturb another
/// session's half-finished work.
/// </para>
/// </summary>
public static class World
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(Func<Task> work)
    {
        await Gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Waits without holding the gate. Only for code that was entered through <see cref="RunAsync"/>.</summary>
    public static async Task PauseAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Gate.Release();
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        finally
        {
            await Gate.WaitAsync(CancellationToken.None);
        }
    }

    /// <summary>Runs <paramref name="work"/> under the gate after <paramref name="delay"/>, detached from the caller.</summary>
    public static void Later(TimeSpan delay, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await RunAsync(work);
            }
            catch (Exception exception)
            {
                Log.Error("World", exception);
            }
        });
    }
}
