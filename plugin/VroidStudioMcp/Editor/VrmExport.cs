using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static VroidStudioMcp.Interop;
using static VroidStudioMcp.VroidState;

namespace VroidStudioMcp;

/// <summary>
/// VRM export through the editor's own export pipeline, without any dialog.
///
/// The stock flow is: EditRoot <c>EnterToVRMExport()</c> pushes the VRM export screen, the legacy
/// PhotoBooth view model (<c>EditRootScreen.ViewModel.obsoleteLegacyPhotoBoothViewModel</c>) enters
/// export mode and builds the export preview avatar (<c>UmaGameObject</c>); the settings dialog then
/// calls <c>OnVRM1SettingChanged(meta)</c> / <c>OnVRMSettingChanged(meta)</c>, which asks for a file
/// and calls the path overload. We call the path overload directly:
/// <c>OnVRM1SettingChanged(VRM10ObjectMeta meta, string path, TaskCompletionSource&lt;bool&gt; tcs)</c>
/// (VRM0: <c>OnVRMSettingChanged(glTF_VRM_Meta, string, TaskCompletionSource&lt;bool&gt;)</c>) and
/// resolve on the completion source. Export options (material reduction, hair merge, texture
/// atlas) are the ones currently stored in the PhotoBooth <c>ExportSettings</c>.
/// </summary>
public static class VrmExport
{
    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static string LastError { get; private set; }

    public static object PhotoBooth()
    {
        if (_editRootVm == null) return null;
        var pb = Get(_editRootVm, "obsoleteLegacyPhotoBoothViewModel");
        return pb == null ? null : RealObj(pb);
    }

    /// <summary>The export preview avatar exists and no preview rebuild is blocking.</summary>
    public static bool PreviewReady(object pb)
    {
        if (pb == null) return false;
        var uma = Get(pb, "UmaGameObject") ?? Get(pb, "_umaGameObject");
        bool blocking = Get(pb, "_isBlockingUpdateExportPreview") is bool b && b;
        return uma != null && !blocking;
    }

    public static string DescribePreview(object pb)
    {
        if (pb == null) return "photobooth=null";
        var uma = Get(pb, "UmaGameObject") ?? Get(pb, "_umaGameObject");
        return $"type={TypeName(pb)} uma={(uma == null ? "null" : SafeStr(Get(uma, "name")))} " +
               $"blocking={Get(pb, "_isBlockingUpdateExportPreview")} active={Get(pb, "IsActive")} " +
               $"selectedMode={Get(pb, "IsSelectedMode")}";
    }

    public static bool EnterExportScreen()
    {
        if (_editRootVm == null) { LastError = "export: edit root not captured"; return false; }
        _vrmExportVm = null;
        Invoke(_editRootVm, "EnterToVRMExport");
        return true;
    }

    /// <summary>Leave the VRM export screen (back to the editor).</summary>
    public static void LeaveExportScreen()
    {
        var vm = _vrmExportVm;
        if (vm != null) Invoke(vm, "PopScreen");
        _vrmExportVm = null;
    }

    private static object NewIl2Cpp(string typeName)
    {
        var t = FindType(typeName);
        return t == null ? null : Activator.CreateInstance(t);
    }

    private static bool Empty(object s) => s is not string str || string.IsNullOrWhiteSpace(str);

    /// <summary>Meta for the export: the model's stored meta when present (missing required fields
    /// filled in), otherwise a fresh one. VRM 1.0 requires a name and at least one author.</summary>
    public static object BuildMeta(object pb, bool vrm1, string name, string author, string version)
    {
        var settings = Get(pb, "ExportSettings");
        object meta = vrm1 ? Get(settings, "VRM10MetaData") : Get(settings, "VRM0MetaData");
        meta ??= NewIl2Cpp(vrm1 ? "UniVRM10.VRM10ObjectMeta" : "VRM.glTF_VRM_Meta");
        if (meta == null) { LastError = "export: cannot create VRM meta"; return null; }
        meta = RealObj(meta);
        if (vrm1)
        {
            if (!string.IsNullOrEmpty(name) || Empty(Get(meta, "Name"))) Set(meta, "Name", name ?? "VRoid");
            if (!string.IsNullOrEmpty(version) || Empty(Get(meta, "Version"))) Set(meta, "Version", version ?? "1.0");
            var authors = Get(meta, "Authors") as Il2CppSystem.Collections.Generic.List<string>;
            bool noAuthor = authors == null || authors.Count == 0 || string.IsNullOrWhiteSpace(authors[0]);
            if (!string.IsNullOrEmpty(author) || noAuthor)
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                list.Add(author ?? "vroid-studio-mcp");
                Set(meta, "Authors", list);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(name) || Empty(Get(meta, "title"))) Set(meta, "title", name ?? "VRoid");
            if (!string.IsNullOrEmpty(version) || Empty(Get(meta, "version"))) Set(meta, "version", version ?? "1.0");
            if (!string.IsNullOrEmpty(author) || Empty(Get(meta, "author"))) Set(meta, "author", author ?? "vroid-studio-mcp");
        }
        return meta;
    }

    /// <summary>Start the export; returns the completion source to poll (Task.IsCompleted/Result).</summary>
    public static object Start(object pb, bool vrm1, object meta, string winPath)
    {
        LastError = null;
        var method = vrm1 ? "OnVRM1SettingChanged" : "OnVRMSettingChanged";
        var m = pb.GetType().GetMethods(BF).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == 3);
        if (m == null) { LastError = $"export: {method}(meta, path, tcs) not found"; return null; }
        object tcs;
        try { tcs = Activator.CreateInstance(m.GetParameters()[2].ParameterType); }
        catch (Exception e) { LastError = $"export: cannot create completion source: {e.Message}"; return null; }
        try { m.Invoke(pb, new[] { meta, winPath, tcs }); }
        catch (Exception e) { LastError = $"export failed to start: {(e.InnerException ?? e).Message}"; return null; }
        return tcs;
    }

    /// <summary>null while running, otherwise the exporter's success flag.</summary>
    public static bool? Poll(object tcs)
    {
        var task = Get(tcs, "Task");
        if (task == null) return null;
        if (!(Get(task, "IsCompleted") is bool done && done)) return null;
        if (Get(task, "IsFaulted") is bool f && f) return false;
        return Get(task, "Result") is bool ok && ok;
    }

    public static Dictionary<string, object> SettingsSummary(object pb)
    {
        var s = Get(pb, "ExportSettings");
        if (s == null) return null;
        return new Dictionary<string, object>
        {
            ["material_reduction"] = SafeStr(Get(s, "CurrentMaterialReductionMode")),
            ["hair_combination"] = Get(s, "IsEnableHairCombination") is bool h && h,
            ["texture_compression"] = SafeStr(Get(s, "CurrentTextureCompressionMode")),
            ["atlas_resolution"] = SafeStr(Get(s, "CurrentAtlasResolution")),
        };
    }

    public static long FileSize(string winPath)
    {
        try { return File.Exists(winPath) ? new FileInfo(winPath).Length : -1; } catch { return -1; }
    }
}
