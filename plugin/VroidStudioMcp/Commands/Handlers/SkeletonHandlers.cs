using System.Collections;

namespace VroidStudioMcp;

/// <summary>Skeleton read-back: bones [all].</summary>
public static class SkeletonHandlers
{
    public static IEnumerator Bones(CommandDispatcher.Context ctx)
    {
        var mode = (CommandUtil.Arg(ctx, 0) ?? "core").Trim().ToLowerInvariant();
        if (mode == "probe") { ctx.Result = Skeleton.Probe(); yield break; }
        if (mode != "core" && mode != "all") { ctx.Error = $"bones: mode must be core|all (got '{mode}')"; yield break; }
        // A slider change rebuilds the model asynchronously: read only after it settles, otherwise
        // the transforms are the previous build's (or absent while the renderers are swapped).
        yield return CommandUtil.WaitUntil(() => !VroidState._modelUpdating, 15f);
        yield return CommandUtil.Pause(0.1f);
        var snap = Skeleton.Snapshot(mode == "all");
        if (snap == null) { ctx.Error = Skeleton.LastError ?? "bones: failed"; yield break; }
        ctx.Result = snap;
    }
}
