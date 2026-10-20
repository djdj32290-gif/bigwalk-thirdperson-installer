using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BigWalkThirdPerson;

/// <summary>
/// While third person is active, reroute the local player's interact cast so
/// it follows the rendered camera ray (screen centre) instead of the
/// first-person ray from the character. Two seams are patched because we
/// cannot see which one this build feeds the target search through:
/// CastThroughHands gets its ray swapped outright; SearchThroughHands gets
/// its hits array refilled from our own camera cast. In both cases the
/// game's own filtering/validation still decides the final target.
/// </summary>
internal static class CameraCastPatch
{
    private static RaycastHit[] buffer;
    private static int loggedSearchFail;
    private static bool loggedCastCall, loggedCastApply;
    private static bool loggedSearchCall, loggedSearchApply;

    internal static void Apply(Harmony harmony)
    {
        Patch(harmony, nameof(PlayerCaster.CastThroughHands),
            nameof(CastThroughHandsPrefix), null);
        Patch(harmony, nameof(PlayerCaster.SearchThroughHands),
            nameof(SearchThroughHandsPrefix), nameof(SearchThroughHandsFinalizer));
    }

    private static void Patch(Harmony harmony, string name, string prefix, string finalizer)
    {
        try
        {
            var target = AccessTools.Method(typeof(PlayerCaster), name);
            if (target == null)
            {
                Plugin.LogSource.LogWarning($"PlayerCaster.{name} not found; skipping that patch.");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(CameraCastPatch), prefix),
                finalizer: finalizer != null ? new HarmonyMethod(typeof(CameraCastPatch), finalizer) : null);
            Plugin.LogSource.LogInfo($"Patched PlayerCaster.{name} for camera-space casts.");
        }
        catch (Exception e)
        {
            Plugin.LogSource.LogWarning($"Patch PlayerCaster.{name} failed: {e.Message}");
        }
    }

    private static bool TryCameraRay(PlayerCaster caster, out Ray ray, out float reachBonus)
    {
        ray = default;
        reachBonus = 0f;
        var tp = ThirdPersonComponent.Current;
        if (tp == null || !tp.IsActiveForCast) return false;
        if (caster == null || caster.playerCharacter == null) return false;
        try { if (!caster.playerCharacter.isLocalPlayer) return false; }
        catch { return false; }
        ray = new Ray(tp.LastBoomPos, tp.LastBoomRot * Vector3.forward);
        reachBonus = Vector3.Distance(tp.LastBoomPos, tp.LastEyePos);
        return true;
    }

    // The game hands us its eye ray - swap it for the rendered camera ray and
    // stretch the range so reach measured from the character stays the same.
    private static void CastThroughHandsPrefix(PlayerCaster __instance, ref Ray ray, ref float maxDistance)
    {
        try
        {
            if (!loggedCastCall)
            {
                loggedCastCall = true;
                Plugin.LogSource.LogInfo("PlayerCaster.CastThroughHands invoked (seam is live).");
            }
            if (!TryCameraRay(__instance, out Ray camRay, out float bonus)) return;
            // Only reroute casts that actually originate near the eye/head so
            // reach/ground checks from elsewhere keep their real origin.
            if ((ray.origin - ThirdPersonComponent.Current.LastEyePos).sqrMagnitude > 2.25f) return;
            maxDistance += bonus;
            ray = camRay;
            if (!loggedCastApply)
            {
                loggedCastApply = true;
                Plugin.LogSource.LogInfo("Interact cast now routed through the camera.");
            }
        }
        catch (Exception) { }
    }

    // The game hands us hits gathered along its eye ray - refill the array
    // along the camera ray, then let the original filtering run untouched.
    private static void SearchThroughHandsPrefix(PlayerCaster __instance,
        Il2CppStructArray<RaycastHit> hits, ref int hitCount)
    {
        try
        {
            if (!loggedSearchCall)
            {
                loggedSearchCall = true;
                Plugin.LogSource.LogInfo("PlayerCaster.SearchThroughHands invoked (seam is live).");
            }
            if (!TryCameraRay(__instance, out Ray camRay, out float bonus) || hits == null) return;
            if (buffer == null || buffer.Length < hits.Length)
                buffer = new RaycastHit[hits.Length];
            float max = __instance.GetMaxDistanceForDirection(camRay.direction) + bonus;
            int n = CameraCast(camRay, buffer, max, __instance.layerMask.value);
            if (n < 0) return;
            Transform self = __instance.playerCharacter.transform;
            Array.Sort(buffer, 0, n, HitDistanceComparer.Instance);
            int w = 0;
            for (int i = 0; i < n && w < hits.Length; i++)
            {
                var col = buffer[i].collider;
                if (col == null) continue;
                try { if (col.transform.IsChildOf(self)) continue; }
                catch (Exception) { }
                hits[w++] = buffer[i];
            }
            hitCount = w;
            if (!loggedSearchApply)
            {
                loggedSearchApply = true;
                Plugin.LogSource.LogInfo("Interact hit search now fed by the camera cast.");
            }
        }
        catch (Exception e) { LogSearchFail(e); }
    }

    // If the game's own SearchThroughHands throws on our refilled hits, swallow
    // it while third person is active so a bad hit set can't break the player
    // update - worst case is "no target". Exceptions still propagate normally
    // when third person is off.
    private static Exception SearchThroughHandsFinalizer(Exception __exception)
    {
        var tp = ThirdPersonComponent.Current;
        if (__exception != null && tp != null && tp.IsActiveForCast)
        {
            LogSearchFail(__exception);
            return null;
        }
        return __exception;
    }

    // Physics query entry points can be stripped per build; try them in order.
    private static int CameraCast(Ray ray, RaycastHit[] buf, float max, int mask)
    {
        try { return Physics.RaycastNonAlloc(ray, buf, max, mask, QueryTriggerInteraction.Ignore); }
        catch (Exception e) { LogSearchFail(e); }
        try
        {
            var all = Physics.RaycastAll(ray, max, mask, QueryTriggerInteraction.Ignore);
            int n = Mathf.Min(all.Length, buf.Length);
            for (int i = 0; i < n; i++) buf[i] = all[i];
            return n;
        }
        catch (Exception e) { LogSearchFail(e); }
        return -1;
    }

    private sealed class HitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
    {
        internal static readonly HitDistanceComparer Instance = new HitDistanceComparer();
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }

    private static void LogSearchFail(Exception e)
    {
        if (loggedSearchFail >= 3) return;
        loggedSearchFail++;
        Plugin.LogSource.LogWarning($"camera cast query failed (likely stripped): {e.Message}");
    }
}
