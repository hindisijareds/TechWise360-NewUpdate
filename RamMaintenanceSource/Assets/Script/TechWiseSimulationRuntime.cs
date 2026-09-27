using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Events;
using UnityEngine.XR.Content.Interaction;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>One source of live component state and validation for desktop and XR input.</summary>
[DefaultExecutionOrder(-9600)]
public sealed class TechWiseSimulationRuntime : MonoBehaviour
{
    public static TechWiseSimulationRuntime Instance { get; private set; }
    public static event Action<TechWiseVrMistakeDetail> MistakeRecorded;
    public static event Action StateChanged;
    public readonly List<XRGrabInteractable> Parts = new();
    public readonly List<XRLockSocketInteractor> Sockets = new();
    public readonly List<TechWiseVrMistakeDetail> PracticeMistakes = new();
    public bool ManipulationLocked { get; private set; }
    public string Feedback { get; private set; } = "Follow the current step. Release an aligned part inside its socket.";
    public bool PracticeComplete { get; private set; }
    public string ConfigurationError { get; private set; }
    readonly Dictionary<XRGrabInteractable, XRSelectFilterDelegate> filters = new();
    readonly HashSet<string> performedSteps = new();
    readonly HashSet<XRGrabInteractable> touched = new();
    readonly Dictionary<string, double> lastMistake = new();
    readonly Dictionary<XRGrabInteractable, Pose> lockedPoses = new();
    readonly Dictionary<Collider, bool> colliderTriggerStates = new();
    readonly Dictionary<XRGrabInteractable, string> partSteps = new();
    readonly Dictionary<XRLockSocketInteractor, string> socketSteps = new();
    readonly HashSet<XRGrabInteractable> pendingReleases = new();
    readonly Dictionary<XRGrabInteractable, TechWiseAssemblyHistory.Snapshot> actionBefore = new();
    double startedAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (Instance != null) return;
        var root = new GameObject("TechWise Simulation State");
        DontDestroyOnLoad(root);
        root.AddComponent<TechWiseSimulationRuntime>();
    }

    void Awake() => Instance = this;
    void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; Refresh(); }
    void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; Clear(); if (Instance == this) Instance = null; }
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == LoadSceneMode.Single || scene == SceneManager.GetActiveScene()) Refresh();
    }

    void Clear()
    {
        foreach (var part in Parts)
        {
            if (part == null) continue;
            part.GetComponent<TechWiseRamInsertion>()?.ClearStage();
            if (filters.TryGetValue(part, out var filter)) part.selectFilters.Remove(filter);
            part.selectEntered.RemoveListener(OnGrab);
            part.selectExited.RemoveListener(OnRelease);
        }
        foreach (var socket in Sockets)
        {
            if (socket == null) continue;
            socket.selectEntered.RemoveListener(OnInstalled);
            socket.selectExited.RemoveListener(OnRemoved);
            socket.placementPreviewValid = null;
        }
        Parts.Clear(); Sockets.Clear(); filters.Clear(); touched.Clear(); lockedPoses.Clear(); colliderTriggerStates.Clear();
        partSteps.Clear(); socketSteps.Clear();
        pendingReleases.Clear(); actionBefore.Clear();
        performedSteps.Clear(); PracticeMistakes.Clear(); lastMistake.Clear();
    }

    internal void Refresh()
    {
        Clear();
        ConfigurationError = null;
        PracticeComplete = false;
        ManipulationLocked = TechWiseSimulationModeManager.IsCompetitionMode;
        startedAt = Time.realtimeSinceStartupAsDouble;
        if (SceneManager.GetActiveScene().name != "Singleplayer" && SceneManager.GetActiveScene().name != "Multiplayer") return;
        PrepareContinuousWorkbench();
        var tutorial = TechWiseTutorialRuntime.InTutorial;
        foreach (var part in FindObjectsByType<XRGrabInteractable>(tutorial ? FindObjectsInactive.Include : FindObjectsInactive.Exclude))
        {
            if (TechWiseSimulationModeManager.IsKnowledgeDisplay(part.transform) || part.GetComponent<TechWiseFastener>() != null) continue;
            // This authored tutorial prefab has no keychain; use its existing
            // cooler socket keys without changing the prefab used by other modes.
            if (tutorial && TechWiseSimulationModeManager.ResolveStepId(part.transform) == "CPUCooler" && part.GetComponent<IKeychain>() == null)
            {
                foreach (var target in FindObjectsByType<XRLockSocketInteractor>(FindObjectsInactive.Include))
                    if (TechWiseSimulationModeManager.ResolveStepId(target.transform) == "CPUCooler" && target.keychainLock != null)
                    {
                        var keys = part.gameObject.AddComponent<Keychain>();
                        foreach (var key in target.keychainLock.requiredKeys) keys.AddKey(key);
                        break;
                    }
            }
            if (part.GetComponent<IKeychain>() == null) continue;
            // The tutorial reveals loose parts from its authored shared tray one at a
            // time. Register dormant lesson parts, but never the legacy prebuilt board.
            var tutorialStep = TechWiseSimulationModeManager.ResolveStepId(part.transform);
            if (tutorial && (tutorialStep == null || tutorialStep == "Motherboard" && !part.gameObject.activeInHierarchy)) continue;
            Parts.Add(part);
            partSteps[part] = TechWiseSimulationModeManager.ResolveStepId(part.transform);
            if (partSteps[part] == "RAM" && part.GetComponent<TechWiseRamInsertion>() == null)
                part.gameObject.AddComponent<TechWiseRamInsertion>();
            var networkObject = part.GetComponent<Unity.Netcode.NetworkObject>();
            if (networkObject != null && !networkObject.IsSpawned) networkObject.AutoObjectParentSync = false;
            foreach (var partCollider in part.colliders)
                if (partCollider != null) colliderTriggerStates[partCollider] = partCollider.isTrigger;
            // A part must have one owner; sockets may take it only after the hand releases it.
            part.selectMode = InteractableSelectMode.Single;
            var filter = new XRSelectFilterDelegate(ValidateSelection);
            filters[part] = filter;
            part.selectFilters.Add(filter);
            part.selectEntered.AddListener(OnGrab);
            part.selectExited.AddListener(OnRelease);
            var body = part.GetComponent<Rigidbody>();
            if (body != null) body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
        var boardPart = Parts.Find(p => StepOf(p) == "Motherboard");
        Collider boardCollider = boardPart != null && boardPart.colliders.Count > 0 ? boardPart.colliders[0] : null;
        if (boardCollider != null)
        {
            foreach (var part in Parts)
            {
                if (part == null || part == boardPart) continue;
                foreach (var c in part.colliders)
                    if (c != null) Physics.IgnoreCollision(boardCollider, c, true);
            }
        }
        foreach (var socket in FindObjectsByType<XRLockSocketInteractor>(FindObjectsSortMode.None))
        {
            if (TechWiseSimulationModeManager.IsKnowledgeDisplay(socket.transform) || socket.GetComponent<TechWiseScrewHole>() != null) continue;
            Sockets.Add(socket);
            socketSteps[socket] = TechWiseSimulationModeManager.ResolveStepId(socket.transform);
            // Hover snapping bypasses deliberate release and orientation checks.
            socket.hoverSocketSnapping = false;
            socket.showInteractableHoverMeshes = TechWiseSimulationModeManager.IsPracticeMode;
            socket.showInteractableHoverMeshes = !TechWiseSimulationModeManager.IsCompetitionMode;
            socket.placementPreviewValid = hovered => PlacementProblem(socket, hovered as XRGrabInteractable, out _, out _) == null;
            socket.selectEntered.AddListener(OnInstalled);
            socket.selectExited.AddListener(OnRemoved);
            DisableTutorialSwaps(socket.selectEntered);
            DisableTutorialSwaps(socket.selectExited);
        }
        foreach (var step in TechWiseSimulationModeManager.GetExpectedOrder())
            if (FindPart(step) == null || FindSocket(step) == null)
                ConfigurationError = (ConfigurationError ?? "Missing simulation parts/targets: ") + step + " ";
        TechWiseComponentRecovery.RefreshTracking();
        StateChanged?.Invoke();
    }

    static void DisableTutorialSwaps(UnityEventBase calls)
    {
        for (int index = 0; index < calls.GetPersistentEventCount(); index++)
            if (calls.GetPersistentMethodName(index) == "SetActive")
                calls.SetPersistentListenerState(index, UnityEventCallState.Off);
    }

    static void PrepareContinuousWorkbench()
    {
        // The authored tutorial swaps a loose board and components for a prebuilt model.
        // In simulation, the original board itself must move with its actual sockets/parts.
        XRLockSocketInteractor cpuSocket = null, boardSocket = null;
        foreach (var socket in FindObjectsByType<XRLockSocketInteractor>(TechWiseTutorialRuntime.InTutorial ? FindObjectsInactive.Include : FindObjectsInactive.Exclude))
        {
            if (TechWiseSimulationModeManager.IsKnowledgeDisplay(socket.transform)) continue;
            var id = TechWiseSimulationModeManager.ResolveStepId(socket.transform);
            if (id == "CPU") cpuSocket = socket;
            if (id == "Motherboard") boardSocket = socket;
        }
        if (cpuSocket == null || boardSocket == null) return;
        var board = cpuSocket.transform.parent;
        // Prepare the retained workbench before controller training, while all grabs remain locked.
        // Otherwise the dormant tutorial group and the Ready gate wait on one another.
        if (TechWiseTutorialRuntime.InTutorial)
        {
            foreach(var root in new[]{board,boardSocket.transform})
                for(var t=root;t!=null;t=t.parent) if(!t.gameObject.activeSelf)t.gameObject.SetActive(true);
        }
        if (board == null || board.GetComponent<XRGrabInteractable>() != null) return;
        InteractionLayerMask prebuiltLayers = boardSocket.interactionLayers;
        foreach (var part in FindObjectsByType<XRGrabInteractable>(FindObjectsInactive.Include))
        {
            if (part == null || part.transform == board || TechWiseSimulationModeManager.IsKnowledgeDisplay(part.transform)) continue;
            if (TechWiseSimulationModeManager.ResolveStepId(part.transform) == "Motherboard")
            {
                prebuiltLayers = part.interactionLayers;
                part.gameObject.SetActive(false);
            }
        }
        var collider = board.gameObject.AddComponent<BoxCollider>();
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasBounds = false;
        foreach (var renderer in board.GetComponentsInChildren<Renderer>())
        {
            if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        collider.center = board.InverseTransformPoint(bounds.center);
        var scale = board.lossyScale;
        collider.size = new Vector3(bounds.size.x / Mathf.Abs(scale.x), bounds.size.y / Mathf.Abs(scale.y), bounds.size.z / Mathf.Abs(scale.z));
        foreach (var meshCollider in board.GetComponentsInChildren<MeshCollider>())
            if (!meshCollider.convex) meshCollider.enabled = false;
        var body = board.GetComponent<Rigidbody>();
        if (body == null) body = board.gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        var keychain = board.GetComponent<Keychain>();
        if (keychain == null) keychain = board.gameObject.AddComponent<Keychain>();
        foreach (var key in boardSocket.keychainLock.requiredKeys) keychain.AddKey(key);
        board.gameObject.SetActive(false);
        var grab = board.gameObject.AddComponent<XRGrabInteractable>();
        grab.interactionLayers = prebuiltLayers;
        grab.colliders.Clear(); grab.colliders.Add(collider);
        boardSocket.socketScaleMode = UnityEngine.XR.Interaction.Toolkit.Interactors.SocketScaleMode.None;
        var socketAttach = boardSocket.attachTransform;
        var caseBox = boardSocket.transform.parent;
        if (socketAttach != null && caseBox != null)
        {
            socketAttach.SetParent(caseBox, false);
            socketAttach.localPosition = new Vector3(0.0012755394f, -0.00050563813f, -0.07378109f);
            socketAttach.localRotation = Quaternion.identity;
            socketAttach.localScale = Vector3.one;
        }
        if (caseBox != null)
        {
            foreach (var t in caseBox.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t != caseBox && t.name.Equals("MotherBoard", StringComparison.OrdinalIgnoreCase))
                    t.gameObject.SetActive(false);
            }
        }

        var boardAttach = board.Find("Attach") ?? board.Find("AttachPoint");
        if (boardAttach == null)
        {
            var attachObj = new GameObject("AttachPoint");
            attachObj.transform.SetParent(board, false);
            attachObj.transform.localPosition = Vector3.zero;
            attachObj.transform.localRotation = Quaternion.identity;
            boardAttach = attachObj.transform;
        }

        grab.attachTransform = boardAttach;
        grab.movementType = XRBaseInteractable.MovementType.Kinematic;
        grab.throwOnDetach = false;
        board.gameObject.SetActive(true);
    }

    public string StepOf(XRGrabInteractable part) => part != null && partSteps.TryGetValue(part, out var step) ? step : null;
    public string StepOf(XRLockSocketInteractor socket) => socket != null && socketSteps.TryGetValue(socket, out var step) ? step : null;
    public XRGrabInteractable FindPart(string step) => Parts.Find(p => p != null && StepOf(p) == step);
    public XRLockSocketInteractor FindSocket(string step) => Sockets.Find(s => s != null && StepOf(s) == step);
    public bool IsInstalled(string step)
    {
        if (string.IsNullOrEmpty(step)) return false;
        bool found = false;
        foreach (var part in Parts)
        {
            if (part == null || StepOf(part) != step) continue;
            found = true;
            if (!IsPartInstalled(part)) return false;
        }
        found &= Parts.Count(p=>p!=null && StepOf(p)==step) == (TechWiseBuildDefinition.Find(step)?.quantity ?? 1);
        return found && (!TechWiseDetailedAssemblyRuntime.Active || TechWiseDetailedAssemblyRuntime.Instance.StepFastened(step));
    }
    public bool IsPartInstalled(XRGrabInteractable part) => Sockets.Exists(socket => socket != null && socket.isActiveAndEnabled && socket.IsSelecting(part) && (StepOf(socket) == StepOf(part) || Matches(socket, part.transform)));

    // Capture BEFORE releasing held parts at DONE. A held part cannot become complete merely
    // because submission itself cancels its grab. This snapshot is never recomputed for results.
    public TechWiseVrComponentResult[] CaptureAssessmentState()
    {
        var results = new List<TechWiseVrComponentResult>();
        var runtime = TechWiseDetailedAssemblyRuntime.Instance;
        bool ready = runtime != null && runtime.Ready;
        bool removal = TechWiseSimulationModeManager.IsDisassembly;
        void Result(string id, string step, string label, bool done, string issue, string explanation, string correction)
        {
            results.Add(new TechWiseVrComponentResult { component_id=id, step=step, component=label, complete=done,
                issue=done?"":issue, explanation=done?"":explanation, correction=done?"":correction });
        }
        foreach (var definition in TechWiseBuildDefinition.Components)
        {
            var parts=Parts.Where(p=>p!=null && StepOf(p)==definition.id).OrderBy(p=>p.name).ToArray();
            for(int i=0;i<definition.quantity;i++)
            {
                var part=i<parts.Length?parts[i]:null;
                var socket=part==null?null:Sockets.Find(t=>t!=null&&t.IsSelecting(part));
                bool complete=false; string problem="Missing or unfinished component.", correction="Install the correct component, then finish its required fastening.", kind="missing_component";
                if(part!=null && !IsHeld(part) && ready)
                {
                    if(removal) { complete=PartRemoved(part) && runtime.Fasteners.Where(f=>f.ComponentId==definition.id).All(f=>f.Removed); kind="incomplete_removal"; problem="Component or its fasteners remain installed."; correction="Loosen and remove every screw, then remove and release the component clear of its mount."; }
                    else if(socket!=null)
                    {
                        problem=PlacementProblem(socket,part,out kind,out correction);
                        complete=problem==null && runtime.StepFastened(definition.id);
                        if(problem==null && !complete) { kind="missing_fastening"; problem="Required latches, paste or individual screws are unfinished."; correction="Complete every insertion and tightening requirement for this component."; }
                    }
                }
                Result(definition.id+":"+(i+1),definition.id,definition.name+" "+(i+1),complete,kind,problem,correction);
            }
            for(int i=0;i<definition.screws;i++)
            {
                string id=TechWiseBuildDefinition.HoleId(definition.id,i);
                var screw=ready?runtime.Fasteners.FirstOrDefault(f=>f.Id==id):null;
                Result(id+"/insert",definition.id,id+" "+(removal?"removed":"inserted"),screw!=null&&(removal?screw.Removed:screw.Inserted),"missing_fastening",
                    removal?"Screw has not been removed from its hole.":"Screw has not been manually inserted in its assigned hole.",removal?"Loosen, grip and remove this screw.":"Grab this numbered screw, align it with its matching hole and release.");
                Result(id+"/tight",definition.id,id+" "+(removal?"loosened":"tightened"),screw!=null&&(removal?screw.Progress==0:screw.Complete),"missing_fastening",
                    removal?"Screw remains tightened.":"Screw is not fully tightened.","Hold the screwdriver tip against this screw, align its shaft and hold the tool activation input.");
            }
        }
        Result("CPU/cover","CPU","CPU retention cover",ready&&(removal?!runtime.Cover.Closed:runtime.Cover.Closed),"missing_dependency","CPU cover is not in the required state.",removal?"Open the lever, then lift the cover.":"Lower the cover and release it.");
        Result("CPU/lever","CPU","CPU locking arm",ready&&(removal?!runtime.Lever.Closed:runtime.Lever.Closed),"missing_dependency","CPU locking arm is not in the required state.",removal?"Lift and release the arm after removing the cooler.":"Lower and release the arm after closing the cover.");
        Result("CPU/paste","CPUCooler","Thermal paste",ready&&(removal?IsStepComplete("CPU"):runtime.PasteApplied),"missing_dependency","Required thermal-paste state is incomplete.",removal?"Remove the CPU after its cooler and locks are cleared.":"Apply paste to the locked CPU before installing the cooler.");
        Result("Case/panel","CasePanel","Case side panel",ready&&(removal?runtime.CaseAccessOpen:runtime.CasePrepared&&runtime.PanelInstalled),"missing_dependency","The case panel is not in its required final state.",removal?"Remove and set aside the case panel first.":"Complete the build, then align and release the side panel onto the case.");
        return results.ToArray();
    }
    bool PartRemoved(XRGrabInteractable part)
    {
        if(part==null || !touched.Contains(part) || IsPartInstalled(part) || IsHeld(part)) return false;
        return !Sockets.Any(s=>s!=null && StepOf(s)==StepOf(part) && Vector3.Distance(part.transform.position,s.GetAttachTransform(part).position)<.2f);
    }
    internal string[] CompletedActions()=>performedSteps.ToArray();
    internal XRGrabInteractable[] TouchedParts()=>touched.ToArray();
    internal void RestoreActions(string[] completed,XRGrabInteractable[] handled)
    {
        performedSteps.Clear(); foreach(var id in completed) performedSteps.Add(id);
        touched.Clear(); foreach(var part in handled) if(part!=null) touched.Add(part);
        pendingReleases.Clear(); actionBefore.Clear(); PracticeComplete=false;
        Feedback="Previous completed action restored. Repeat that action to continue.";
        StateChanged?.Invoke();
    }
    public bool IsStepComplete(string step)
    {
        if (!TechWiseSimulationModeManager.IsDisassembly) return IsInstalled(step);
        var definition=TechWiseBuildDefinition.Find(step);
        if(definition==null) return false;
        var parts=Parts.Where(p=>p!=null&&StepOf(p)==step).ToArray();
        if(parts.Length!=definition.quantity || parts.Any(p=>!PartRemoved(p))) return false;
        var runtime=TechWiseDetailedAssemblyRuntime.Instance;
        return runtime!=null && runtime.Ready && runtime.Fasteners.Where(f=>f.ComponentId==step).Count(f=>f.Removed)==definition.screws;
    }
    public string CurrentStep
    {
        get
        {
            if (TechWiseDetailedAssemblyRuntime.Active && TechWiseDetailedAssemblyRuntime.Instance.Ready &&
                TechWiseDetailedAssemblyRuntime.Instance.CurrentPartStep != null)
                return TechWiseDetailedAssemblyRuntime.Instance.CurrentPartStep;
            foreach (var step in TechWiseSimulationModeManager.GetExpectedOrder())
                if (!IsStepComplete(step)) return step;
            return null;
        }
    }
    public static bool Matches(XRLockSocketInteractor socket, Transform part)
    {
        if (socket == null || part == null || socket.keychainLock == null) return false;
        var kc = part.GetComponent<IKeychain>() ?? part.GetComponentInParent<IKeychain>() ?? part.GetComponentInChildren<IKeychain>();
        return socket.keychainLock.CanUnlock(kc);
    }
    public static bool IsHeld(XRGrabInteractable part)
    {
        foreach (var owner in part.interactorsSelecting) if (owner is not XRSocketInteractor) return true;
        return false;
    }

    bool ValidateSelection(IXRSelectInteractor interactor, IXRSelectInteractable selectable)
    {
        if (TechWiseAssemblyHistory.Restoring) return true;
        if (TechWiseTutorialRuntime.ControlsPending && !TechWiseDisassemblyRuntime.IsPreparing) return interactor is XRSocketInteractor existing && existing.IsSelecting(selectable);
        if (TechWiseDisassemblyRuntime.IsPreparing) return interactor is XRLockSocketInteractor;
        if (ManipulationLocked) return interactor is XRSocketInteractor socketOwner && socketOwner.IsSelecting(selectable);
        if (interactor is not XRLockSocketInteractor socket)
        {
            if (selectable is not XRGrabInteractable part) return true;
            if (TechWiseDetailedAssemblyRuntime.Active && !TechWiseDetailedAssemblyRuntime.Instance.CanGrab(StepOf(part))) return false;
            if (TechWiseDetailedAssemblyRuntime.Active && TechWiseSimulationModeManager.IsAssembly && IsPartInstalled(part)) return false;
            return StepOf(part) != "Motherboard" || MotherboardReady;
        }
        if (socket.IsSelecting(selectable)) return true;
        if (selectable is XRGrabInteractable held && IsHeld(held)) return false;
        if (TechWiseSimulationModeManager.IsAssembly && selectable.transform.TryGetComponent<TechWiseRamInsertion>(out var ram) && !ram.AllowSocketSelection) return false;
        return PlacementProblem(socket, selectable as XRGrabInteractable, out _, out _) == null;
    }

    public bool MotherboardReady => !TechWiseSimulationModeManager.IsAssembly ||
        (IsInstalled("CPU") && IsInstalled("RAM") && IsInstalled("M2") && IsInstalled("CPUCooler"));

    public string PlacementProblem(XRLockSocketInteractor socket, XRGrabInteractable part, out string kind, out string correction)
    {
        kind = "invalid_placement"; correction = "Release the component inside a compatible, empty target.";
        if (part == null || socket == null) return "The component or target is unavailable.";
        if (!Matches(socket, part.transform))
        {
            kind = "wrong_part";
            correction = $"Use the matching {Label(TechWiseSimulationModeManager.ResolveStepId(part.transform))} target.";
            return "This component does not fit this target.";
        }
        if (socket.hasSelection && !socket.IsSelecting(part)) return "This target is already occupied.";
        if (StepOf(socket) == "RAM" && Parts.Any(p => p != null && p != part && p.TryGetComponent<TechWiseRamInsertion>(out var ram) && ram.Socket == socket))
            return "This slot already contains aligned RAM. Push that module down before using another slot.";
        var partAttach = part.GetAttachTransform(socket);
        var socketAttach = socket.GetAttachTransform(part);
        float attachDist = Vector3.Distance(partAttach.position, socketAttach.position);
        if (TechWiseDetailedAssemblyRuntime.Active && StepOf(part) == "M2")
            attachDist = TechWiseDetailedAssemblyRuntime.Instance.M2ConnectorDistance(part);
        var rule = socket.GetComponent<TechWisePlacementRule>();
        if (attachDist > (rule != null ? rule.distanceTolerance : .035f))
        {
            kind = "invalid_target";
            correction = "Release the component inside its target area.";
            return "The component is outside the target area.";
        }
        if (rule == null || rule.orientationTolerance > 0f)
        {
            float angle = Quaternion.Angle(partAttach.rotation, socketAttach.rotation);
            if (angle > (rule != null ? rule.orientationTolerance : 12f))
            {
                kind = "incorrect_orientation"; correction = "Rotate the component to align its keyed edge with the target before releasing it.";
                return "The component is not aligned with the target.";
            }
        }
        var step = TechWiseSimulationModeManager.ResolveStepId(part.transform);
        if (TechWiseDetailedAssemblyRuntime.Active)
        {
            var prerequisite = TechWiseDetailedAssemblyRuntime.Instance.PrerequisiteProblem(step);
            if (prerequisite != null) { kind = "missing_dependency"; correction = prerequisite; return prerequisite; }
        }
        if (!TechWiseAssemblyHistory.Restoring && !TechWiseDisassemblyRuntime.IsPreparing && step == "Motherboard" && !MotherboardReady)
        {
            kind = "missing_dependency";
            correction = "Install the CPU, all RAM modules, M.2 SSD and CPU cooler before moving the motherboard into the case.";
            return "The motherboard assembly is incomplete.";
        }
        var dependency = rule != null && !string.IsNullOrEmpty(rule.requiredInstalledStep) ? rule.requiredInstalledStep :
            step == "CPUCooler" ? "CPU" : step == "GPU" ? "Motherboard" : null;
        if (TechWiseSimulationModeManager.IsAssembly && !TechWiseDisassemblyRuntime.IsPreparing && !TechWiseAssemblyHistory.Restoring && dependency != null && !IsInstalled(dependency))
        {
            kind = "missing_dependency"; correction = $"Install {Label(dependency)} before this component.";
            return "A required supporting component has not been installed.";
        }
        if (rule != null && !rule.fastenAfterPlacement && !rule.FasteningReady)
        {
            kind = "missing_fastening"; correction = "Complete the configured fastening interaction first.";
            return "The fastening requirement is incomplete.";
        }
        return null;
    }

    void OnGrab(SelectEnterEventArgs args)
    {
        if (args.interactorObject is XRSocketInteractor || TechWiseDisassemblyRuntime.IsPreparing || ManipulationLocked) return;
        if (args.interactableObject is XRGrabInteractable part)
        {
            if (TechWiseSimulationModeManager.IsAssembly || !actionBefore.ContainsKey(part)) actionBefore[part] = TechWiseAssemblyHistory.Capture();
            part.GetComponent<TechWiseRamInsertion>()?.BeginPress(args.interactorObject);
            touched.Add(part);
            part.throwOnDetach = false;
            // The board's solid collision envelope must not push a held CPU/RAM
            // away from the recessed attachment point. Restore loose-part physics on release.
            foreach (var collider in part.colliders)
                if (collider != null) collider.isTrigger = true;
        }
    }

    void AttachPartToSocket(XRGrabInteractable part, XRLockSocketInteractor socket)
    {
        if (part == null || socket == null) return;
        if (IsHeld(part) || PlacementProblem(socket, part, out _, out _) != null || socket.interactionManager == null) return;
        if (TechWiseSimulationModeManager.IsAssembly && part.TryGetComponent<TechWiseRamInsertion>(out var ram) && !ram.AllowSocketSelection)
        {
            if (!ram.Staged)
            {
                ram.Stage(socket); pendingReleases.Remove(part);
                if (actionBefore.Remove(part, out var beforeStage)) TechWiseAssemblyHistory.Commit(beforeStage);
                RamFeedback(ram.Guidance);
            }
            return;
        }
        if (!socket.interactionManager.IsSelectPossible((IXRSelectInteractor)socket, part)) return;
        pendingReleases.Remove(part);
        foreach (var c in part.colliders)
            if (c != null) c.isTrigger = true;
        var body = part.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        var socketAttach = socket.GetAttachTransform(part);
        var partAttach = part.GetAttachTransform(socket);
        if (socketAttach != null && partAttach != null)
        {
            Quaternion rotDiff = socketAttach.rotation * Quaternion.Inverse(partAttach.rotation);
            part.transform.rotation = rotDiff * part.transform.rotation;
            part.transform.position += socketAttach.position - partAttach.position;
        }
        else if (socketAttach != null)
        {
            part.transform.SetPositionAndRotation(socketAttach.position, socketAttach.rotation);
        }
        var savedScale = part.transform.localScale;
        if (socket.interactionManager != null)
        {
            foreach (var interactor in part.interactorsSelecting.ToArray())
            {
                if (interactor != (IXRSelectInteractor)socket)
                    socket.interactionManager.SelectExit(interactor, part);
            }
            if (socket.interactionManager.IsSelectPossible((IXRSelectInteractor)socket, part))
                socket.interactionManager.SelectEnter((IXRSelectInteractor)socket, part);
        }
        part.transform.localScale = savedScale;
        var step = TechWiseSimulationModeManager.ResolveStepId(part.transform);
        if (TechWiseSimulationModeManager.IsAssembly && IsInstalled(step))
            RecordPracticeStep(step);
        if (IsPartInstalled(part) && actionBefore.Remove(part, out var before)) TechWiseAssemblyHistory.Commit(before);
        StateChanged?.Invoke();
    }

    void OnRelease(SelectExitEventArgs args)
    {
        if (args.interactableObject is XRGrabInteractable released && released.TryGetComponent<TechWiseRamInsertion>(out var ram) && ram.Release(args)) return;
        if (args.isCanceled || TechWisePauseSession.Active || TechWiseAssemblyHistory.Restoring) return;
        if (args.interactorObject is XRSocketInteractor || TechWiseDisassemblyRuntime.IsPreparing || ManipulationLocked) return;
        if (args.interactableObject is not XRGrabInteractable part) return;

        if (TechWiseSimulationModeManager.IsDisassembly)
        {
            foreach(var c in part.colliders) if(c!=null) c.isTrigger=false;
            if(PartRemoved(part) && actionBefore.Remove(part,out var before)) TechWiseAssemblyHistory.Commit(before);
            StateChanged?.Invoke(); return;
        }
        XRLockSocketInteractor candidate = null;
        float bestDist = float.MaxValue;
        foreach (var socket in Sockets)
        {
            if (socket == null || !socket.isActiveAndEnabled || socket.hasSelection) continue;
            if (socket.transform.IsChildOf(part.transform)) continue;
            if (!Matches(socket, part.transform)) continue;

            bool hovering = socket.interactablesHovered.Contains(part);
            var prob = PlacementProblem(socket, part, out _, out _);
            if (prob != null) continue;

            float d = Vector3.Distance(part.GetAttachTransform(socket).position, socket.GetAttachTransform(part).position);
            if (hovering || d < 0.35f)
            {
                if (d < bestDist)
                {
                    bestDist = d;
                    candidate = socket;
                }
            }
        }

        if (candidate != null)
        {
            AttachPartToSocket(part, candidate);
            if (!IsPartInstalled(part))
                pendingReleases.Add(part);
            return;
        }

        if (args.interactorObject is not XRSocketInteractor)
            foreach (var partCollider in part.colliders)
                if (partCollider != null && colliderTriggerStates.TryGetValue(partCollider, out var originalTrigger)) partCollider.isTrigger = originalTrigger;
        if (args.isCanceled) return;

        // Resolve after XRI finishes releasing the hand and restoring its Rigidbody state.
        pendingReleases.Add(part);
        XRLockSocketInteractor nearest = null;
        float distance = float.MaxValue;
        foreach (var socket in Sockets)
        {
            if (socket == null || !socket.isActiveAndEnabled) continue;
            // A moving motherboard carries its CPU/RAM sockets. Those are not destinations
            // for the motherboard itself and must not generate release mistakes.
            if (socket.transform.IsChildOf(part.transform)) continue;
            var current = Vector3.Distance(part.GetAttachTransform(socket).position, socket.GetAttachTransform(part).position);
            var attemptRule=socket.GetComponent<TechWisePlacementRule>();
            float attemptRadius=Mathf.Max(.08f,(attemptRule!=null?attemptRule.distanceTolerance:.035f)*2.5f);
            if (current >= attemptRadius && !socket.interactablesHovered.Contains(part)) continue;
            // Adjacent RAM/CPU targets can be closer than the compatible target.
            bool matches = Matches(socket, part.transform);
            bool nearestMatches = nearest != null && Matches(nearest, part.transform);
            if ((matches && !nearestMatches) || (matches == nearestMatches && current < distance))
            { distance = current; nearest = socket; }
        }
        // Release attempts only, never continuous hover polling: no phantom mistake spam.
        if (nearest != null && (distance < 0.35f || nearest.interactablesHovered.Contains(part)))
        {
            var problem = PlacementProblem(nearest, part, out var kind, out var correction);
            if (problem != null) RecordMistake(kind, TechWiseSimulationModeManager.ResolveStepId(part.transform), problem, correction);
        }
        StateChanged?.Invoke();
    }

    void OnInstalled(SelectEnterEventArgs args)
    {
        if (args.interactableObject is XRGrabInteractable installed)
        {
            foreach (var collider in installed.colliders)
                if (collider != null) collider.isTrigger = true;
            var body = installed.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
        }
        if (TechWiseDisassemblyRuntime.IsPreparing || ManipulationLocked || TechWiseAssemblyHistory.Restoring) return;
        var step = TechWiseSimulationModeManager.ResolveStepId(args.interactableObject.transform);
        if (TechWiseSimulationModeManager.IsAssembly && IsInstalled(step)) RecordPracticeStep(step);
        StateChanged?.Invoke();
    }
    void OnRemoved(SelectExitEventArgs args)
    {
        if (args.isCanceled || TechWiseAssemblyHistory.Restoring) return;
        if (TechWiseSimulationModeManager.IsDisassembly && args.interactableObject is XRGrabInteractable part)
        {
            var snapshot=TechWiseAssemblyHistory.Capture();
            var saved=snapshot?.parts.Find(p=>p.part==part);
            if(saved!=null) saved.socket=args.interactorObject as XRLockSocketInteractor;
            actionBefore[part]=snapshot;
        }
        StateChanged?.Invoke();
    }

    void Update()
    {
        if (TechWisePauseSession.Active) return;
        if (TechWiseDisassemblyRuntime.IsPreparing || ConfigurationError != null || Parts.Count == 0) return;
        if (TechWiseSimulationModeManager.IsAssembly)
        {
            foreach (var step in TechWiseSimulationModeManager.GetExpectedOrder()) if (IsStepComplete(step)) RecordPracticeStep(step);
        }
        else if (TechWiseSimulationModeManager.IsDisassembly)
        {
            foreach (var step in TechWiseSimulationModeManager.GetExpectedOrder()) if (IsStepComplete(step)) RecordPracticeStep(step);
        }
        PracticeComplete = TechWiseDetailedAssemblyRuntime.Active ? (TechWiseSimulationModeManager.IsDisassembly ? TechWiseDetailedAssemblyRuntime.Instance.RemovalComplete : TechWiseDetailedAssemblyRuntime.Instance.BuildComplete) : CurrentStep == null;
    }
    void LateUpdate()
    {
        if (TechWisePauseSession.Active) return;
        if (!ManipulationLocked && !TechWiseDisassemblyRuntime.IsPreparing)
        {
            foreach (var part in pendingReleases.ToArray())
            {
                if (part == null || IsPartInstalled(part)) { pendingReleases.Remove(part); continue; }
                XRLockSocketInteractor target = null;
                float distance = float.MaxValue;
                foreach (var socket in Sockets)
                {
                    if (socket == null || !socket.isActiveAndEnabled || !socket.socketActive || socket.hasSelection ||
                        socket.transform.IsChildOf(part.transform) ||
                        (socket.interactionLayers.value & part.interactionLayers.value) == 0 ||
                        !Matches(socket, part.transform)) continue;

                    bool hovering = socket.interactablesHovered.Contains(part);
                    var prob = PlacementProblem(socket, part, out _, out _);
                    if (prob != null) continue;

                    float current = Vector3.Distance(part.GetAttachTransform(socket).position, socket.GetAttachTransform(part).position);
                    if (hovering || current < 0.35f)
                    {
                        if (current < distance) { distance = current; target = socket; }
                    }
                }
                if (target != null)
                {
                    AttachPartToSocket(part, target);
                    if (IsPartInstalled(part)) pendingReleases.Remove(part);
                }
            }
            pendingReleases.Clear();

            foreach (var part in Parts)
            {
                if (part == null || part.isSelected || IsPartInstalled(part)) continue;
                if (part.TryGetComponent<TechWiseRamInsertion>(out var ram) && ram.Staged) continue;
                var body = part.GetComponent<Rigidbody>();
                if (body == null) continue;
                bool boardLocked = StepOf(part) == "Motherboard" && !MotherboardReady;
                if (boardLocked) body.isKinematic = true;
                else if (touched.Contains(part) || StepOf(part) == "Motherboard")
                {
                    body.isKinematic = false;
                    body.useGravity = true;
                    body.detectCollisions = true;
                    foreach (var collider in part.colliders)
                        if (collider != null) collider.isTrigger = false;
                }
            }
        }
        else
        {
            pendingReleases.Clear();
        }
        foreach (var pair in lockedPoses)
            if (pair.Key != null) pair.Key.transform.SetPositionAndRotation(pair.Value.position, pair.Value.rotation);
    }
    void RecordPracticeStep(string step)
    {
        if (string.IsNullOrEmpty(step) || performedSteps.Contains(step) || !TechWiseSimulationModeManager.GetExpectedOrder().Contains(step)) return;
        string expected = null;
        foreach (var id in TechWiseSimulationModeManager.GetExpectedOrder())
        {
            if (id==step) break;
            // A screw can finish immediately before the next part's release in this frame.
            // Validate live prerequisites, not the previous Update's completion cache.
            if (!IsStepComplete(id)) { expected=id; break; }
            performedSteps.Add(id);
        }
        if (expected != null && expected != step)
            RecordMistake("wrong_order", step, $"{Label(step)} was handled before {Label(expected)}.", $"Follow the current step: {Label(expected)}.");
        else Feedback = $"{Label(step)} completed.";
        performedSteps.Add(step);
    }
    internal void CompleteRamInsertion(XRGrabInteractable part, XRLockSocketInteractor socket) => AttachPartToSocket(part, socket);
    internal void RamFeedback(string text) { Feedback = text; StateChanged?.Invoke(); }
    public void RecordMistake(string kind, string step, string explanation, string correction)
    {
        var key = kind + ":" + step;
        var now = Time.realtimeSinceStartupAsDouble;
        if (lastMistake.TryGetValue(key, out var last) && now - last < 1.5) return;
        lastMistake[key] = now;
        var detail = new TechWiseVrMistakeDetail { kind = kind, attempted_step = Label(step), explanation = explanation,
            correction = correction, occurred_at_seconds = (int)(now - startedAt) };
        Feedback = explanation + " " + correction;
        if (TechWiseSimulationModeManager.IsPracticeMode)
        {
            PracticeMistakes.Add(detail); Feedback = explanation + " " + correction;
        }
        MistakeRecorded?.Invoke(detail);
    }
    public void SetManipulationLocked(bool locked)
    {
        ManipulationLocked = locked;
        if (!locked) { lockedPoses.Clear(); startedAt = Time.realtimeSinceStartupAsDouble; return; }
        // Keep socket ownership for final-state validation; cancel only hands/rays.
        var runtime=TechWiseDetailedAssemblyRuntime.Instance;
        var all=Parts.AsEnumerable();
        if(runtime!=null && runtime.Ready) all=all.Concat(runtime.Fasteners.Select(s=>s.Grab)).Concat(TechWiseAssemblyTool.Tools.Select(t=>t.GetComponent<XRGrabInteractable>())).Append(runtime.SidePanel);
        foreach (var part in all.Distinct())
        {
            if (part == null) continue;
            var pose = new Pose(part.transform.position, part.transform.rotation);
            var owners = new List<IXRSelectInteractor>(part.interactorsSelecting);
            foreach (var owner in owners)
                if (owner is not XRSocketInteractor && part.interactionManager != null) part.interactionManager.SelectCancel(owner, part);
            lockedPoses[part] = pose;
            var body = part.GetComponent<Rigidbody>();
            if (body != null) { if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; } body.isKinematic = true; }
        }
    }
    public static string Label(string step) => TechWiseBuildDefinition.Find(step)?.name ?? (step switch { "CPUCooler" => "CPU cooler", "GPUConnector" => "GPU connector", "M2" => "M.2 SSD", "Storage" => "SATA storage", "PSU" => "Power supply", null => "component", _ => step });
}
