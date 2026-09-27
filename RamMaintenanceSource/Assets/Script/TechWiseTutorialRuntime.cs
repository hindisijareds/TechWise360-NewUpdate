using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using CommonUsages = UnityEngine.XR.CommonUsages;

/// <summary>Scene-scoped onboarding using the existing XRI rig and simulation events.</summary>
[DefaultExecutionOrder(-9000)]
public sealed class TechWiseTutorialRuntime : MonoBehaviour
{
    internal enum Stage { Welcome, Point, Move, Turn, Grab, Manipulate, Place, ControlsComplete, Assembly, Finished }
    internal static TechWiseTutorialRuntime Instance { get; private set; }
    internal static bool InTutorial => TechWiseSimulationModeManager.IsTutorialMode &&
        SceneManager.GetActiveScene().name == "Singleplayer";
    internal static bool ControlsPending => InTutorial && (Instance == null || Instance.stage < Stage.Assembly);
    internal Stage stage = Stage.Welcome;
    internal string Heading { get; private set; } = "Tutorial Mode";
    internal string Instructions { get; private set; } = "Press Let's Get Started to learn the VR controls, then build your PC.";
    internal string Feedback { get; private set; } = "";

    Button startButton;
    Button.ButtonClickedEvent assemblyStart;
    GameObject welcome;
    bool initialized, startedAssembly;
    XROrigin origin;
    Camera playerCamera;
    TechWiseTutorialView view;
    XRGrabInteractable practiceObject;
    IXRSelectInteractor holdingHand;
    Transform practiceTarget;
    Vector3 practiceHome, previousOrigin, manipulationPosition;
    Quaternion previousOriginRotation, manipulationRotation;
    float movementDistance, turnDegrees, movedObjectDistance, rotatedObjectDegrees, stageTime, refreshTime, releaseTime;
    bool gripArmed, rotatedWithStick, translatedWithStick, waitingForRelease;
    readonly Dictionary<string, InputAction> actions = new();
    readonly Dictionary<Rigidbody, (bool kinematic, bool gravity, Pose pose)> frozenBodies = new();
    readonly HashSet<string> acknowledgedSteps = new();
    readonly List<XRGrabInteractable> listenedParts = new();
    readonly Dictionary<UnityEngine.XR.Content.Interaction.XRLockSocketInteractor, bool> previewStates = new();
    string previousStep;
    float lastErrorSound = -10;
    float lastTurnInput = -10;
    AudioSource audioSource;
    AudioClip successTone, errorTone;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (Instance != null) return;
        var root = new GameObject("TechWise Interactive Tutorial Runtime");
        DontDestroyOnLoad(root);
        root.AddComponent<TechWiseTutorialRuntime>();
    }

    void Awake() => Instance = this;
    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TechWiseSimulationRuntime.StateChanged += OnAssemblyStateChanged;
        TechWiseSimulationRuntime.MistakeRecorded += OnMistake;
        if (InTutorial) StartCoroutine(InitializeScene());
    }
    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        TechWiseSimulationRuntime.StateChanged -= OnAssemblyStateChanged;
        TechWiseSimulationRuntime.MistakeRecorded -= OnMistake;
        StopAllCoroutines();
        ClearSession();
        if (Instance == this) Instance = null;
    }
    void OnDestroy()
    {
        if (successTone != null) Destroy(successTone);
        if (errorTone != null) Destroy(errorTone);
    }
    void OnApplicationQuit() => initialized = false;
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene != SceneManager.GetActiveScene()) return;
        StopAllCoroutines();
        ClearSession();
        if (InTutorial) StartCoroutine(InitializeScene());
    }
    IEnumerator InitializeScene()
    {
        // Other simulation runtimes finish their initial scene setup first.
        yield return null;
        while (InTutorial && !initialized)
        {
            Initialize();
            if (!initialized) yield return new WaitForSecondsRealtime(0.2f);
        }
    }
    internal void Initialize()
    {
        if (!InTutorial || initialized || TechWiseDetailedAssemblyRuntime.Instance == null || !TechWiseDetailedAssemblyRuntime.Instance.Ready || TechWiseDisassemblyRuntime.IsPreparing) return;
        var label = FindObjectsByType<TMP_Text>(FindObjectsInactive.Include)
            .FirstOrDefault(t => t != null && string.Equals(t.text?.Trim(), "Let's get started", StringComparison.OrdinalIgnoreCase));
        startButton = label != null ? label.GetComponentInParent<Button>(true) : null;
        origin = FindAnyObjectByType<XROrigin>();
        playerCamera = origin != null ? origin.Camera : Camera.main;
        if (startButton == null || playerCamera == null || TechWiseSimulationRuntime.Instance == null) return;
        assemblyStart = startButton.onClick;
        startButton.onClick = new Button.ButtonClickedEvent();
        startButton.onClick.AddListener(BeginControls);
        welcome = startButton.transform.parent.gameObject;
        stage = Stage.Welcome;
        initialized = true;
        BindActions();
        CaptureBodies();
        SetupAudio();
        SetStageText();
        Debug.Log("[Tutorial] Ready: Let's Get Started opens controller training.");
    }
    void ClearSession()
    {
        if (startButton != null && assemblyStart != null) startButton.onClick = assemblyStart;
        RestoreBodies();
        foreach (var part in listenedParts)
            if (part != null) part.selectExited.RemoveListener(OnAssemblyReleased);
        foreach (var pair in previewStates)
            if (pair.Key != null) pair.Key.showInteractableHoverMeshes = pair.Value;
        listenedParts.Clear(); previewStates.Clear(); actions.Clear(); acknowledgedSteps.Clear();
        if (practiceObject != null) Destroy(practiceObject.gameObject);
        if (view != null) Destroy(view.gameObject);
        practiceObject = null; practiceTarget = null; view = null; holdingHand = null;
        assemblyStart = null; startButton = null; welcome = null;
        initialized = false; startedAssembly = false; waitingForRelease = false;
        stage = Stage.Welcome; previousStep = null;
        Heading = "Tutorial Mode";
        Instructions = "Press Let's Get Started to learn the VR controls, then build your PC.";
        Feedback = "";
    }
    void CaptureBodies()
    {
        var state = TechWiseSimulationRuntime.Instance;
        if (state == null) return;
        foreach (var part in state.Parts)
        {
            var body = part != null ? part.GetComponent<Rigidbody>() : null;
            if (body != null && !frozenBodies.ContainsKey(body))
                frozenBodies.Add(body, (body.isKinematic, body.useGravity, new Pose(body.position, body.rotation)));
        }
    }
    void RestoreBodies()
    {
        foreach (var pair in frozenBodies)
            if (pair.Key != null) { pair.Key.isKinematic = pair.Value.kinematic; pair.Key.useGravity = pair.Value.gravity; }
        frozenBodies.Clear();
    }
    void LateUpdate()
    {
        if (!initialized || !ControlsPending) return;
        foreach (var pair in frozenBodies)
        {
            if (pair.Key == null) continue;
            pair.Key.isKinematic = true;
            pair.Key.transform.SetPositionAndRotation(pair.Value.pose.position, pair.Value.pose.rotation);
        }
    }
    internal void BeginControls()
    {
        if (!initialized || stage != Stage.Welcome) return;
        welcome.SetActive(false);
        var root = new GameObject("TechWise Tutorial Lesson");
        view = root.AddComponent<TechWiseTutorialView>();
        view.Create(playerCamera, this);
        SetStage(Stage.Point);
    }
    internal void ConfirmPointer(bool trackedTriggerClick)
    {
        if (stage == Stage.Point && trackedTriggerClick) SetStage(Stage.Move);
    }
    internal void SkipControls()
    {
        if (!initialized || stage == Stage.Welcome || stage >= Stage.Assembly) return;
        StartAssembly();
    }
    void SetStage(Stage next)
    {
        if (stage == next) return;
        if (stage > Stage.Welcome) PlayTone(true);
        stage = next; stageTime = Time.unscaledTime;
        Feedback = ""; movementDistance = 0; turnDegrees = 0;
        lastTurnInput = -10;
        previousOrigin = origin != null ? origin.transform.position : Vector3.zero;
        previousOriginRotation = origin != null ? origin.transform.rotation : Quaternion.identity;
        if (next == Stage.Grab) CreatePracticeObject();
        if (next == Stage.Manipulate) ResetManipulationBaseline();
        SetStageText();
        Debug.Log("[Tutorial] Stage: " + stage);
    }
    void ResetManipulationBaseline()
    {
        if (practiceObject == null) return;
        manipulationPosition = practiceObject.transform.position;
        manipulationRotation = practiceObject.transform.rotation;
        movedObjectDistance = 0; rotatedObjectDegrees = 0;
        rotatedWithStick = false; translatedWithStick = false;
    }
    void Update()
    {
        if (TechWisePauseSession.Active) return;
        if (!initialized || !InTutorial) return;
        if (stage == Stage.Welcome) return;
        if (stage == Stage.ControlsComplete && Time.unscaledTime - stageTime >= 2f) StartAssembly();
        if (stage == Stage.Move || stage == Stage.Turn) ObserveLocomotion();
        if (stage == Stage.Manipulate) ObserveManipulation();
        if (practiceObject != null && stage <= Stage.Place)
        {
            if (waitingForRelease && !practiceObject.isSelected && Time.unscaledTime >= releaseTime)
            { waitingForRelease = false; CheckPracticePlacement(); }
            if (!practiceObject.isSelected && (practiceObject.transform.position.y < practiceHome.y - 0.8f ||
                Vector3.Distance(practiceObject.transform.position, practiceHome) > 2.5f)) ResetPracticeObject();
        }
        if (Time.unscaledTime < refreshTime) return;
        refreshTime = Time.unscaledTime + 0.1f;
        if (stage == Stage.Assembly) RefreshAssemblyGuide();
        else if (stage < Stage.Assembly) SetStageText();
        if (view != null) view.Refresh(Heading, Instructions, Feedback, stage);
    }
    void ObserveLocomotion()
    {
        if (origin == null) return;
        var position = origin.transform.position;
        var rotation = origin.transform.rotation;
        var move = ReadVector("XRI Left Locomotion/Move");
        var turn = ReadVector("XRI Right Locomotion/Turn");

        // Count rig movement with its bound input, never head motion or merely waiting.
        ObserveMovement(move.magnitude > 0.35f, Vector3.ProjectOnPlane(position - previousOrigin, Vector3.up).magnitude);
        if (Mathf.Abs(turn.x) > 0.35f) lastTurnInput = Time.unscaledTime;
        // The locomotion provider applies a snap after this component's early Update.
        ObserveTurn(Time.unscaledTime - lastTurnInput < 0.2f, Quaternion.Angle(previousOriginRotation, rotation));
        previousOrigin = position; previousOriginRotation = rotation;
    }
    internal void ObserveMovement(bool correctInput, float distance)
    {
        if (stage != Stage.Move || !correctInput || distance > 0.3f) return;
        movementDistance += distance;
        if (movementDistance >= 0.2f) SetStage(Stage.Turn);
    }
    internal void ObserveTurn(bool correctInput, float degrees)
    {
        if (stage != Stage.Turn || !correctInput) return;
        turnDegrees += degrees;
        if (turnDegrees >= 15f) SetStage(Stage.Grab);
    }
    void CreatePracticeObject()
    {
        if (practiceObject != null) return;
        var forward = Vector3.ProjectOnPlane(playerCamera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward);
        practiceHome = playerCamera.transform.position + forward * 0.65f - right * 0.18f + Vector3.down * 0.4f;
        var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = "Tutorial Training Block";
        obj.transform.SetPositionAndRotation(practiceHome, Quaternion.LookRotation(forward));
        obj.transform.localScale = new Vector3(0.1f, 0.08f, 0.16f);
        obj.GetComponent<Renderer>().sharedMaterial = view.BlockMaterial;
        var stripe = GameObject.CreatePrimitive(PrimitiveType.Cube);
        stripe.name = "Training block front marker"; stripe.transform.SetParent(obj.transform, false);
        stripe.transform.localPosition = new Vector3(0, 0.53f, 0.3f);
        stripe.transform.localScale = new Vector3(0.75f, 0.06f, 0.18f);
        stripe.GetComponent<Collider>().enabled = false;
        Destroy(stripe.GetComponent<Collider>());
        var body = obj.AddComponent<Rigidbody>();
        body.isKinematic = true; body.useGravity = false;
        practiceObject = obj.AddComponent<XRGrabInteractable>();
        practiceObject.movementType = XRBaseInteractable.MovementType.Kinematic;
        practiceObject.throwOnDetach = false;
        practiceObject.interactionLayers = ~0;
        practiceObject.selectMode = InteractableSelectMode.Single;
        practiceObject.selectEntered.AddListener(OnPracticeGrab);
        practiceObject.selectExited.AddListener(OnPracticeRelease);
        // Deliberately no PC keychain: this object can never satisfy an assembly socket.
        practiceTarget = view.CreatePracticeTarget(practiceHome + right * 0.35f, Quaternion.LookRotation(forward));
        view.SetPracticeObject(practiceObject.transform);
    }
    void OnPracticeGrab(SelectEnterEventArgs args)
    {
        if (args.interactorObject is XRSocketInteractor || stage < Stage.Grab || stage > Stage.Place) return;
        holdingHand = args.interactorObject;
        gripArmed = IsGripHeld(holdingHand.transform);
        waitingForRelease = false;
        if (stage == Stage.Grab && gripArmed) SetStage(Stage.Manipulate);
        else if (stage == Stage.Manipulate) ResetManipulationBaseline();
    }
    void OnPracticeRelease(SelectExitEventArgs args)
    {
        if (args.interactorObject is XRSocketInteractor) return;
        if (args.isCanceled) { holdingHand = null; waitingForRelease = false; return; }
        if (stage == Stage.Place && gripArmed)
        {
            // Release events fire while the controller action is already up.
            waitingForRelease = !IsGripHeld(args.interactorObject.transform);
            releaseTime = Time.unscaledTime + 0.05f;
        }
        holdingHand = null;
        if (stage == Stage.Manipulate) Feedback = "Grab the block again and keep holding the grip while you practise.";
    }
    void ObserveManipulation()
    {
        if (practiceObject == null || holdingHand == null || !gripArmed) return;
        var attach = holdingHand.transform.GetComponent<NearFarInteractor>()?.interactionAttachController as InteractionAttachController;
        var side = IsLeft(holdingHand.transform) ? "Left" : "Right";
        var input = attach != null && attach.useManipulationInput ? attach.manipulationInput.ReadValue() : ReadVector($"XRI {side} Interaction/Manipulation");
        float distance = Vector3.Distance(manipulationPosition, practiceObject.transform.position);
        float angle = Quaternion.Angle(manipulationRotation, practiceObject.transform.rotation);
        // Bind progress to actual movement of the selected object with manipulation input.
        if (Mathf.Abs(input.x) > 0.3f) rotatedObjectDegrees += angle;
        if (Mathf.Abs(input.y) > 0.3f) movedObjectDistance += distance;
        rotatedWithStick |= rotatedObjectDegrees >= 20f;
        translatedWithStick |= movedObjectDistance >= 0.06f;
        manipulationPosition = practiceObject.transform.position;
        manipulationRotation = practiceObject.transform.rotation;
        if (rotatedWithStick && translatedWithStick) SetStage(Stage.Place);
    }
    void CheckPracticePlacement()
    {
        if (stage != Stage.Place || practiceObject == null || practiceTarget == null || practiceObject.isSelected) return;
        if (Vector3.Distance(practiceObject.transform.position, practiceTarget.position) <= 0.13f &&
            Quaternion.Angle(practiceObject.transform.rotation, practiceTarget.rotation) <= 30f)
        {
            practiceObject.transform.SetPositionAndRotation(practiceTarget.position, practiceTarget.rotation);
            SetStage(Stage.ControlsComplete);
        }
        else
        {
            Feedback = "Try again: line up the block with the glowing frame, then release the grip.";
            PlayTone(false);
        }
    }
    internal void ResetPracticeObject()
    {
        if (practiceObject == null || practiceObject.isSelected) return;
        practiceObject.transform.SetPositionAndRotation(practiceHome, practiceTarget != null ? practiceTarget.rotation : Quaternion.identity);
        Feedback = "The training block is back. Grab it with either grip.";
    }
    void StartAssembly()
    {
        if (startedAssembly || stage >= Stage.Assembly) return;
        startedAssembly = true;
        SetStage(Stage.Assembly);
        RestoreBodies();
        if (practiceObject != null)
        {
            var owners = new List<IXRSelectInteractor>(practiceObject.interactorsSelecting);
            foreach (var owner in owners) practiceObject.interactionManager.SelectExit(owner, practiceObject);
            practiceObject.gameObject.SetActive(false);
            Destroy(practiceObject.gameObject);
        }
        practiceObject = null; holdingHand = null; waitingForRelease = false;
        view?.EndControls();
        assemblyStart?.Invoke(); // The original simulation-start callback is preserved and invoked once.
        var state = TechWiseSimulationRuntime.Instance;
        // The authored welcome button activates initially dormant component groups.
        // Register those parts only after the preserved start actions have run.
        state.Refresh();
        foreach (var part in state.Parts)
        {
            if (part == null) continue;
            listenedParts.Add(part); part.selectExited.AddListener(OnAssemblyReleased);
        }
        foreach (var socket in state.Sockets)
            if (socket != null) previewStates[socket] = socket.showInteractableHoverMeshes;
        RefreshAssemblyGuide();
    }
    void OnAssemblyReleased(SelectExitEventArgs args)
    {
        if (stage != Stage.Assembly || args.isCanceled || args.interactorObject is XRSocketInteractor) return;
        StartCoroutine(CheckReleasedPart(args.interactableObject as XRGrabInteractable));
    }
    IEnumerator CheckReleasedPart(XRGrabInteractable part)
    {
        yield return new WaitForSecondsRealtime(0.25f);
        if (!InTutorial || stage != Stage.Assembly || part == null) yield break;
        var state=TechWiseSimulationRuntime.Instance;
        if (part.TryGetComponent<TechWiseRamInsertion>(out var ram) && ram.Staged) { Feedback=ram.Guidance; yield break; }
        if (!TechWiseSimulationModeManager.IsDisassembly && !state.IsPartInstalled(part))
        {
            var target=state.Sockets.Find(s=>s!=null && !s.hasSelection && TechWiseSimulationRuntime.Matches(s,part.transform));
            if(target!=null) { state.PlacementProblem(target,part,out _,out var correction); Feedback=correction; PlayTone(false); }
        }
    }
    void OnAssemblyStateChanged()
    {
        if (initialized && InTutorial && stage == Stage.Assembly) RefreshAssemblyGuide();
    }
    void OnMistake(TechWiseVrMistakeDetail mistake)
    {
        if (!initialized || !InTutorial || stage != Stage.Assembly) return;
        Feedback = mistake.correction; PlayTone(false);
    }
    void RefreshAssemblyGuide()
    {
        var state = TechWiseSimulationRuntime.Instance;
        if (state == null) return;
        if (state.ConfigurationError != null)
        { Heading = "Tutorial paused"; Instructions = "The workbench is not ready. Return to the menu and open Tutorial Mode again."; return; }
        if (TechWiseDetailedAssemblyRuntime.Active)
        {
            var phase = TechWiseDetailedAssemblyRuntime.Instance;
            if (TechWiseSimulationModeManager.IsDisassembly ? phase.RemovalComplete : phase.BuildComplete)
            { SetStage(Stage.Finished); view?.ClearMarkers(); return; }
            Heading = phase.Heading; Instructions = phase.Instruction;
            if (previousStep != phase.Heading) { PlayTone(true); previousStep = phase.Heading; Feedback = ""; }
            var next = state.Parts.Find(p => p != null && state.StepOf(p) == phase.CurrentPartStep && (TechWiseSimulationModeManager.IsDisassembly ? state.IsPartInstalled(p) : !state.IsPartInstalled(p)));
            var socket = next == null ? null : state.Sockets.Find(s => s != null && !s.hasSelection && state.StepOf(s) == phase.CurrentPartStep && TechWiseSimulationRuntime.Matches(s, next.transform));
            bool valid = next != null && socket != null && state.PlacementProblem(socket, next, out _, out _) == null;
            if (next != null && TechWiseSimulationRuntime.IsHeld(next) && socket != null)
                Feedback = state.PlacementProblem(socket, next, out _, out var correction) == null ? "Aligned. Release the grip to seat the component." : correction;
            if (!TechWiseSimulationModeManager.IsDisassembly && next != null && next.TryGetComponent<TechWiseRamInsertion>(out var ram) && (ram.Staged || valid))
                Feedback = ram.Guidance;
            foreach (var s in state.Sockets) if (s != null) s.showInteractableHoverMeshes = s == socket;
            if (phase.CurrentFastener != null)
            {
                var screw=phase.CurrentFastener;
                view?.MarkAssembly(screw.Inserted?null:screw.transform,screw.Hole.transform,screw.Inserted);
            }
            else if(phase.GuideActionFocus(out var actionItem,out var actionTarget)) view?.MarkAssembly(actionItem,actionTarget,false);
            else if (view != null) view.MarkAssembly(next != null ? next.transform : null, socket != null ? socket.GetAttachTransform(next) : null, valid);
            return;
        }
        var current = state.CurrentStep;
        foreach (var id in TechWiseSimulationModeManager.AssemblyOrder)
        {
            if (!state.IsStepComplete(id) || state.Parts.Any(p => p != null && state.StepOf(p) == id && TechWiseSimulationRuntime.IsHeld(p))) continue;
            if (acknowledgedSteps.Add(id)) PlayTone(true);
        }
        if (current == null && acknowledgedSteps.Count == TechWiseSimulationModeManager.AssemblyOrder.Length)
        {
            SetStage(Stage.Finished);
            view?.ClearMarkers();
            return;
        }
        if (previousStep != current) { Feedback = ""; previousStep = current; }
        int index = Array.IndexOf(TechWiseSimulationModeManager.AssemblyOrder, current);
        Heading = $"PC Assembly  |  Step {index + 1} of 8";
        Instructions = TechWiseGuideAssistant.TutorialInstruction(current);
        var part = state.Parts.Find(p => p != null && state.StepOf(p) == current && !state.IsPartInstalled(p));
        if (part != null && !part.gameObject.activeSelf)
        {
            // Preserve the existing tray layout: reveal the next loose part after
            // the previous one is installed, including each of the four RAM sticks.
            part.gameObject.SetActive(true);
            TechWiseComponentRecovery.TrackTutorialPart(part);
        }
        var target = part == null ? null : state.Sockets.Find(s => s != null && s.isActiveAndEnabled && !s.hasSelection && state.StepOf(s) == current && TechWiseSimulationRuntime.Matches(s, part.transform));
        foreach (var pair in previewStates)
            if (pair.Key != null) pair.Key.showInteractableHoverMeshes = pair.Key == target;
        var held = state.Parts.Find(p => p != null && TechWiseSimulationRuntime.IsHeld(p));
        bool ready = false;
        if (held != null)
        {
            if (state.StepOf(held) != current) Feedback = $"Put this part down. First install the {TechWiseSimulationRuntime.Label(current)}.";
            else if (target != null)
            {
                var problem = state.PlacementProblem(target, held, out _, out var correction);
                ready = problem == null;
                Feedback = ready ? "Aligned. Release the grip to install the part." : correction;
            }
        }
        else if (string.IsNullOrEmpty(Feedback)) Feedback = $"Hold {Binding("XRI Right Interaction/Select", "either grip")} to grab the highlighted part.";
        view?.MarkAssembly(part != null ? part.transform : null, target != null ? target.GetAttachTransform(part) : null, ready);
    }
    void SetStageText()
    {
        var grip = Binding("XRI Right Interaction/Select", "right grip");
        switch (stage)
        {
            case Stage.Welcome:
                Heading = "Welcome to Tutorial Mode";
                Instructions = "First learn the controllers. Then build a PC with step-by-step guidance.\nPress Let's Get Started to begin."; break;
            case Stage.Point:
                Heading = "Controls 1 / 6  |  Point and click";
                Instructions = $"Aim either controller ray at the glowing button.\nPress and release {Binding("XRI Right Interaction/UI Press", "right trigger")} or {Binding("XRI Left Interaction/UI Press", "left trigger")}."; break;
            case Stage.Move:
                Heading = "Controls 2 / 6  |  Move";
                Instructions = $"Push {Binding("XRI Left Locomotion/Move", "left joystick")} to move a short distance.\nYou can stay physically in place."; break;
            case Stage.Turn:
                Heading = "Controls 3 / 6  |  Turn";
                Instructions = $"Push {Binding("XRI Right Locomotion/Turn", "right joystick")} left or right to turn smoothly. Release to stop.\nThen return your view to the lesson."; break;
            case Stage.Grab:
                Heading = "Controls 4 / 6  |  Grab";
                Instructions = $"Point at or reach toward the blue training block.\nSqueeze and HOLD {grip} or {Binding("XRI Left Interaction/Select", "left grip")}."; break;
            case Stage.Manipulate:
                Heading = "Controls 5 / 6  |  Rotate and reposition";
                Instructions = "Keep holding the grip. Turn your wrist to orient the block.\nUse the holding hand's joystick: left/right rotates; forward/back changes distance.\n" +
                    (rotatedWithStick ? "[done] Joystick rotation   " : "[ ] Joystick rotation   ") +
                    (translatedWithStick ? "[done] Reposition" : "[ ] Reposition"); break;
            case Stage.Place:
                Heading = "Controls 6 / 6  |  Place and release";
                Instructions = "Match the block to the glowing frame, with the white stripe toward the arrow.\nRelease the grip when the frame turns green.\nA loose or misaligned release lets you try again."; break;
            case Stage.ControlsComplete:
                Heading = "Controls complete!";
                Instructions = "You can point, move, turn, grab, rotate and place.\nYour guided PC lesson starts next."; break;
            case Stage.Finished:
                Heading = "Tutorial complete!";
                Instructions = (TechWiseSimulationModeManager.IsDisassembly ? "All components and 36 individual screws removed." : "All components, 36 individual screws, CPU locks, paste and case panel completed.") + "\nUse Menu to return home or start another activity.";
                Feedback = "Well done. You are ready for Practice Mode."; break;
        }
        if (stage==Stage.Point || stage==Stage.ControlsComplete)
            Instructions += "\nPanels: aim at their title and hold "+TechWiseControlLabels.Grab+" to reposition; release to leave them there. Aim at text and use "+TechWiseControlLabels.Scroll+" to scroll. UI buttons use "+TechWiseControlLabels.Ui+".";
        view?.Refresh(Heading, Instructions, Feedback, stage);
    }
    void BindActions()
    {
        actions.Clear();
        foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
            foreach (var map in asset.actionMaps)
                foreach (var action in map.actions)
                {
                    var key = map.name + "/" + action.name;
                    if (!actions.ContainsKey(key) || action.enabled) actions[key] = action;
                }
    }
    internal string Binding(string key, string fallback)
    {
        int split=key.LastIndexOf('/');
        return TechWiseControlLabels.Binding(key.Substring(0,split),key.Substring(split+1));
    }
    Vector2 ReadVector(string key) => actions.TryGetValue(key, out var a) && a.enabled ? a.ReadValue<Vector2>() : Vector2.zero;
    bool IsGripHeld(Transform hand)
    {
        string key = "XRI " + (IsLeft(hand) ? "Left" : "Right") + " Interaction/Select";
        if (actions.TryGetValue(key, out var action) && action.enabled && action.IsPressed()) return true;
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(IsLeft(hand) ? XRNode.LeftHand : XRNode.RightHand);
        return device.TryGetFeatureValue(CommonUsages.gripButton, out var pressed) && pressed;
    }
    internal bool TriggerHeld(Transform hand)
    {
        string key = "XRI " + (IsLeft(hand) ? "Left" : "Right") + " Interaction/UI Press";
        if (actions.TryGetValue(key, out var action) && action.enabled && action.IsPressed()) return true;
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(IsLeft(hand) ? XRNode.LeftHand : XRNode.RightHand);
        return device.TryGetFeatureValue(CommonUsages.triggerButton, out var pressed) && pressed ||
            device.TryGetFeatureValue(CommonUsages.trigger, out var value) && value > 0.45f;
    }
    internal static bool IsLeft(Transform hand)
    {
        for (var t = hand; t != null; t = t.parent)
            if (t.name.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
    void SetupAudio()
    {
        if (audioSource == null) { audioSource = gameObject.AddComponent<AudioSource>(); audioSource.playOnAwake = false; audioSource.spatialBlend = 0; audioSource.volume = 0.18f; }
        if (successTone == null) successTone = MakeTone("Tutorial success", 660, 880);
        if (errorTone == null) errorTone = MakeTone("Tutorial retry", 240, 180);
    }
    static AudioClip MakeTone(string name, float start, float end)
    {
        const int rate = 22050, samples = 5512;
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / rate, progress = (float)i / samples;
            data[i] = Mathf.Sin(2 * Mathf.PI * (start * t + (end - start) * t * progress * 0.5f)) * Mathf.Sin(Mathf.PI * progress) * 0.5f;
        }
        var clip = AudioClip.Create(name, samples, 1, rate, false); clip.SetData(data, 0); return clip;
    }
    void PlayTone(bool success)
    {
        if (audioSource == null) return;
        if (!success && Time.unscaledTime - lastErrorSound < 1.5f) return;
        if (!success) lastErrorSound = Time.unscaledTime;
        audioSource.PlayOneShot(success ? successTone : errorTone);
    }
}

/// <summary>The lesson target only accepts a tracked pointer pressed with its trigger.</summary>
public sealed class TechWiseTutorialTriggerTarget : MonoBehaviour, IPointerDownHandler, IPointerClickHandler, IPointerExitHandler
{
    internal TechWiseTutorialRuntime lesson;
    int pressedPointer = int.MinValue;
    public void OnPointerDown(PointerEventData data)
    {
        pressedPointer = int.MinValue;
        if (data is TrackedDeviceEventData tracked && tracked.interactor is Component component && lesson.TriggerHeld(component.transform))
            pressedPointer = data.pointerId;
    }
    public void OnPointerClick(PointerEventData data)
    {
        lesson.ConfirmPointer(data.button == PointerEventData.InputButton.Left && data.pointerId == pressedPointer);
        pressedPointer = int.MinValue;
    }
    public void OnPointerExit(PointerEventData data) => pressedPointer = int.MinValue;
}
