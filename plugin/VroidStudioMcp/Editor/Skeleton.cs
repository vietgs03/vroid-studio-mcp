using System;
using System.Collections.Generic;
using static VroidStudioMcp.Interop;

namespace VroidStudioMcp;

/// <summary>
/// Read-only view of the live avatar skeleton.
///
/// The edited avatar is the humanoid <c>Animator</c> on <c>CurrentModelOrigin</c>; its bones are read
/// with <c>Animator.GetBoneTransform(HumanBodyBones)</c>, which is exact regardless of how the skinned
/// meshes bind them (VRoid's renderers mostly bind the <c>J_Adj_*</c> helper bones, not <c>J_Bip_*</c>).
///
/// Used to fit Body sliders against an external reference (e.g. a game rig): set_param → bones →
/// compare. Positions are Unity world metres (Y up). Heights are measured from the mesh bottom
/// (shoe soles), so they compare directly with a reference measured from the ground.
/// </summary>
public static class Skeleton
{
    private static readonly (UnityEngine.HumanBodyBones bone, string name)[] Core =
    {
        (UnityEngine.HumanBodyBones.Hips, "hips"),
        (UnityEngine.HumanBodyBones.Spine, "spine"),
        (UnityEngine.HumanBodyBones.Chest, "chest"),
        (UnityEngine.HumanBodyBones.UpperChest, "upper_chest"),
        (UnityEngine.HumanBodyBones.Neck, "neck"),
        (UnityEngine.HumanBodyBones.Head, "head"),
        (UnityEngine.HumanBodyBones.LeftShoulder, "l_shoulder"),
        (UnityEngine.HumanBodyBones.LeftUpperArm, "l_upper_arm"),
        (UnityEngine.HumanBodyBones.LeftLowerArm, "l_lower_arm"),
        (UnityEngine.HumanBodyBones.LeftHand, "l_hand"),
        (UnityEngine.HumanBodyBones.RightShoulder, "r_shoulder"),
        (UnityEngine.HumanBodyBones.RightUpperArm, "r_upper_arm"),
        (UnityEngine.HumanBodyBones.RightLowerArm, "r_lower_arm"),
        (UnityEngine.HumanBodyBones.RightHand, "r_hand"),
        (UnityEngine.HumanBodyBones.LeftUpperLeg, "l_upper_leg"),
        (UnityEngine.HumanBodyBones.LeftLowerLeg, "l_lower_leg"),
        (UnityEngine.HumanBodyBones.LeftFoot, "l_foot"),
        (UnityEngine.HumanBodyBones.LeftToes, "l_toes"),
        (UnityEngine.HumanBodyBones.RightUpperLeg, "r_upper_leg"),
        (UnityEngine.HumanBodyBones.RightLowerLeg, "r_lower_leg"),
        (UnityEngine.HumanBodyBones.RightFoot, "r_foot"),
        (UnityEngine.HumanBodyBones.RightToes, "r_toes"),
        (UnityEngine.HumanBodyBones.LeftEye, "l_eye"),
        (UnityEngine.HumanBodyBones.RightEye, "r_eye"),
    };

    public static string LastError { get; private set; }

    /// <summary>The edited avatar's humanoid Animator (CurrentModelOrigin), or null while rebuilding.</summary>
    public static UnityEngine.Animator AvatarAnimator()
    {
        UnityEngine.Animator fallback = null;
        var anims = UnityEngine.Object.FindObjectsOfType<UnityEngine.Animator>();
        foreach (var o in Enumerate(anims))
        {
            if (o is not UnityEngine.Animator a || !a.isHuman || a.avatar == null) continue;
            if (!a.gameObject.activeInHierarchy) continue;
            if (a.name == "CurrentModelOrigin") return a;
            fallback ??= a;
        }
        return fallback;
    }

    private static List<object> V(UnityEngine.Vector3 v) => new()
    {
        Math.Round(v.x, 4), Math.Round(v.y, 4), Math.Round(v.z, 4),
    };

    /// <summary>World-space Y and X extent of a skinned mesh in its current pose (BakeMesh).</summary>
    private static bool BakedRange(UnityEngine.SkinnedMeshRenderer r, out float ymin, out float ymax, out float xmin, out float xmax)
    {
        ymin = xmin = float.MaxValue; ymax = xmax = float.MinValue;
        var mesh = new UnityEngine.Mesh();
        try
        {
            r.BakeMesh(mesh, true);
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0) return false;
            var tr = r.transform;
            var pos = tr.position;
            var rot = tr.rotation;
            for (int i = 0; i < verts.Length; i++)
            {
                var w = pos + rot * verts[i];
                if (w.y < ymin) ymin = w.y;
                if (w.y > ymax) ymax = w.y;
                if (w.x < xmin) xmin = w.x;
                if (w.x > xmax) xmax = w.x;
            }
            return true;
        }
        finally { UnityEngine.Object.Destroy(mesh); }
    }

    private static double Dist(UnityEngine.Transform a, UnityEngine.Transform b) =>
        a == null || b == null ? double.NaN : Math.Round(UnityEngine.Vector3.Distance(a.position, b.position), 4);

    /// <summary>Snapshot of the skeleton: bone world positions plus derived measurements that are
    /// independent of where the avatar stands (heights from the floor, segment lengths, widths).
    /// <paramref name="all"/> adds every transform under the avatar named J_Bip_/J_Adj_/J_Sec_
    /// (fingers, face adjust bones, hair and skirt springs).</summary>
    public static Dictionary<string, object> Snapshot(bool all)
    {
        LastError = null;
        UnityEngine.Animator anim;
        try { anim = AvatarAnimator(); }
        catch (Exception e) { LastError = $"bones: {e.Message}"; return null; }
        if (anim == null) { LastError = "bones: humanoid avatar not found (model still building?)"; return null; }

        UnityEngine.Transform B(UnityEngine.HumanBodyBones b)
        {
            try { return anim.GetBoneTransform(b); } catch { return null; }
        }

        float floor = 0f, top = 0f;
        bool hasBounds = CameraApi.TryGetAvatarBounds(out var bounds);
        if (hasBounds) { floor = bounds.min.y; top = bounds.max.y; }

        // Renderer bounds are padded approximations (on the default model the floor read -2 cm and the
        // head top +2.4 cm off). Measure on the skinned vertices instead (BakeMesh): exact for the
        // current pose and sliders, ~0.2 s for the whole avatar.
        double headTop = double.NaN, headWidth = double.NaN, chin = double.NaN;
        try
        {
            float bmin = float.MaxValue, bmax = float.MinValue, skullTop = float.MinValue, faceMin = float.MaxValue;
            float hxMin = float.MaxValue, hxMax = float.MinValue;
            var smrs = anim.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(false);
            foreach (var o in Enumerate(smrs))
            {
                if (o is not UnityEngine.SkinnedMeshRenderer r || !r.enabled) continue;
                if (!BakedRange(r, out var ymin, out var ymax, out var xmin, out var xmax)) continue;
                bmin = Math.Min(bmin, ymin); bmax = Math.Max(bmax, ymax);
                if (r.name == "Head" || r.name == "Face")
                {
                    skullTop = Math.Max(skullTop, ymax);
                    hxMin = Math.Min(hxMin, xmin); hxMax = Math.Max(hxMax, xmax);
                }
                if (r.name == "Face") faceMin = Math.Min(faceMin, ymin);
            }
            if (bmax > bmin)
            {
                floor = bmin; top = bmax; hasBounds = true;
                if (skullTop > float.MinValue) headTop = Math.Round(skullTop - floor, 4);
                if (hxMax > hxMin) headWidth = Math.Round(hxMax - hxMin, 4);
                if (faceMin < float.MaxValue) chin = Math.Round(faceMin - floor, 4);
            }
        }
        catch (Exception e) { PluginLog.LogWarning($"bones: baked measurement failed, using bounds: {e.Message}"); }

        var core = new Dictionary<string, object>();
        foreach (var (bone, name) in Core)
        {
            var t = B(bone);
            if (t != null) core[name] = V(t.position);
        }
        if (!core.ContainsKey("hips")) { LastError = "bones: avatar has no Hips bone yet (model still building?)"; return null; }

        double H(UnityEngine.HumanBodyBones b) { var t = B(b); return t == null ? double.NaN : Math.Round(t.position.y - floor, 4); }

        var heights = new Dictionary<string, object>
        {
            ["hips"] = H(UnityEngine.HumanBodyBones.Hips),
            ["upper_leg"] = H(UnityEngine.HumanBodyBones.LeftUpperLeg),
            ["knee"] = H(UnityEngine.HumanBodyBones.LeftLowerLeg),
            ["ankle"] = H(UnityEngine.HumanBodyBones.LeftFoot),
            ["chest"] = H(UnityEngine.HumanBodyBones.Chest),
            ["neck"] = H(UnityEngine.HumanBodyBones.Neck),
            ["head"] = H(UnityEngine.HumanBodyBones.Head),
            ["head_top"] = headTop,
            ["chin"] = chin,
            ["eye"] = H(UnityEngine.HumanBodyBones.LeftEye),
            ["shoulder"] = H(UnityEngine.HumanBodyBones.LeftUpperArm),
            ["hand"] = H(UnityEngine.HumanBodyBones.LeftHand),
        };

        var lengths = new Dictionary<string, object>
        {
            ["upper_leg"] = Dist(B(UnityEngine.HumanBodyBones.LeftUpperLeg), B(UnityEngine.HumanBodyBones.LeftLowerLeg)),
            ["lower_leg"] = Dist(B(UnityEngine.HumanBodyBones.LeftLowerLeg), B(UnityEngine.HumanBodyBones.LeftFoot)),
            ["upper_arm"] = Dist(B(UnityEngine.HumanBodyBones.LeftUpperArm), B(UnityEngine.HumanBodyBones.LeftLowerArm)),
            ["lower_arm"] = Dist(B(UnityEngine.HumanBodyBones.LeftLowerArm), B(UnityEngine.HumanBodyBones.LeftHand)),
            ["spine"] = Dist(B(UnityEngine.HumanBodyBones.Hips), B(UnityEngine.HumanBodyBones.Neck)),
            ["neck"] = Dist(B(UnityEngine.HumanBodyBones.Neck), B(UnityEngine.HumanBodyBones.Head)),
        };

        var widths = new Dictionary<string, object>
        {
            ["shoulders"] = Dist(B(UnityEngine.HumanBodyBones.LeftUpperArm), B(UnityEngine.HumanBodyBones.RightUpperArm)),
            ["hips"] = Dist(B(UnityEngine.HumanBodyBones.LeftUpperLeg), B(UnityEngine.HumanBodyBones.RightUpperLeg)),
            ["head"] = headWidth,
        };

        var result = new Dictionary<string, object>
        {
            ["units"] = "metres, Unity world (Y up)",
            ["floor_y"] = Math.Round(floor, 4),
            ["mesh_height"] = hasBounds ? Math.Round(top - floor, 4) : double.NaN,
            ["heights"] = heights,
            ["lengths"] = lengths,
            ["widths"] = widths,
            ["bones"] = core,
        };

        if (all)
        {
            var extra = new Dictionary<string, object>();
            var transforms = anim.GetComponentsInChildren<UnityEngine.Transform>(true);
            foreach (var o in Enumerate(transforms))
            {
                if (o is not UnityEngine.Transform t || t.name == null) continue;
                var n = t.name;
                if (!(n.StartsWith("J_Bip_", StringComparison.Ordinal) || n.StartsWith("J_Adj_", StringComparison.Ordinal)
                      || n.StartsWith("J_Sec_", StringComparison.Ordinal))) continue;
                if (!extra.ContainsKey(n)) extra[n] = V(t.position);
            }
            result["bone_count"] = extra.Count;
            result["all"] = extra;
        }
        return result;
    }

    /// <summary>Diagnostics: skinned renderers (bone count, sample bone names, parent path) and every
    /// Animator, to confirm how this app build names and binds the avatar skeleton.</summary>
    public static List<object> Probe()
    {
        var list = new List<object>();
        var arr = UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>();
        foreach (var o in Enumerate(arr))
        {
            if (o is not UnityEngine.SkinnedMeshRenderer r) continue;
            var bones = r.bones;
            var sample = new List<object>();
            int n = bones == null ? 0 : bones.Length;
            for (int i = 0; i < n && sample.Count < 12; i++) if (bones[i] != null) sample.Add(bones[i].name);
            var path = new List<string>();
            for (var t = r.transform; t != null && path.Count < 8; t = t.parent) path.Add(t.name);
            list.Add(new Dictionary<string, object>
            {
                ["renderer"] = r.name,
                ["active"] = r.gameObject.activeInHierarchy,
                ["bones"] = n,
                ["sample"] = sample,
                ["path"] = string.Join(" < ", path),
                ["bounds_h"] = Math.Round(r.bounds.size.y, 3),
            });
        }
        var anims = UnityEngine.Object.FindObjectsOfType<UnityEngine.Animator>();
        foreach (var o in Enumerate(anims))
        {
            if (o is not UnityEngine.Animator a) continue;
            list.Add(new Dictionary<string, object>
            {
                ["animator"] = a.name,
                ["human"] = a.isHuman,
                ["has_avatar"] = a.avatar != null,
            });
        }
        return list;
    }
}
