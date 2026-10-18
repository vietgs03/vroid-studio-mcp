using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

/// <summary>export_vrm &lt;path&gt; [vrm1|vrm0] [name] [author] [version]</summary>
public static class ExportHandlers
{
    public static IEnumerator ExportVrm(CommandDispatcher.Context ctx)
    {
        var path = CommandUtil.Arg(ctx, 0);
        if (string.IsNullOrWhiteSpace(path)) { ctx.Error = "usage: export_vrm <path.vrm> [vrm1|vrm0] [name] [author] [version]"; yield break; }
        var format = (CommandUtil.Arg(ctx, 1) ?? "vrm1").Trim().ToLowerInvariant();
        if (format != "vrm1" && format != "vrm0") { ctx.Error = $"export_vrm: format must be vrm1|vrm0 (got '{format}')"; yield break; }
        bool vrm1 = format == "vrm1";
        string Opt(int i) { var v = CommandUtil.Arg(ctx, i); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        var name = Opt(2) ?? Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        var author = Opt(3);
        var version = Opt(4);

        var win = WinePaths.ToDos(path);
        if (!win.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)) { ctx.Error = "export_vrm: path must end with .vrm"; yield break; }
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(win)); }
        catch (Exception e) { ctx.Error = $"export_vrm: bad path: {e.Message}"; yield break; }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ctx.Error = $"export_vrm: directory does not exist: {WinePaths.ToHost(dir ?? path)}"; yield break; }
        if (Directory.Exists(win)) { ctx.Error = $"export_vrm: path is a directory: {path}"; yield break; }

        // The model must be fully built: the exporter snapshots the current avatar.
        yield return CommandUtil.WaitUntil(() => !_modelUpdating && _modelReady, 20f);
        if (!_modelReady) { ctx.Error = "export_vrm: model is not ready"; yield break; }

        var sw = Stopwatch.StartNew();
        if (!VrmExport.EnterExportScreen()) { ctx.Error = VrmExport.LastError; yield break; }
        yield return CommandUtil.WaitUntil(() => _vrmExportVm != null, 20f);
        PluginLog.LogWarning($"export: screen active={_vrmExportVm != null} after {sw.ElapsedMilliseconds} ms");
        object pb = null;
        yield return CommandUtil.WaitUntil(() => VrmExport.PreviewReady(pb ??= VrmExport.PhotoBooth()), 90f);
        PluginLog.LogWarning($"export: preview {VrmExport.DescribePreview(pb)} after {sw.ElapsedMilliseconds} ms");
        if (!VrmExport.PreviewReady(pb))
        {
            VrmExport.LeaveExportScreen();
            ctx.Error = "export_vrm: export preview did not become ready (a dialog may be open; check ui_screenshot)";
            yield break;
        }
        Perf.Since("export: preview ready", sw);
        yield return CommandUtil.Pause(0.3f);

        var meta = VrmExport.BuildMeta(pb, vrm1, name, author, version);
        if (meta == null) { VrmExport.LeaveExportScreen(); ctx.Error = VrmExport.LastError; yield break; }
        long before = VrmExport.FileSize(win);
        var tcs = VrmExport.Start(pb, vrm1, meta, win);
        if (tcs == null) { VrmExport.LeaveExportScreen(); ctx.Error = VrmExport.LastError; yield break; }

        bool? ok = null;
        yield return CommandUtil.WaitUntil(() => (ok = VrmExport.Poll(tcs)) != null, 300f);
        Perf.Since("export: done", sw);
        var settings = VrmExport.SettingsSummary(pb);
        long size = VrmExport.FileSize(win);

        VrmExport.LeaveExportScreen();
        // Back on the edit root the editor re-shows the edit model; let it settle so the next
        // command (camera/screenshot/set_param) sees a ready model.
        yield return CommandUtil.Pause(0.5f);
        yield return CommandUtil.WaitUntil(() => !_modelUpdating, 20f);

        if (ok == null) { ctx.Error = $"export_vrm: timed out after {sw.Elapsed.TotalSeconds:F0}s (file size {size})"; yield break; }
        if (ok == false || size <= 0) { ctx.Error = $"export_vrm: exporter reported failure (file size {size})"; yield break; }
        ctx.Result = new Dictionary<string, object>
        {
            ["path"] = WinePaths.ToHost(win),
            ["format"] = format,
            ["bytes"] = size,
            ["replaced_existing"] = before > 0,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["settings"] = settings,
        };
    }
}
