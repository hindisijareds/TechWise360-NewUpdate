using UnityEngine;
using UnityEngine.XR.Content.Interaction;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// A raised RAM module reserves its slot but does not own the XR socket until pressed and released.
[DefaultExecutionOrder(10000)]
internal sealed class TechWiseRamInsertion : MonoBehaviour
{
    internal const float Travel = .018f;
    internal XRLockSocketInteractor Socket { get; private set; }
    internal bool Staged => Socket != null;
    internal bool AllowSocketSelection { get; private set; }
    XRGrabInteractable grab;
    IXRSelectInteractor hand;
    Vector3 pressOrigin;
    float depth;
    bool trackingSaved, trackPosition, trackRotation;
    bool waitingToSeat;
    internal bool ReadyToSeat => hand != null && depth >= Travel - .003f;
    Vector3 Normal => TechWiseSimulationRuntime.Instance.FindPart("Motherboard").transform.forward.normalized;
    internal string Guidance => !Staged
        ? "Align the keyed notch and release to rest the RAM above the slot. Then grip it again and push down."
        : ReadyToSeat ? "RAM fully pressed down. Release the grip to seat it."
        : "RAM aligned but not seated. Hold grip on the module, push straight DOWN toward the motherboard, then release.";

    void Awake() => grab = GetComponent<XRGrabInteractable>();
    internal void Stage(XRLockSocketInteractor socket)
    {
        ClearStage(); Socket = socket;
        SetPose(Travel);
    }
    internal void BeginPress(IXRSelectInteractor interactor)
    {
        if (!Staged) return;
        hand = interactor;
        pressOrigin = interactor.GetAttachTransform(grab).position;
        depth = 0;
        trackPosition = grab.trackPosition; trackRotation = grab.trackRotation; trackingSaved = true;
        // Keep the keyed module constrained to its slot while the existing controller moves down.
        grab.trackPosition = false; grab.trackRotation = false;
    }
    void RestoreTracking()
    {
        if (trackingSaved && grab != null) { grab.trackPosition = trackPosition; grab.trackRotation = trackRotation; }
        trackingSaved = false; hand = null;
    }
    internal void ClearStage()
    {
        RestoreTracking(); Socket = null; depth = 0; AllowSocketSelection = false; waitingToSeat = false;
    }
    void ObservePress()
    {
        if (hand == null || !Staged) return;
        Vector3 delta = hand.GetAttachTransform(grab).position - pressOrigin;
        float down = -Vector3.Dot(delta, Normal);
        float sideways = Vector3.ProjectOnPlane(delta, Normal).magnitude;
        // Pulling clear lets the user change slots or return the module to the table.
        if (down < -.06f || sideways > .06f)
        {
            SetPose(Travel); ClearStage(); transform.position += delta;
            var body = grab.GetComponent<Rigidbody>();
            if (body != null) body.position = transform.position;
            return;
        }
        depth = sideways <= .025f ? Mathf.Clamp(down, 0, Travel) : 0;
        SetPose(Travel - depth);
    }
    internal bool Release(SelectExitEventArgs args)
    {
        if (!Staged || args.interactorObject is XRSocketInteractor) return false;
        var state = TechWiseSimulationRuntime.Instance;
        bool canceled = args.isCanceled || TechWisePauseSession.Active || state.ManipulationLocked || TechWiseAssemblyHistory.Restoring;
        if (!canceled) ObservePress();
        if (!Staged) return false;
        bool complete = !canceled && ReadyToSeat;
        RestoreTracking();
        if (complete)
        {
            // XRI finishes releasing and restoring Rigidbody state after the exit callback.
            waitingToSeat = true; depth = Travel; SetPose(0); return true;
        }
        depth = 0; SetPose(Travel);
        if (!canceled) state.RamFeedback(Guidance);
        return true;
    }
    void SetPose(float height)
    {
        if (!Staged) return;
        var target = Socket.GetAttachTransform(grab);
        var from = grab.GetAttachTransform(Socket);
        transform.rotation = target.rotation * Quaternion.Inverse(from.rotation) * transform.rotation;
        transform.position += target.position + Normal * height - from.position;
        var body = grab.GetComponent<Rigidbody>();
        if (body != null)
        {
            if (!body.isKinematic) body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.isKinematic = true; body.position = transform.position; body.rotation = transform.rotation;
        }
        foreach (var collider in grab.colliders) if (collider != null) collider.isTrigger = true;
    }
    void LateUpdate()
    {
        if (!Staged || TechWisePauseSession.Active || TechWiseSimulationRuntime.Instance.ManipulationLocked) return;
        if (waitingToSeat && !TechWiseSimulationRuntime.IsHeld(grab))
        {
            waitingToSeat = false; SetPose(0); AllowSocketSelection = true;
            var state = TechWiseSimulationRuntime.Instance;
            try { state.CompleteRamInsertion(grab, Socket); }
            finally { AllowSocketSelection = false; }
            if (state.IsPartInstalled(grab)) { ClearStage(); return; }
            depth = 0;
        }
        if (hand != null) ObservePress();
        else SetPose(Travel);
    }
    void OnDisable() => ClearStage();
}
