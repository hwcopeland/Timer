namespace Source2Surf.Timer.Shared.Interfaces.Listeners;

/// <summary>
/// Listener interface for the replay module, allowing external modules to participate in replay upload decisions.
/// </summary>
public interface IReplayModuleListener
{
    /// <summary>
    /// Called after a replay is saved. Determines whether it should be uploaded to remote storage.
    /// Enables uploading non-PB/WR replays (e.g. all finished run replays).
    /// Upload is triggered if any listener returns true.
    /// </summary>
    /// <param name="steamId">Player Steam ID</param>
    /// <param name="style">Style index</param>
    /// <param name="track">Track index</param>
    /// <param name="stage">Stage index (0 for main replay)</param>
    /// <param name="time">Finish time</param>
    /// <param name="isNewBest">Whether this is a new best record (PB or WR)</param>
    bool ShouldUploadReplay(ulong steamId, int style, int track, int stage, float time, bool isNewBest)
    {
        return isNewBest;
    }
}
