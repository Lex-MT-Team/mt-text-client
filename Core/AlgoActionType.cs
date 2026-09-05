namespace MTTextClient.Core;

/// <summary>
/// Client-side algorithm action verb.
///
/// MTCore 0.7.25589 dropped the single <c>AlgorithmData.actionType</c> discriminator
/// and replaced it with a family of <c>AlgorithmRequestData</c> subtypes (one per
/// verb, dispatched by the <c>RequestType</c> string). This enum keeps command call
/// sites verb-shaped; <see cref="CoreConnection.SendAlgorithmRequest"/> and
/// <see cref="CoreConnection.SendAlgorithmGroupRequest"/> translate a verb into the
/// vendor request object.
/// </summary>
public enum AlgoActionType
{
    /// <summary>Run one algorithm by id (AlgorithmRunRequestData).</summary>
    START,

    /// <summary>Stop one algorithm by id (AlgorithmStopRequestData).</summary>
    STOP,

    /// <summary>Run every algorithm (AlgorithmsRunAllRequestData).</summary>
    START_ALL,

    /// <summary>Stop every algorithm (AlgorithmsStopAllRequestData).</summary>
    STOP_ALL,

    /// <summary>Persist an algorithm (AlgorithmAdd/UpdateRequestData, by id).</summary>
    SAVE,

    /// <summary>Persist an algorithm and start it (same, runAlgorithm=true).</summary>
    SAVE_START,

    /// <summary>Remove one algorithm by id (AlgorithmRemoveRequestData).</summary>
    DELETE,

    /// <summary>Toggle per-algorithm profiling (AlgorithmToggleDebagRequestData).</summary>
    TOGGLE_DEBUG,

    /// <summary>Persist a folder/group (AlgorithmFolderAdd/UpdateRequestData).</summary>
    SAVE_GROUP,

    /// <summary>Clone a folder/group (AlgorithmFolderCloneRequestData).</summary>
    CLONE_GROUP,

    /// <summary>Remove a folder/group (AlgorithmFolderRemoveRequestData).</summary>
    DELETE_GROUP,
}
