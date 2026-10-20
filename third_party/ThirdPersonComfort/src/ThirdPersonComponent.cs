using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BigWalkThirdPerson;

/// <summary>
/// Drives the third-person camera. Two modes:
///  - Direct (default): the player camera is a child of the CameraPivot rig.
///    We overwrite its world position after the game has placed it, which keeps
///    the whole rig (look rotation, crouch, head bob) working.
///  - Guide: reroutes PlayerCameraMinder.cameraGuide to our own transform.
/// </summary>
public class ThirdPersonComponent : MonoBehaviour
{
    public ThirdPersonComponent(IntPtr ptr) : base(ptr) { }
    public ThirdPersonComponent() : base(ClassInjector.DerivedConstructorPointer<ThirdPersonComponent>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    private PlayerCharacter pc;
    private PlayerCameraReferences pcr;
    private PlayerCameraMinder minder;
    private Camera cam;
    private Transform pivot;     // "CameraPivot" - eye anchor that is never offset by us
    private Transform guide;     // our guide transform (Guide mode)
    private Transform gameGuide; // the game's original guide (Guide mode)
    private CameraDriver driver;

    private bool active;
    private bool suspended;
    private float curDistance;
    private float nextResolve;
    private float nextBodyCheck;
    private bool wantEnabled;
    private bool bodyApplied;
    private bool origHideTorso;
    private Vector3 origCamLocalPos;
    private Quaternion origCamLocalRot;
    private int collideMode = -1; // -1 = not probed, 0 = none works, 1 = sphere, 2 = ray
    private int collisionMask;
    private string lastError;
    private float lastErrorTime;
    private Vector3 smoothPivot;
    private bool hasSmoothPivot;
    private Vector3 smoothAim;
    private bool hasSmoothAim;
    private bool hasBoom;
    // The view rotation the game itself wants, tracked separately from
    // cam.transform.rotation because we overwrite that every frame - reading
    // it back would feed our own convergence offset into the base and the
    // camera would drift and never return.
    private Quaternion gameRot;
    private Quaternion appliedRot;
    private Quaternion prevPivotRot;
    private Quaternion prevCharRot;
    private bool hasGameRot, hasAppliedRot, hasPrevPivot, hasPrevChar;
    private float aimProx;          // 0 = free third person, 1 = leaned to eye
    private float aimProxVel;       // SmoothDamp velocity for aimProx
    private float aimGraceUntil;    // target-loss grace to eat flicker
    private float fpProx;           // wheel-driven first-person blend
    private float fpProxVel;
    private bool wheelFP;           // scrolled in past min zoom = first person
    private bool proxHeadHidden;    // head+torso hidden while leaned in

    // Shared with CameraCastPatch: the transform the camera is actually
    // rendered from, so the interact cast can be rerouted through it.
    internal static ThirdPersonComponent Current;
    internal bool IsActiveForCast => active && !suspended && hasBoom && Plugin.CastFromCamera.Value;
    internal Vector3 LastBoomPos { get; private set; }
    internal Quaternion LastBoomRot { get; private set; }
    internal Vector3 LastEyePos { get; private set; }
    internal Quaternion LastEyeRot { get; private set; }

    private void Awake()
    {
        var g = new GameObject("BigWalkThirdPersonGuide");
        UnityEngine.Object.DontDestroyOnLoad(g);
        g.hideFlags = HideFlags.HideAndDontSave;
        guide = g.transform;
        curDistance = Plugin.Distance.Value;
        wantEnabled = Plugin.StartEnabled.Value;
        Current = this;
    }

    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(Plugin.ToggleKey.Value))
                wantEnabled = !wantEnabled;
            if (Input.GetKeyDown(Plugin.DumpKey.Value))
                Dump();
            if (active && !suspended)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (scroll != 0f)
                {
                    if (scroll > 0f && !wheelFP
                        && curDistance <= Plugin.MinDistance.Value + 0.001f)
                    {
                        // Scrolled in past the minimum: blend to first person.
                        wheelFP = true;
                    }
                    else if (scroll < 0f && wheelFP)
                    {
                        // Scrolled out from first person: back to third.
                        wheelFP = false;
                    }
                    else if (!wheelFP)
                    {
                        curDistance = Mathf.Clamp(curDistance - scroll * Plugin.ZoomSpeed.Value,
                            Plugin.MinDistance.Value, Plugin.MaxDistance.Value);
                    }
                }
            }
        }
        catch (Exception e)
        {
            LogThrottled("input", $"Input handling failed: {e.Message}");
        }

        if (Time.unscaledTime >= nextResolve)
        {
            nextResolve = Time.unscaledTime + 0.5f;
            try { ResolveRefs(); }
            catch (Exception e) { LogThrottled("resolve", $"ResolveRefs: {e.Message}"); }
        }

        try
        {
            if (wantEnabled && !active && RefsOk()) Enter();
            if (!wantEnabled && active) Exit();
        }
        catch (Exception e)
        {
            LogThrottled("enterexit", $"Enter/Exit: {e}");
        }

        if (active && !suspended && Plugin.ShowBody.Value && Time.unscaledTime >= nextBodyCheck)
        {
            nextBodyCheck = Time.unscaledTime + 0.5f;
            try { ApplyBody(); }
            catch (Exception e) { LogThrottled("body", $"ApplyBody: {e.Message}"); }
        }

        // Pivot smoothing runs once per frame here so it stays stable even when
        // ApplyDirect is invoked twice (LateUpdate + OnPreRender).
        if (active && RefsOk() && pivot != null)
        {
            Vector3 raw = pivot.position;
            if (!hasSmoothPivot || (raw - smoothPivot).sqrMagnitude > 2.25f)
            {
                // Teleport / first frame: snap instead of dragging the camera.
                smoothPivot = raw;
                hasSmoothPivot = true;
            }
            else
            {
                float tau = Mathf.Max(0.01f, Plugin.StabilizeSmoothTime.Value);
                smoothPivot = Vector3.Lerp(smoothPivot, raw,
                    1f - Mathf.Exp(-Time.deltaTime / tau));
            }
        }
        else
        {
            hasSmoothPivot = false;
        }

        // Interact lean-in, once per frame. While the game highlights an
        // interactable, aimProx eases toward 1 and the boom collapses toward
        // the eye so the game's own aim point lands at the screen centre;
        // when it clears the blend eases back out. Smoothing the blend also
        // keeps target flicker from snapping the camera.
        Vector3 rawAim = default;
        bool seesTarget = active && RefsOk() && Plugin.AimConverge.Value
            && TryGetAimPoint(out rawAim);
        // Cursor-driven trigger: even before the game acquires the target,
        // if the screen-centre ray finds an interactable inside the
        // character's reach, lean in - near first person the game's own eye
        // ray lands on it and the normal targeting takes over.
        if (!seesTarget && active && RefsOk() && Plugin.AimConverge.Value
            && TryGetCursorInteractable(out Vector3 cursorAim))
        {
            seesTarget = true;
            rawAim = cursorAim;
        }
        if (seesTarget) aimGraceUntil = Time.unscaledTime + 0.15f;
        // A short grace after the target drops keeps single-frame flickers
        // from bouncing the lean in and out.
        bool hasTarget = seesTarget || Time.unscaledTime < aimGraceUntil;
        if (seesTarget)
        {
            if (!hasSmoothAim || (rawAim - smoothAim).sqrMagnitude > 4f)
            {
                smoothAim = rawAim;
                hasSmoothAim = true;
            }
            else
            {
                float tau = Mathf.Max(0.01f, Plugin.AimSmoothTime.Value);
                smoothAim = Vector3.Lerp(smoothAim, rawAim,
                    1f - Mathf.Exp(-Time.deltaTime / tau));
            }
        }
        float proxTime = hasTarget
            ? Plugin.InteractZoomTime.Value
            : Plugin.InteractZoomTime.Value * 1.5f;
        aimProx = Mathf.SmoothDamp(aimProx, hasTarget ? 1f : 0f,
            ref aimProxVel, Mathf.Max(0.05f, proxTime));
        if (!hasTarget && aimProx <= 0.001f)
        {
            aimProx = 0f;
            aimProxVel = 0f;
            hasSmoothAim = false;
        }

        // Wheel-driven first-person blend, independent of the interact lean.
        fpProx = Mathf.SmoothDamp(fpProx, wheelFP ? 1f : 0f, ref fpProxVel,
            Mathf.Max(0.05f, Plugin.InteractZoomTime.Value));
        if (!wheelFP && fpProx <= 0.001f) { fpProx = 0f; fpProxVel = 0f; }

        float proxAll = Mathf.Max(aimProx, fpProx);
        // Leaned in nearly to first person the head and torso would fill the
        // screen; hide both with a small hysteresis.
        if (active && Plugin.ShowBody.Value && pc != null && pc.looks != null)
        {
            bool wantHide = proxHeadHidden ? proxAll > 0.35f : proxAll > 0.5f;
            if (wantHide != proxHeadHidden)
            {
                proxHeadHidden = wantHide;
                SetHeadVisible(!proxHeadHidden && !Plugin.HideHead.Value);
                SetRenderersVisible(pc.looks.torsoRenderers, !proxHeadHidden);
                SetRenderersVisible(pc.looks.legsRenderers, !proxHeadHidden);
                pc.looks.SetHideLocalTorso(proxHeadHidden);
            }
        }
    }

    private void LateUpdate()
    {
        if (!active || !RefsOk()) return;
        try
        {
            if (Plugin.Mode.Value == CameraMode.Guide) LateUpdateGuide();
            else ApplyDirect();
        }
        catch (Exception e)
        {
            LogThrottled("lateupdate", $"LateUpdate: {e.Message}");
        }
    }

    private bool RefsOk() => pc != null && cam != null;

    private void ResolveRefs()
    {
        if (pcr == null)
            pcr = UnityEngine.Object.FindObjectOfType<PlayerCameraReferences>();
        if (cam == null)
            cam = pcr != null && pcr.playerCamera != null ? pcr.playerCamera : Camera.main;

        if (pc == null && PlayerCharacter.allPlayerCharacters != null)
        {
            PlayerCharacter fallback = null;
            for (int i = 0; i < PlayerCharacter.allPlayerCharacters.Count; i++)
            {
                var c = PlayerCharacter.allPlayerCharacters[i];
                if (c == null) continue;
                try { if (c.isLocalPlayer) { pc = c; break; } }
                catch (Exception) { }
                if (fallback == null && c.cameraTransform != null && c.cameraMinder != null)
                    fallback = c;
            }
            if (pc == null) pc = fallback;
        }

        if (pc != null)
        {
            minder = pc.cameraMinder;
            if (cam == null && pc.cameraTransform != null)
                cam = pc.cameraTransform.GetComponent<Camera>();

            // CameraPivot is the camera's parent in the rig:
            // kernal/crouchTranslator/CameraHeightOffset/CameraUprighter/CameraPivot/PlayerCamera
            if (pivot == null && cam != null && cam.transform.parent != null)
                pivot = cam.transform.parent;
            if (pivot == null && pc.registry != null && pc.registry.headBone != null)
                pivot = pc.registry.headBone;
        }
    }

    private void Enter()
    {
        active = true;
        suspended = false;
        hasGameRot = hasAppliedRot = hasPrevPivot = hasPrevChar = false;
        hasSmoothAim = false;
        hasBoom = false;
        aimProx = 0f;
        aimProxVel = 0f;
        fpProx = 0f;
        fpProxVel = 0f;
        wheelFP = false;
        proxHeadHidden = false;
        origCamLocalPos = cam.transform.localPosition;
        origCamLocalRot = cam.transform.localRotation;
        BuildCollisionMask();

        if (Plugin.Mode.Value == CameraMode.Guide)
        {
            if (minder != null)
            {
                gameGuide = minder._cameraGuide;
                minder.cameraGuide = guide;
            }
        }
        else
        {
            EnsureDriver();
        }

        if (Plugin.ShowBody.Value && pc.looks != null)
        {
            origHideTorso = pc.looks.hideLocalTorso;
            ApplyBody();
        }
        Plugin.LogSource.LogInfo($"Third person ON ({Plugin.Mode.Value})");
    }

    private void Exit()
    {
        active = false;
        suspended = false;
        try
        {
            if (Plugin.Mode.Value == CameraMode.Guide && minder != null)
            {
                if (minder._cameraGuide == null || minder._cameraGuide.Pointer == guide.Pointer)
                    minder.cameraGuide = gameGuide;
            }
            else
            {
                if (driver != null) driver.enabled = false;
                if (cam != null) cam.transform.localPosition = origCamLocalPos;
            }
            RestoreBody();
            Plugin.LogSource.LogInfo("Third person OFF");
        }
        catch (Exception e)
        {
            Plugin.LogSource.LogError($"Exit failed: {e}");
        }
    }

    private void EnsureDriver()
    {
        if (driver != null || cam == null) return;
        driver = cam.gameObject.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<CameraDriver>())
            .TryCast<CameraDriver>();
        if (driver != null) driver.owner = this;
    }

    private void BuildCollisionMask()
    {
        // Exclude every layer that any collider under the player uses, plus the
        // built-in Ignore Raycast layer, so the boom can't hit the player's body.
        int exclude = 1 << 2;
        if (pc != null)
        {
            var cols = pc.GetComponentsInChildren<Collider>(true);
            if (cols != null)
                for (int i = 0; i < cols.Count; i++)
                    if (cols[i] != null) exclude |= 1 << cols[i].gameObject.layer;
            exclude |= 1 << pc.gameObject.layer;
        }
        collisionMask = ~exclude;
    }

    /// <summary>Probes once which physics query is unstripped in this build.</summary>
    private int CollideMode()
    {
        if (collideMode != -1) return collideMode;
        Vector3 p = pivot != null ? pivot.position : Vector3.zero;
        var ray = new Ray(p, Vector3.down);
        try
        {
            Physics.SphereCast(ray, 0.1f, out RaycastHit _, 1f, collisionMask, QueryTriggerInteraction.Ignore);
            collideMode = 1;
        }
        catch (Exception) { collideMode = -2; }
        if (collideMode == -2)
        {
            try
            {
                Physics.Raycast(ray, out RaycastHit _, 1f, collisionMask, QueryTriggerInteraction.Ignore);
                collideMode = 2;
            }
            catch (Exception) { collideMode = 0; }
        }
        if (collideMode == 0)
            Plugin.LogSource.LogWarning("No usable physics query found (all stripped); camera collision disabled.");
        else
            Plugin.LogSource.LogInfo($"Collision probe: mode {collideMode} (1=sphere,2=ray)");
        return collideMode;
    }

    /// <summary>Computes the boom-arm position and view rotation.</summary>
    internal bool ComputeBoom(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;
        if (!RefsOk()) return false;

        Vector3 pivotPos = pivot != null ? pivot.position : pc.transform.position + Vector3.up * 1.5f;
        if (Plugin.Stabilize.Value && hasSmoothPivot)
            pivotPos = Vector3.Lerp(pivotPos, smoothPivot,
                Mathf.Clamp01(Plugin.StabilizeAmount.Value));

        // Base view rotation. CameraPivot is the look-driven transform and is
        // never written by us, so it is the clean source: yaw stays glued to
        // the character's facing while pitch is the view. Fall back to
        // tracking cam.transform.rotation when the pivot diverges too far,
        // ignoring our own applied rotation and keeping roll pinned.
        Quaternion rawRot = cam.transform.rotation;
        Quaternion pivotRot = pivot != null ? pivot.rotation : Quaternion.identity;
        Quaternion charRot = pc.transform.rotation;
        Quaternion pivotDelta = hasPrevPivot
            ? pivotRot * Quaternion.Inverse(prevPivotRot) : Quaternion.identity;
        Quaternion charDelta = hasPrevChar
            ? charRot * Quaternion.Inverse(prevCharRot) : Quaternion.identity;
        Quaternion viewDelta = Quaternion.Inverse(charDelta) * pivotDelta;

        if (pivot != null && Quaternion.Angle(pivotRot, rawRot) < 60f)
        {
            gameRot = pivotRot;
            hasGameRot = true;
        }
        else if (!hasGameRot)
        {
            gameRot = rawRot;
            hasGameRot = true;
        }
        else if (hasAppliedRot && Quaternion.Angle(rawRot, appliedRot) < 0.001f)
        {
            // No write this frame: the camera still holds our rotation.
        }
        else if (hasAppliedRot
            && Quaternion.Angle(rawRot, viewDelta * appliedRot) < 0.5f)
        {
            gameRot = viewDelta * gameRot;
        }
        else
        {
            gameRot = rawRot;
        }
        // Roll never belongs in the view base; any that leaked in via delta
        // composition or a converged write is dropped so it cannot build up.
        var ge = gameRot.eulerAngles;
        if (Mathf.Abs(ge.z) > 0.01f) gameRot = Quaternion.Euler(ge.x, ge.y, 0f);
        prevPivotRot = pivotRot;
        prevCharRot = charRot;
        hasPrevPivot = hasPrevChar = true;
        rot = gameRot;
        if (Plugin.LevelHorizon.Value)
        {
            var e = rot.eulerAngles;
            rot = Quaternion.Euler(e.x, e.y, 0f);
        }
        Quaternion eyeRot = rot; // view direction before any convergence
        Vector3 fwd = rot * Vector3.forward;
        Vector3 right = rot * Vector3.right;

        // Lean-in: while an interactable is targeted (or the wheel hits the
        // zoom floor), the height/shoulder offsets collapse toward the eye
        // and the boom shortens, so the game's own aim ray nearly coincides
        // with the camera ray.
        float prox = Mathf.Clamp01(Mathf.Max(aimProx, fpProx));
        Vector3 anchor = pivotPos + Vector3.up * (Plugin.Height.Value * (1f - prox))
            + right * (Plugin.Shoulder.Value * (1f - prox));
        float dist = Mathf.Max(0.05f,
            Mathf.Lerp(curDistance, Plugin.InteractDistance.Value, prox));
        Vector3 desired = anchor - fwd * dist;

        if (Plugin.Collision.Value && CollideMode() != 0)
        {
            Vector3 dir = (desired - anchor).normalized;
            float minDist = 0.25f;
            bool hit;
            RaycastHit h = default;
            if (collideMode == 1)
                hit = Physics.SphereCast(anchor, Plugin.CollisionRadius.Value, dir, out h, dist,
                    collisionMask, QueryTriggerInteraction.Ignore);
            else
                hit = Physics.Raycast(anchor, dir, out h, dist, collisionMask, QueryTriggerInteraction.Ignore);
            if (hit && h.distance < dist)
                desired = anchor + dir * Mathf.Max(h.distance, minDist);
        }
        pos = desired;

        // Residual tilt: mid-transition a bounded convergence keeps the aim
        // near centre. The budget humps to zero at both ends - free third
        // person is untouched and the fully leaned-in pose is the game's own
        // first-person camera, where the aim ray already crosses the centre.
        if (hasSmoothAim && prox > 0.001f)
        {
            Vector3 toAim = smoothAim - desired;
            // World-up keeps the converged rotation roll-free; a nearly
            // vertical aim is degenerate, so skip those frames entirely.
            if (toAim.sqrMagnitude > 0.0025f && Vector3.Dot(toAim, fwd) > 0f
                && Mathf.Abs(Vector3.Dot(toAim.normalized, Vector3.up)) < 0.98f)
            {
                Quaternion targetRot = Quaternion.LookRotation(toAim, Vector3.up);
                rot = Quaternion.RotateTowards(rot, targetRot,
                    Mathf.Min(Plugin.AimMaxAngle.Value,
                        Plugin.AimMaxAngle.Value * prox * (1f - prox) * 4f));
            }
        }

        // Fully leaned in, reproduce the game's first-person camera exactly:
        // the local offset/rotation captured before we took over, applied in
        // pivot space. The eye ray then crosses the screen centre by
        // construction - identical to toggling F4 off.
        Vector3 fpPos = pivot != null
            ? pivot.position + pivotRot * origCamLocalPos
            : desired;
        Quaternion fpRot = pivotRot * origCamLocalRot;
        pos = Vector3.Lerp(pos, fpPos, prox);
        rot = Quaternion.Slerp(rot, fpRot, prox);
        LastBoomPos = pos;
        LastBoomRot = rot;
        LastEyePos = pivotPos;
        LastEyeRot = eyeRot;
        appliedRot = rot;
        hasAppliedRot = true;
        hasBoom = true;
        return true;
    }

    /// <summary>
    /// World-space point of the interactable the game is currently aiming at,
    /// or false when there is none. Only real targets count - the free-floating
    /// crosshair ring lands on arbitrary surfaces (e.g. the floor), and
    /// converging on those makes the camera dig into the ground.
    /// </summary>
    private bool TryGetAimPoint(out Vector3 aim)
    {
        aim = default;
        if (pc == null) return false;
        try
        {
            var caster = pc.caster;
            if (caster != null)
            {
                if (caster.castableTarget != null)
                {
                    var t = caster.castableTarget.GetCrosshairTransform();
                    aim = t != null ? t.position : caster.hitPosition;
                    return true;
                }
                if (caster.castProp != null)
                {
                    var cp = caster.castProp.customCrosshairPoint;
                    aim = cp != null ? cp.position : caster.hitPosition;
                    return true;
                }
                if (caster.castCharacter != null)
                {
                    aim = caster.hitPosition;
                    return true;
                }
            }
            var decisions = pc.decisions;
            if (decisions != null && decisions.heldDownSwitch != null)
            {
                var t = decisions.heldDownSwitch.GetCrosshairTransform();
                aim = t != null ? t.position : decisions.heldDownSwitch.transform.position;
                return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>
    /// Screen-centre query for a not-yet-highlighted interactable inside the
    /// character's reach. Used to start the lean-in from the reticle itself.
    /// The ray is evaluated from the *un-leaned* boom pose so the trigger
    /// cannot flap as the camera zooms: a low item visible from the boom but
    /// not the eye would otherwise bounce the lean in and out.
    /// </summary>
    private bool TryGetCursorInteractable(out Vector3 aim)
    {
        aim = default;
        if (!hasBoom || cam == null || CollideMode() == 0) return false;
        try
        {
            Vector3 eyeFwd = LastEyeRot * Vector3.forward;
            Vector3 eyeRight = LastEyeRot * Vector3.right;
            Vector3 refAnchor = LastEyePos + Vector3.up * Plugin.Height.Value
                + eyeRight * Plugin.Shoulder.Value;
            var ray = new Ray(refAnchor - eyeFwd * curDistance, eyeFwd);
            float maxDist = curDistance + 4f;
            int mask = pc.caster != null ? pc.caster.layerMask.value : collisionMask;
            RaycastHit h;
            bool hit = collideMode == 1
                ? Physics.SphereCast(ray, 0.05f, out h, maxDist, mask, QueryTriggerInteraction.Ignore)
                : Physics.Raycast(ray, out h, maxDist, mask, QueryTriggerInteraction.Ignore);
            if (!hit || h.collider == null) return false;
            try { if (h.collider.transform.IsChildOf(pc.transform)) return false; }
            catch (Exception) { }
            if (!IsInteractable(h.collider)) return false;
            float reach = 2f;
            try { if (pc.caster != null) reach = pc.caster.raycastMaxDistance; }
            catch (Exception) { }
            if (Vector3.Distance(h.point, LastEyePos) > reach * 1.15f) return false;
            aim = h.point;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool IsInteractable(Collider col)
    {
        try
        {
            if (col.GetComponentInParent(Il2CppInterop.Runtime.Il2CppType.Of<Prop>()) != null) return true;
            if (col.GetComponentInParent(Il2CppInterop.Runtime.Il2CppType.Of<CastableTarget>()) != null) return true;
            if (col.GetComponentInParent(Il2CppInterop.Runtime.Il2CppType.Of<PeckSwitch>()) != null) return true;
        }
        catch (Exception) { }
        return false;
    }

    private void LateUpdateGuide()
    {
        if (minder == null) return;
        var cur = minder._cameraGuide;
        if (cur == null || cur.Pointer == guide.Pointer)
        {
            suspended = false;
            if (cur == null || cur.Pointer != guide.Pointer)
                minder.cameraGuide = guide;
        }
        else if (gameGuide == null || cur.Pointer != gameGuide.Pointer)
        {
            // Something else (cutscene, dream, ...) holds the camera. Leave it alone.
            suspended = true;
            gameGuide = cur;
            return;
        }
        else
        {
            // Camera control returned to the default guide -> reassert ours.
            suspended = false;
            minder.cameraGuide = guide;
        }
        suspended = false;

        if (ComputeBoom(out Vector3 pos, out Quaternion rot))
            guide.SetPositionAndRotation(pos, rot);
    }

    internal void ApplyDirect()
    {
        if (ComputeBoom(out Vector3 pos, out Quaternion rot))
            cam.transform.SetPositionAndRotation(pos, rot);
    }

    private void ApplyBody()
    {
        if (pc == null || pc.looks == null) return;
        if (!bodyApplied || pc.looks.currentLookType != PlayerLooks.LookType.Remote)
        {
            if (bodyApplied)
                LogThrottled("bodymode", $"Game reset look mode to {pc.looks.currentLookType}; re-applying remote body.");
            pc.looks.SetBodyToRemoteMode();
        }
        pc.looks.SetHideLocalTorso(proxHeadHidden);
        SetHeadVisible(!Plugin.HideHead.Value && !proxHeadHidden);
        SetRenderersVisible(pc.looks.torsoRenderers, !proxHeadHidden);
        SetRenderersVisible(pc.looks.legsRenderers, !proxHeadHidden);
        bodyApplied = true;
    }

    private void RestoreBody()
    {
        if (pc == null || pc.looks == null || !bodyApplied) return;
        try
        {
            SetHeadVisible(true);
            SetRenderersVisible(pc.looks.torsoRenderers, true);
            SetRenderersVisible(pc.looks.legsRenderers, true);
            pc.looks.SetBodyToLocalMode();
            if (pc.looks.hideLocalTorso != origHideTorso) pc.looks.SetHideLocalTorso(origHideTorso);
            bodyApplied = false;
        }
        catch (Exception e)
        {
            Plugin.LogSource.LogWarning($"RestoreBody: {e.Message}");
        }
    }

    private void SetHeadVisible(bool visible)
    {
        if (pc.looks == null) return;
        SetRenderersVisible(pc.looks.headRenderers, visible);
    }

    private static void SetRenderersVisible(Il2CppReferenceArray<PlayerLooks.LooksRenderer> list, bool visible)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].renderer != null)
                list[i].renderer.enabled = visible;
    }

    private void OnGUI()
    {
        try
        {
            if (!active || !Plugin.ShowStatus.Value) return;
            var r = new Rect(12, Screen.height - 34, 340, 22);
            string s = $"3rd person [{Plugin.ToggleKey.Value}]  zoom:{curDistance:0.0}m{(suspended ? " (cutscene)" : "")}";
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s);
            GUI.color = Color.white;
            GUI.Label(r, s);
            if (Plugin.ShowAimDot.Value && hasBoom) DrawAimDot();
        }
        catch (Exception e)
        {
            LogThrottled("gui", $"OnGUI: {e.Message}");
        }
    }

    /// <summary>
    /// A small dot drawn on the game's highlighted interact target, doubling
    /// as a convergence diagnostic. Not drawn without a target - an eye-ray
    /// fallback lands on the character's head on screen and reads as noise.
    /// </summary>
    private void DrawAimDot()
    {
        if (!TryGetAimPoint(out Vector3 aim)) return;
        Vector3 sp = cam.WorldToScreenPoint(aim);
        if (sp.z <= 0f) return;
        const float size = 12f;
        GUI.color = new Color(1f, 1f, 1f, 0.95f);
        GUI.DrawTexture(new Rect(sp.x - size / 2f, Screen.height - sp.y - size / 2f, size, size), DotTex());
        GUI.color = Color.white;
    }

    private static Texture2D dotTex;
    private static Texture2D DotTex()
    {
        if (dotTex != null) return dotTex;
        var t = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        var px = new Color[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                float dx = x - 7.5f, dy = y - 7.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                px[y * 16 + x] = (d >= 3.5f && d <= 6.5f) ? Color.white
                    : (d < 3.5f ? new Color(1f, 1f, 1f, 0.25f)
                    : (d <= 7.5f ? new Color(1f, 1f, 1f, 0.35f) : new Color(1f, 1f, 1f, 0f)));
            }
        t.SetPixels(px);
        t.Apply();
        dotTex = t;
        return t;
    }

    private void LogThrottled(string key, string msg)
    {
        if (lastError == key && Time.unscaledTime - lastErrorTime < 5f) return;
        lastError = key;
        lastErrorTime = Time.unscaledTime;
        Plugin.LogSource.LogError(msg);
    }

    private void Dump()
    {
        if (pc == null) { Plugin.LogSource.LogInfo("Dump: no local player yet"); return; }
        Plugin.LogSource.LogInfo("=== ThirdPerson dump ===");
        Plugin.LogSource.LogInfo($"cam={(cam != null ? cam.name : "null")} pivot={(pivot != null ? pivot.name : "null")} " +
            $"camLocal={cam?.transform.localPosition} collideMode={collideMode} mask={collisionMask}");
        try
        {
            var c = pc.caster;
            if (c != null)
                Plugin.LogSource.LogInfo($"caster: prop={(c.castProp != null ? c.castProp.name : "-")} " +
                    $"castable={(c.castableTarget != null ? c.castableTarget.name : "-")} " +
                    $"character={(c.castCharacter != null ? c.castCharacter.name : "-")} " +
                    $"hit={c.hitPosition} dist={c.castDistance} maxDist={c.raycastMaxDistance}");
            var hl = pc.highlighter;
            if (hl != null)
                Plugin.LogSource.LogInfo($"highlighter: target={(hl.target != null ? hl.target.name : "-")} " +
                    $"ringActive={(hl.inWorldCrosshair != null && hl.inWorldCrosshair.gameObject.activeInHierarchy)}");
            var looks = pc.looks;
            if (looks != null)
                Plugin.LogSource.LogInfo($"looks: type={looks.currentLookType} hideTorso={looks.hideLocalTorso}");
            Plugin.LogSource.LogInfo($"boom={LastBoomPos} eye={LastEyePos} castFromCam={IsActiveForCast} prox={aimProx:0.00}");
            Plugin.LogSource.LogInfo($"rot: cam={cam.transform.eulerAngles} game={gameRot.eulerAngles} " +
                $"pivot={(pivot != null ? pivot.eulerAngles : Vector3.zero)} char={pc.transform.eulerAngles} " +
                $"guide={(minder != null && minder._cameraGuide != null ? minder._cameraGuide.eulerAngles : Vector3.zero)}");
        }
        catch (Exception e) { Plugin.LogSource.LogWarning($"dump caster: {e.Message}"); }
        DumpTransform(pc.transform, 0);
    }

    private void DumpTransform(Transform t, int depth)
    {
        var r = t.GetComponent<Renderer>();
        string extra = r != null ? $" renderer.enabled={r.enabled} layer={t.gameObject.layer}" : $" layer={t.gameObject.layer}";
        Plugin.LogSource.LogInfo($"{new string(' ', depth * 2)}{t.name}{extra}");
        for (int i = 0; i < t.childCount; i++)
            DumpTransform(t.GetChild(i), depth + 1);
    }

    private void OnDestroy()
    {
        if (active) Exit();
        if (Current == this) Current = null;
    }
}

/// <summary>
/// Attached to the player camera in Direct mode; OnPreRender runs after every
/// script update, right before the camera renders - the last word on position.
/// </summary>
public class CameraDriver : MonoBehaviour
{
    public CameraDriver(IntPtr ptr) : base(ptr) { }
    public CameraDriver() : base(ClassInjector.DerivedConstructorPointer<CameraDriver>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    internal ThirdPersonComponent owner;

    private void OnPreRender()
    {
        try { owner?.ApplyDirect(); }
        catch (Exception) { }
    }
}
