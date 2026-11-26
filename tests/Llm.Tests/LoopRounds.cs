namespace Llm.Tests;

/// <summary>
/// Waiting for a background loop to look again, instead of a sleep: woken, until it has gone round twice more (a round
/// going on may have read before the change). What a test that says "nothing happens" waits for.
/// </summary>
public static class LoopRounds
{
    public static async Task SeenAsync(Func<int> rounds, Action wake, string what)
    {
        var at = rounds();
        for (var i = 0; rounds() < at + 2; i++)
        {
            Assert.True(i < 3000, $"{what} did not go round again");
            wake();
            await Task.Delay(10);
        }
    }

    /// <summary>Until a loop that wakes by itself (a saved setting wakes it) has done <paramref name="rounds"/>.</summary>
    public static async Task AtAsync(Func<int> count, int rounds, string what)
    {
        for (var i = 0; count() < rounds; i++)
        {
            Assert.True(i < 3000, $"{what} did not go round again");
            await Task.Delay(10);
        }
    }

    public static Task SeenAsync(Llm.Api.Models.EngineWatcher watcher) => SeenAsync(() => watcher.Rounds, watcher.Wake, "the engine watcher");

    public static Task SeenAsync(Llm.Api.Models.MediaControl media) => SeenAsync(() => media.Rounds, media.Wake, "the media loop");
}
