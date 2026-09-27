using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Content.Interaction;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Filtering;

/// <summary>Additive Phase 1 mechanics. Existing keyed XRI sockets remain the only placement/ownership system.</summary>
[DefaultExecutionOrder(-9300)]
public sealed partial class TechWiseDetailedAssemblyRuntime : MonoBehaviour
{
    public enum Phase { PrepareMotherboard, OpenSocket, InstallCPU, CloseAndLockSocket, ApplyThermalPaste, InstallCooler, TightenCooler, InstallRAM, InsertAndLowerM2, TightenM2, PrepareCase, PlaceMotherboard, TightenMotherboard, InstallRearFan, InstallFrontFans, ValidateAirflow, InstallGpuConnector, InstallGpu, InstallStorage, InstallPsu, CloseCase, Complete }
    public static TechWiseDetailedAssemblyRuntime Instance { get; private set; }
    public static bool Active => Instance != null && Instance.assets != null &&
        (!TechWiseSimulationModeManager.IsTutorialMode || SceneManager.GetActiveScene().name == "Singleplayer") &&
        (SceneManager.GetActiveScene().name == "Singleplayer" || SceneManager.GetActiveScene().name == "Multiplayer");
    public bool Ready { get; private set; }
    public bool PasteApplied { get; private set; }
    public bool CasePrepared { get; private set; }
    public bool M2Lowered { get; private set; }
    public string SetupError { get; private set; }
    public string Feedback => Ready && CurrentPhase == Phase.InstallCooler ? "Thermal paste applied. The cooler is ready to install." : "";
    public TechWiseAssemblyHinge Cover { get; private set; }
    public TechWiseAssemblyHinge Lever { get; private set; }
    public TechWiseAssemblyHinge M2Hinge { get; private set; }
    public readonly List<TechWiseFastener> CoolerScrews = new(), BoardScrews = new(), M2Screws = new();
    TechWisePhaseOneAssets assets;
    TechWiseSimulationRuntime state;
    Transform board, casing;
    GameObject additions, shell, blob;
    XRGrabInteractable panel;
    Vector3 panelHome;
    bool panelTouched, settingUp, m2HandleMade;
    float pasteProgress;
    TechWiseAssemblyHistory.Snapshot pasteBefore;
    LineRenderer m2InsertionGuide;
    Material insertionMaterial;
    readonly Dictionary<string, Transform> targets = new();
    readonly List<GameObject> owned = new();
    readonly List<Key> fanKeys = new();
    readonly List<TMP_Text> labels = new();
    readonly Dictionary<TMP_Text, Func<bool>> labelConditions = new();
    readonly HashSet<GameObject> retiredPaste = new();
    internal bool IsRetiredPaste(Transform item)
    {
        for (; item != null; item = item.parent) if (retiredPaste.Contains(item.gameObject)) return true;
        return false;
    }
    readonly List<Action> restore = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (Instance != null) return;
        var root = new GameObject("TechWise Detailed Assembly Phase 1"); DontDestroyOnLoad(root); root.AddComponent<TechWiseDetailedAssemblyRuntime>();
    }
    void Awake() { Instance = this; assets = Resources.Load<TechWisePhaseOneAssets>("TechWisePhaseOneAssets"); }
    void OnEnable() { SceneManager.sceneLoaded += SceneLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= SceneLoaded; Clear(); if (Instance == this) Instance = null; }
    void OnApplicationQuit() => Clear();
    void SceneLoaded(Scene scene, LoadSceneMode mode) { if (scene == SceneManager.GetActiveScene()) Clear(); }
    void Clear()
    {
        foreach (var item in owned)
            if (item != null)
                foreach (var interactable in item.GetComponentsInChildren<XRBaseInteractable>(true))
                    if (interactable != null) interactable.enabled = false;
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
        if (insertionMaterial != null) Destroy(insertionMaterial);
        insertionMaterial = null; m2InsertionGuide = null;
        foreach (var item in owned)
        {
            if (item == null) continue;
            Destroy(item);
        }
        foreach (var item in fanKeys) if (item != null) Destroy(item);
        owned.Clear(); fanKeys.Clear(); targets.Clear(); labels.Clear(); labelConditions.Clear(); retiredPaste.Clear();
        ClearCompleteBuild();
        CoolerScrews.Clear(); BoardScrews.Clear(); M2Screws.Clear();
        Ready = PasteApplied = CasePrepared = M2Lowered = panelTouched = m2HandleMade = false;
        Cover = Lever = M2Hinge = null; board = casing = null; additions = shell = blob = null; panel = null; SetupError = null; pasteProgress = 0;
    }
    void Update()
    {
        if (!Active) { if (owned.Count > 0) Clear(); return; }
        if (!Ready) { if (SetupError == null) TrySetup(); return; }
        if (TechWisePauseSession.Active || state.ManipulationLocked || TechWiseTutorialRuntime.ControlsPending) return;
        RefreshFasteners();
        UpdateCompleteBuild();
        if (Seated("M2") && !m2HandleMade) CreateM2Handle();
        if (panel != null && panelTouched && !panel.isSelected && Vector3.Distance(panel.transform.position, panelHome) > .2f) CasePrepared = true;
        foreach (var tool in TechWiseAssemblyTool.Tools)
            if (tool != null && tool.kind == TechWiseAssemblyTool.ToolKind.ThermalPaste && tool.tip != null)
                ApplyPaste(tool.tip.position, tool.tip.forward, tool.TriggerPressed, Time.deltaTime);
        RevealCurrentTutorialPart();
    }
    void LateUpdate()
    {
        DrawM2InsertionGuide();
        var camera = Camera.main; if (camera == null) return;
        foreach (var label in labels)
        {
            if (label == null) continue;
            bool visible = Ready && !TechWiseSimulationModeManager.IsCompetitionMode && !TechWiseTutorialRuntime.ControlsPending &&
                (!labelConditions.TryGetValue(label, out var condition) || condition());
            label.gameObject.SetActive(visible);
            if (!visible) continue;
            // Keep callouts upright and beside the part instead of rotating into the CPU.
            label.transform.position = label.transform.parent.position + Vector3.up * .13f + camera.transform.right * .16f;
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - camera.transform.position, Vector3.up);
        }
    }
    void TrySetup()
    {
        if (settingUp) return;
        state = TechWiseSimulationRuntime.Instance;
        if (state == null || state.FindPart("Motherboard") == null || state.FindSocket("CPU") == null) return;
        settingUp = true;
        try
        {
            board = state.FindPart("Motherboard").transform;
            casing = state.FindSocket("Motherboard").transform.parent;
            additions = Own(new GameObject("Detailed assembly tools and hardware"));
            RegisterGpuConnector();
            foreach (var socket in state.Sockets)
            {
                if (socket == null) continue;
                var rule = socket.GetComponent<TechWisePlacementRule>();
                if (rule == null) { rule = socket.gameObject.AddComponent<TechWisePlacementRule>(); var added = rule; restore.Add(() => { if (added != null) Destroy(added); }); }
                else { var saved = JsonUtility.ToJson(rule); var original = rule; restore.Add(() => { if (original != null) JsonUtility.FromJsonOverwrite(saved, original); }); }
                rule.exactOrientation = true; rule.distanceTolerance = state.StepOf(socket) == "Motherboard" ? .05f : .025f;
                rule.orientationTolerance = state.StepOf(socket) == "CPU" ? 8 : state.StepOf(socket) == "M2" ? 20 : 12;
                if (state.StepOf(socket) == "M2") rule.distanceTolerance = .045f;
                rule.fastenAfterPlacement = true;
            }
            // Scale and orient the retained loose models using their original assembled source poses.
            foreach (var pose in assets.boardParts)
            {
                var part = state.Parts.Find(p => p != null && (pose.id.StartsWith("RAM") ? p.name == pose.id : state.StepOf(p) == pose.id));
                if (part == null) throw new InvalidOperationException("Phase 1 component missing: " + pose.id);
                var savedScale = part.transform.localScale; var originalPart = part;
                restore.Add(() => { if (originalPart != null) originalPart.transform.localScale = savedScale; });
                SetWorldScale(part.transform, Vector3.Scale(board.lossyScale, pose.scale));
                if (!pose.id.StartsWith("RAM")) SetTarget(pose.id, state.FindSocket(pose.id), part, board, pose);
            }
            var ramSockets = state.Sockets.Where(s => state.StepOf(s) == "RAM").OrderBy(s => s.name).ToArray();
            var ramPoses = assets.boardParts.Where(p => p.id.StartsWith("RAM")).OrderBy(p => p.id).ToArray();
            for (int i = 0; i < ramSockets.Length; i++)
                SetTarget("RAM" + i, ramSockets[i], state.Parts.First(p => state.StepOf(p) == "RAM"), board, ramPoses[i]);
            SetTarget("Motherboard", state.FindSocket("Motherboard"), state.FindPart("Motherboard"), casing, assets.boardInCase);
            NarrowBoardCollider();
            CreateRetention();
            // Retire only the old paste-trigger demo within this phase; its authored objects are restored on exit.
            foreach (var legacy in FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (legacy.gameObject.scene != board.gameObject.scene ||
                    !(legacy.name.StartsWith("Thermal Paste", StringComparison.OrdinalIgnoreCase) || legacy.name.StartsWith("Tooth_Paste", StringComparison.OrdinalIgnoreCase))) continue;
                bool active = legacy.gameObject.activeSelf;
                retiredPaste.Add(legacy.gameObject);
                restore.Add(() => { if (legacy != null) legacy.gameObject.SetActive(active); });
                legacy.gameObject.SetActive(false);
            }
            foreach (var socket in state.Sockets)
            {
                if (socket == null) continue;
                if (state.StepOf(socket) != null || !socket.name.Contains("Thermal Paste")) continue;
                bool enabled = socket.enabled; restore.Add(() => { if (socket != null) socket.enabled = enabled; }); socket.enabled = false;
            }
            shell = Own(Instantiate(assets.officeShell, casing, false)); shell.name = "Office PC enclosure";
            CreateSidePanel();
            CreateFans();
            CreateTools();
            CreateScrews();
            CreateAdditionalFasteners();
            InitializeManualScrews();
            SetM2Angle(25);
            Ready = true;
            InitializePanelAndWorkspace();
            state.Refresh(); // Register the supplemental keyed fans through the existing simulation registry.
            UpdateAvailability();
            additions.AddComponent<TechWiseAssemblyPresentation>().Initialize(state);
            additions.AddComponent<TechWiseScrewDetail>();
            Debug.Log("[Phase 1] Ready: source-model poses, " + assets.motherboardHoles.Length + " PCB mounting holes, four cooler screws, M.2 screw and three fan mounts.");
        }
        catch (Exception error) { SetupError = error.Message; Debug.LogError("[Phase 1] Setup failed: " + error); }
        finally { settingUp = false; }
    }
    GameObject Own(GameObject obj) { owned.Add(obj); return obj; }
    void RegisterGpuConnector()
    {
        var part = FindObjectsByType<XRGrabInteractable>(FindObjectsInactive.Include).FirstOrDefault(p => p.name.Trim() == "GPUConnect" && !TechWiseSimulationModeManager.IsKnowledgeDisplay(p.transform));
        // Empty-key legacy demo sockets also accept this keychain. Choose the authored
        // GPU bracket explicitly rather than depending on Unity's object discovery order.
        var socket = part == null ? null : state.Sockets.Find(s => s != null && s.name == "GPU_Bracket1" && TechWiseSimulationRuntime.Matches(s, part.transform));
        if (part == null || socket == null) return;
        foreach (var obj in new[] { part.gameObject, socket.gameObject })
        {
            var id = obj.GetComponent<TechWiseAssemblyPartId>();
            if (id != null) continue;
            id = obj.AddComponent<TechWiseAssemblyPartId>(); id.step = "GPUConnector";
            var added = id; restore.Add(() => { if (added != null) Destroy(added); });
        }
    }
    static void SetWorldScale(Transform t, Vector3 scale)
    {
        var parent = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(scale.x / parent.x, scale.y / parent.y, scale.z / parent.z);
    }
    void SetTarget(string id, XRLockSocketInteractor socket, XRGrabInteractable part, Transform space, TechWisePhaseOneAssets.ModelPose pose)
    {
        var oldAttach = socket.attachTransform; var oldScaleMode = socket.socketScaleMode;
        restore.Add(() => { if (socket != null) { socket.attachTransform = oldAttach; socket.socketScaleMode = oldScaleMode; } });
        var obj = Own(new GameObject("Phase 1 " + id + " exact placement")); obj.transform.SetParent(space, false);
        var localAttach = part.GetAttachTransform(socket);
        var localPosition = part.transform.InverseTransformPoint(localAttach.position);
        var localRotation = Quaternion.Inverse(part.transform.rotation) * localAttach.rotation;
        var rootRotation = space.rotation * pose.rotation;
        obj.transform.position = space.TransformPoint(pose.position) + rootRotation * Vector3.Scale(localPosition, part.transform.lossyScale);
        obj.transform.rotation = rootRotation * localRotation;
        socket.attachTransform = obj.transform; socket.socketScaleMode = SocketScaleMode.None;
        targets[id] = obj.transform;
    }
    void NarrowBoardCollider()
    {
        var pcb = board.Find("pCube511"); if (pcb == null) return;
        var grab = state.FindPart("Motherboard"); var collider = grab.colliders.OfType<BoxCollider>().FirstOrDefault();
        if (collider == null) return;
        var oldCentre = collider.center; var oldSize = collider.size;
        restore.Add(() => { if (collider != null) { collider.center = oldCentre; collider.size = oldSize; } });
        var b = pcb.GetComponent<MeshFilter>().sharedMesh.bounds;
        collider.center = board.InverseTransformPoint(pcb.TransformPoint(b.center));
        collider.size = new Vector3(b.size.x * pcb.localScale.x, b.size.y * pcb.localScale.y, Mathf.Max(b.size.z * pcb.localScale.z, .0008f));
    }
    void CreateRetention()
    {
        var cover = board.Find("CPU Case") ?? board.Find("pCube329") ?? board.Find("pCube329 (Case)");
        var lever = board.Find("Retention arm") ?? board.Find("nurbsToPoly1");
        if (cover == null || lever == null) throw new InvalidOperationException("The retained motherboard needs its CPU cover and retention arm meshes.");
        Cover = MakeHinge(cover, "CPU retention cover", assets.coverHinge, 95, () => TechWiseSimulationModeManager.IsDisassembly ? !Seated("CPUCooler") && !Lever.Closed : Seated("CPU") && !PasteApplied);
        Lever = MakeHinge(lever, "CPU locking arm", assets.coverHinge, 75, () => TechWiseSimulationModeManager.IsDisassembly ? !Seated("CPUCooler") : Cover.Closed && Seated("CPU") && !PasteApplied);
        Label(Cover.transform, "CPU COVER\nLower, then release", .04f, () => CurrentPhase == Phase.CloseAndLockSocket && !Cover.Closed);
        Label(Lever.transform, "LOCKING ARM\nLower, then release", .065f, () => CurrentPhase == Phase.CloseAndLockSocket && Cover.Closed && !Lever.Closed);
    }
    TechWiseAssemblyHinge MakeHinge(Transform mesh, string name, Vector3 pivot, float openAngle, Func<bool> allowed)
    {
        foreach (var previous in mesh.GetComponentsInChildren<XRBaseInteractable>(true))
        { bool wasEnabled = previous.enabled; restore.Add(() => { if (previous != null) previous.enabled = wasEnabled; }); previous.enabled = false; }
        var oldParent = mesh.parent; var oldPosition = mesh.localPosition; var oldRotation = mesh.localRotation; var oldScale = mesh.localScale;
        restore.Add(() => { if (mesh != null && oldParent != null) { mesh.SetParent(oldParent, false); mesh.localPosition = oldPosition; mesh.localRotation = oldRotation; mesh.localScale = oldScale; } });
        var obj = Own(new GameObject(name)); obj.SetActive(false); obj.transform.SetParent(board, false); obj.transform.localPosition = pivot;
        mesh.SetParent(obj.transform, true);
        var interactable = obj.AddComponent<XRSimpleInteractable>();
        foreach (var oldCollider in mesh.GetComponentsInChildren<Collider>()) { var wasEnabled = oldCollider.enabled; restore.Add(() => { if (oldCollider != null) oldCollider.enabled = wasEnabled; }); oldCollider.enabled = false; }
        var filter = mesh.GetComponent<MeshFilter>(); var collider = mesh.gameObject.AddComponent<BoxCollider>(); collider.center = filter.sharedMesh.bounds.center; collider.size = filter.sharedMesh.bounds.size;
        restore.Add(() => { if (collider != null) Destroy(collider); });
        interactable.colliders.Clear(); interactable.colliders.Add(collider); interactable.interactionLayers = ~0;
        var hinge = obj.AddComponent<TechWiseAssemblyHinge>(); hinge.movingPart = obj.transform; hinge.localAxis = Vector3.left;
        hinge.useLoweringGesture = true; hinge.maximumAngle = openAngle; hinge.closeTolerance = 15;
        hinge.permitted = () => ControlsAllowed && allowed(); hinge.Initialize(openAngle);
        interactable.selectFilters.Add(new XRSelectFilterDelegate((_, __) => (TechWiseSimulationModeManager.IsDisassembly || !hinge.Closed) && ControlsAllowed && allowed()));
        obj.SetActive(true);
        return hinge;
    }
    bool ControlsAllowed => Ready && !TechWisePauseSession.Active && !state.ManipulationLocked && !TechWiseTutorialRuntime.ControlsPending;
    void CreateSidePanel()
    {
        var side = shell.transform.Find("Removable side panel");
        panel = AddGrab(side.gameObject, false); panelHome = side.position;
        panel.selectEntered.AddListener(_ => panelTouched = true);
        var hint = Label(side, "SIDE PANEL\nGrip to remove", .07f, () => CurrentPhase == Phase.PrepareCase);
        hint.transform.position = side.GetComponent<Renderer>().bounds.center + Vector3.up * .1f;
    }
    void CreateFans()
    {
        for (int i = 0; i < 3; i++)
        {
            string id = i == 0 ? "FanRear" : "FanFront" + i;
            var mount = shell.transform.Find(i == 0 ? "Rear fan mount" : "Front fan mount " + i);
            var obj = Own(Instantiate(assets.fan)); obj.name = i == 0 ? "Rear exhaust fan" : "Front intake fan " + i;
            // Loose fan positions are a workbench layout; installed poses come directly from the generated grille anchors.
            obj.transform.position = board.position + Vector3.right * (.46f + i * .16f) + Vector3.up * .04f;
            obj.transform.rotation = Quaternion.Euler(90, 0, 0);
            var part = AddGrab(obj, false); part.gameObject.AddComponent<TechWiseAssemblyPartId>().step = id;
            var key = ScriptableObject.CreateInstance<Key>(); key.name = id; fanKeys.Add(key); obj.AddComponent<Keychain>().AddKey(key);
            mount.gameObject.SetActive(false);
            var volume = mount.gameObject.AddComponent<SphereCollider>(); volume.isTrigger = true; volume.radius = .035f / Mathf.Abs(casing.lossyScale.x);
            var target = mount.gameObject.AddComponent<XRLockSocketInteractor>(); target.interactionLayers = ~0; target.socketScaleMode = SocketScaleMode.None;
            mount.gameObject.AddComponent<TechWiseAssemblyPartId>().step = id;
            target.keychainLock.requiredKeys.Add(key); target.attachTransform = mount; target.hoverSocketSnapping = false;
            var rule = mount.gameObject.AddComponent<TechWisePlacementRule>(); rule.exactOrientation = true; rule.distanceTolerance = .035f; rule.orientationTolerance = 10; rule.fastenAfterPlacement = true;
            targets[id] = mount;
            mount.gameObject.SetActive(true);
            var arrow = new GameObject("Airflow arrow", typeof(LineRenderer)); arrow.transform.SetParent(obj.transform, false);
            var line = arrow.GetComponent<LineRenderer>(); line.useWorldSpace = false; line.sharedMaterial = assets.accent; line.widthMultiplier = .002f; line.positionCount = 5;
            line.SetPositions(new[] { new Vector3(.04f,0,-.018f),new Vector3(.04f,0,.04f),new Vector3(.025f,0,.025f),new Vector3(.04f,0,.04f),new Vector3(.055f,0,.025f) });
            Label(obj.transform, i == 0 ? "REAR EXHAUST\nAirflow out" : i == 1 ? "UPPER INTAKE\nAirflow to interior" : "LOWER INTAKE\nAirflow to interior", .09f, () => CurrentPartStep == id);
        }
    }
    XRGrabInteractable AddGrab(GameObject obj, bool gravity)
    {
        bool active = obj.activeSelf; obj.SetActive(false);
        var body = obj.GetComponent<Rigidbody>(); if (body == null) body = obj.AddComponent<Rigidbody>(); body.useGravity = gravity; body.isKinematic = !gravity;
        var grab = obj.GetComponent<XRGrabInteractable>(); if (grab == null) grab = obj.AddComponent<XRGrabInteractable>();
        grab.selectFilters.Add(new XRSelectFilterDelegate((interactor, selectable) => interactor is XRSocketInteractor || ControlsAllowed));
        grab.interactionLayers = ~0; grab.selectMode = InteractableSelectMode.Single; grab.throwOnDetach = false;
        var bounds = new Bounds(); bool first = true;
        foreach (var mesh in obj.GetComponentsInChildren<MeshFilter>())
        {
            var b = mesh.sharedMesh.bounds; var p = obj.transform.InverseTransformPoint(mesh.transform.TransformPoint(b.center));
            var size = Vector3.Scale(b.size, mesh.transform.lossyScale) / Mathf.Abs(obj.transform.lossyScale.x);
            var local = new Bounds(p, size); if (first) { bounds = local; first = false; } else bounds.Encapsulate(local);
        }
        var collider = obj.AddComponent<BoxCollider>(); collider.center = bounds.center; collider.size = Vector3.Max(bounds.size, Vector3.one * .003f);
        grab.colliders.Clear(); grab.colliders.Add(collider); grab.movementType = XRBaseInteractable.MovementType.Kinematic;
        obj.SetActive(active);
        return grab;
    }
    void CreateTools()
    {
        for (int i = 0; i < 2; i++)
        {
            var obj = Own(Instantiate(i == 0 ? assets.screwdriver : assets.pasteApplicator)); obj.transform.position = board.position + Vector3.right * (.28f + .12f * i) + Vector3.up * .08f;
            AddGrab(obj, true); var tool = obj.AddComponent<TechWiseAssemblyTool>(); tool.kind = i == 0 ? TechWiseAssemblyTool.ToolKind.Screwdriver : TechWiseAssemblyTool.ToolKind.ThermalPaste;
            tool.tip = new GameObject("Actual tool tip").transform; tool.tip.SetParent(obj.transform, false); tool.tip.localPosition = Vector3.forward * (i == 0 ? .115f : .08f);
            tool.permitted = () => ControlsAllowed;
            Label(obj.transform, i == 0 ? "SCREWDRIVER\nTip on screw + trigger" : "THERMAL PASTE\nTip at CPU + trigger", .05f,
                () => tool.kind == TechWiseAssemblyTool.ToolKind.ThermalPaste ? CurrentPhase == Phase.ApplyThermalPaste :
                    CurrentPhase == Phase.TightenCooler || CurrentPhase == Phase.TightenM2 || CurrentPhase == Phase.TightenMotherboard);
        }
    }
    void CreateScrews()
    {
        foreach (var point in assets.coolerScrews)
        {
            var screw = Screw(board, board.TransformPoint(point) + board.forward * .0025f, board.forward, "Cooler mounting screw", () => Seated("CPUCooler"));
            var pcb = board.Find("pCube511").GetComponent<MeshFilter>();
            var top = board.InverseTransformPoint(pcb.transform.TransformPoint(pcb.sharedMesh.bounds.center + Vector3.forward * pcb.sharedMesh.bounds.extents.z));
            screw.ExtendShaftTo(board.TransformPoint(new Vector3(point.x, point.y, top.z)) - board.forward * .001f);
            CoolerScrews.Add(screw);
        }
        foreach (var renderer in state.FindPart("CPUCooler").GetComponentsInChildren<Renderer>(true))
            if (renderer.name.StartsWith("pCylinder18")) { var wasEnabled = renderer.enabled; restore.Add(() => { if (renderer != null) renderer.enabled = wasEnabled; }); renderer.enabled = false; }
        M2Screws.Add(Screw(board, board.TransformPoint(assets.m2Standoff) + board.forward * .004f, board.forward, "M.2 retention screw", () => M2Lowered && Seated("M2")));
        foreach (var hole in assets.motherboardHoles)
        {
            BoardScrews.Add(Screw(board, board.TransformPoint(hole) + board.forward * .002f, board.forward, "Motherboard mounting screw", () => Seated("Motherboard")));
            var pose = assets.boardInCase;
            var p = casing.TransformPoint(pose.position + pose.rotation * Vector3.Scale(hole, pose.scale));
            var stand = Own(new GameObject("Motherboard standoff (PCB hole)", typeof(MeshFilter), typeof(MeshRenderer))); stand.transform.SetParent(casing, false);
            stand.GetComponent<MeshFilter>().sharedMesh = assets.standoffMesh; stand.GetComponent<MeshRenderer>().sharedMaterial = assets.metal;
            float supportDepth = .006f + assets.boardThickness * Mathf.Abs(board.lossyScale.z);
            stand.transform.SetPositionAndRotation(p - casing.rotation * pose.rotation * Vector3.forward * supportDepth, casing.rotation * pose.rotation); SetWorldScale(stand.transform, Vector3.one);
        }
    }
    TechWiseFastener Screw(Transform parent, Vector3 position, Vector3 normal, string name, Func<bool> allowed)
    {
        var obj = Own(Instantiate(assets.screw, parent)); obj.name = name;
        obj.transform.SetPositionAndRotation(position - normal * .006f, Quaternion.LookRotation(-normal)); SetWorldScale(obj.transform, Vector3.one);
        var screw = obj.AddComponent<TechWiseFastener>(); screw.permitted = () => ControlsAllowed && allowed(); obj.SetActive(false); return screw;
    }
    TechWiseFastener highlightedFastener;
    void RefreshFasteners()
    {
        // Resolve once per frame for the 36 markers and their callouts.
        highlightedFastener=CurrentFastener;
        foreach (var screw in Fasteners)
            if (screw != null) screw.ShowMarker(!TechWiseSimulationModeManager.IsCompetitionMode &&
                !TechWiseTutorialRuntime.ControlsPending && screw == highlightedFastener);
    }
    void CreateM2Handle()
    {
        m2HandleMade = true;
        var part = state.FindPart("M2");
        var obj = Own(new GameObject("Lower M.2 free end")); obj.SetActive(false); obj.transform.SetParent(part.transform, false);
        var pose = assets.boardParts.First(p => p.id == "M2");
        obj.transform.localPosition = Quaternion.Inverse(pose.rotation) * (assets.m2Standoff - pose.position + Vector3.forward * .002f);
        var collider = obj.AddComponent<SphereCollider>(); collider.radius = .014f / Mathf.Abs(obj.transform.lossyScale.x);
        var interactable = obj.AddComponent<XRSimpleInteractable>(); interactable.interactionLayers = ~0; interactable.colliders.Add(collider);
        var pivot = Own(new GameObject("M.2 insertion hinge")); pivot.transform.SetParent(board, false);
        M2Hinge = obj.AddComponent<TechWiseAssemblyHinge>(); M2Hinge.movingPart = pivot.transform; M2Hinge.localAxis = Vector3.down; M2Hinge.maximumAngle = 25;
        M2Hinge.permitted = () => ControlsAllowed && Seated("M2") && (!TechWiseSimulationModeManager.IsDisassembly || M2Screws.All(f => f.Removed)); M2Hinge.moved = SetM2Angle; M2Hinge.closed = () => M2Lowered = true; M2Hinge.opened = () => M2Lowered = false;
        M2Hinge.Initialize(25); Label(obj.transform, "LOWER SSD\nGrip + turn wrist", .04f, () => Seated("M2") && !M2Lowered);
        interactable.selectFilters.Add(new XRSelectFilterDelegate((_, __) => ControlsAllowed && (!M2Lowered || TechWiseSimulationModeManager.IsDisassembly)));
        obj.SetActive(true);
    }
    void SetM2Angle(float angle)
    {
        var pose = assets.boardParts.First(p => p.id == "M2"); var socket = state.FindSocket("M2"); var part = state.FindPart("M2");
        var pivot = board.TransformPoint(assets.m2Connector);
        var finalRotation = board.rotation * pose.rotation;
        var freeEnd = board.TransformPoint(assets.m2Standoff) - pivot;
        var rotation = Quaternion.AngleAxis(angle, Vector3.Cross(freeEnd, board.forward).normalized);
        var local = part.transform.InverseTransformPoint(part.GetAttachTransform(socket).position);
        targets["M2"].position = pivot + rotation * (board.TransformPoint(pose.position) - pivot + finalRotation * Vector3.Scale(local, part.transform.lossyScale));
        targets["M2"].rotation = rotation * finalRotation * Quaternion.Inverse(part.transform.rotation) * part.GetAttachTransform(socket).rotation;
    }
    internal float M2ConnectorDistance(XRGrabInteractable part)
    {
        if (board == null) return float.PositiveInfinity;
        var pose = assets.boardParts.First(p => p.id == "M2");
        var local = Quaternion.Inverse(pose.rotation) * (assets.m2Connector - pose.position);
        local = new Vector3(local.x / pose.scale.x, local.y / pose.scale.y, local.z / pose.scale.z);
        return Vector3.Distance(part.transform.TransformPoint(local), board.TransformPoint(assets.m2Connector));
    }
    void DrawM2InsertionGuide()
    {
        bool visible = Active && Ready && CurrentPartStep == "M2" && !Seated("M2") &&
            !TechWiseSimulationModeManager.IsCompetitionMode && !TechWiseTutorialRuntime.ControlsPending;
        if (!visible) { if (m2InsertionGuide != null) m2InsertionGuide.enabled = false; return; }
        var part = state.FindPart("M2"); var socket = state.FindSocket("M2");
        if (m2InsertionGuide == null)
        {
            insertionMaterial = new Material(Resources.Load<Shader>("TechWisePlacementMarker"));
            var obj = Own(new GameObject("M.2 angled insertion outline", typeof(LineRenderer)));
            m2InsertionGuide = obj.GetComponent<LineRenderer>(); m2InsertionGuide.sharedMaterial = insertionMaterial;
            m2InsertionGuide.widthMultiplier = .0015f; m2InsertionGuide.positionCount = 5;
            m2InsertionGuide.useWorldSpace = true;
        }
        m2InsertionGuide.enabled = true;
        insertionMaterial.color = state.PlacementProblem(socket, part, out _, out _) == null ? new Color(.2f,.9f,.5f) : new Color(1f,.7f,.25f);
        // Project the retained PCB mesh into the exact socket pose, including its 25-degree lift.
        var mesh = part.transform.Find("pCube842")?.GetComponent<MeshFilter>() ?? part.GetComponentInChildren<MeshFilter>();
        var bounds = mesh.sharedMesh.bounds;
        var size = bounds.size; int thin = size.x < size.y ? (size.x < size.z ? 0 : 2) : (size.y < size.z ? 1 : 2);
        int u = (thin + 1) % 3, v = (thin + 2) % 3;
        var from = part.GetAttachTransform(socket); var to = socket.GetAttachTransform(part);
        var rotation = to.rotation * Quaternion.Inverse(from.rotation);
        for (int i = 0; i < 5; i++)
        {
            int corner = i % 4; var point = bounds.center;
            point[u] += bounds.extents[u] * (corner == 0 || corner == 3 ? -1 : 1);
            point[v] += bounds.extents[v] * (corner < 2 ? -1 : 1);
            m2InsertionGuide.SetPosition(i, to.position + rotation * (mesh.transform.TransformPoint(point) - from.position));
        }
    }
    internal void ApplyPaste(Vector3 tip, Vector3 direction, bool heldTrigger, float elapsed)
    {
        if (!ControlsAllowed || PasteApplied || Cover == null || !Cover.Closed || !Lever.Closed || !Seated("CPU") || !heldTrigger) return;
        var part = state.FindPart("CPU"); var renderers = part.GetComponentsInChildren<Renderer>(); var bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        var centre = bounds.center + board.forward * Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(board.forward.x),Mathf.Abs(board.forward.y),Mathf.Abs(board.forward.z)));
        if (Vector3.Distance(tip, centre) > .025f || Vector3.Angle(direction, -board.forward) > 25) return;
        pasteBefore ??= TechWiseAssemblyHistory.Capture();
        pasteProgress += Mathf.Clamp(elapsed, 0, .1f);
        if (blob == null)
        {
            blob = Own(new GameObject("Visible thermal paste on CPU", typeof(MeshFilter), typeof(MeshRenderer))); blob.transform.SetParent(part.transform, true);
            blob.transform.SetPositionAndRotation(centre + board.forward * .001f, Quaternion.FromToRotation(Vector3.up, board.forward)); SetWorldScale(blob.transform, Vector3.one);
            blob.GetComponent<MeshFilter>().sharedMesh = assets.pasteBlob; blob.GetComponent<MeshRenderer>().sharedMaterial = assets.paste;
        }
        SetWorldScale(blob.transform, Vector3.one * Mathf.Clamp01(pasteProgress));
        if (pasteProgress >= 1) { PasteApplied = true; TechWiseAssemblyHistory.Commit(pasteBefore); pasteBefore = null; }
    }
    TMP_Text Label(Transform parent, string text, float height, Func<bool> visible = null)
    {
        var obj = new GameObject("Phase 1 instruction", typeof(TextMeshPro)); obj.transform.SetParent(parent, true); obj.transform.position = parent.position + Vector3.up * height;
        var label = obj.GetComponent<TextMeshPro>(); label.text = text; label.fontSize = .12f; label.color = new Color(.95f,.96f,.97f); label.alignment = TextAlignmentOptions.Center; label.rectTransform.sizeDelta = new Vector2(.28f,.06f);
        SetWorldScale(obj.transform, Vector3.one); labels.Add(label); if (visible != null) labelConditions[label] = visible;
        obj.SetActive(false); return label;
    }
    internal bool Seated(string step)
    {
        if (state == null) return false;
        var parts = state.Parts.Where(p => p != null && state.StepOf(p) == step).ToArray();
        return parts.Length == (TechWiseBuildDefinition.Find(step)?.quantity ?? 1) && parts.All(p => state.IsPartInstalled(p) && !TechWiseSimulationRuntime.IsHeld(p));
    }
    static bool Tight(List<TechWiseFastener> screws) => screws.Count > 0 && screws.All(s => s != null && s.Complete);
    public bool StepFastened(string step)
    {
        if (!Ready) return false;
        if (step == "CPU") return Cover.Closed && Lever.Closed;
        if (step == "CPUCooler" && !PasteApplied || step == "M2" && !M2Lowered) return false;
        var definition = TechWiseBuildDefinition.Find(step);
        if (definition == null || definition.screws == 0) return true;
        var screws = Fasteners.Where(f => f.ComponentId == step).ToArray();
        return screws.Length == definition.screws && screws.All(f => f.Complete);
    }
    public Phase CurrentPhase
    {
        get
        {
            if (!Ready) return Phase.PrepareMotherboard;
            if (!Seated("CPU")) return Cover.Angle < 60 ? Phase.OpenSocket : Phase.InstallCPU;
            if (!StepFastened("CPU")) return Phase.CloseAndLockSocket;
            if (!PasteApplied) return Phase.ApplyThermalPaste;
            if (!Seated("CPUCooler")) return Phase.InstallCooler;
            if (!StepFastened("CPUCooler")) return Phase.TightenCooler;
            if (!Seated("RAM")) return Phase.InstallRAM;
            if (!Seated("M2") || !M2Lowered) return Phase.InsertAndLowerM2;
            if (!StepFastened("M2")) return Phase.TightenM2;
            if (!CasePrepared) return Phase.PrepareCase;
            if (!Seated("Motherboard")) return Phase.PlaceMotherboard;
            if (!StepFastened("Motherboard")) return Phase.TightenMotherboard;
            if (!Seated("FanRear") || !StepFastened("FanRear")) return Phase.InstallRearFan;
            if (!Seated("FanFront1") || !StepFastened("FanFront1") || !Seated("FanFront2") || !StepFastened("FanFront2")) return Phase.InstallFrontFans;
            if (!FanAlignmentValid) return Phase.ValidateAirflow;
            if (HasGpuConnector && !Seated("GPUConnector")) return Phase.InstallGpuConnector;
            if (!Seated("GPU") || !StepFastened("GPU")) return Phase.InstallGpu;
            if (!Seated("Storage") || !StepFastened("Storage")) return Phase.InstallStorage;
            if (!Seated("PSU") || !StepFastened("PSU")) return Phase.InstallPsu;
            if (!PanelInstalled) return Phase.CloseCase;
            return Phase.Complete;
        }
    }
    bool HasGpuConnector => state.Parts.Exists(p => p != null && state.StepOf(p) == "GPUConnector");
    internal bool PreparationComplete => Ready && Seated("Motherboard") && StepFastened("Motherboard") &&
        Seated("FanRear") && StepFastened("FanRear") && Seated("FanFront1") && StepFastened("FanFront1") && Seated("FanFront2") && StepFastened("FanFront2") && FanAlignmentValid;
    bool FanAlignmentValid => new[]{"FanRear","FanFront1","FanFront2"}.All(id =>
    {
        var part = state.FindPart(id); var socket = state.FindSocket(id);
        return part != null && socket != null && Quaternion.Angle(part.GetAttachTransform(socket).rotation, socket.GetAttachTransform(part).rotation) <= 10;
    });
    public string CurrentPartStep => TechWiseSimulationModeManager.IsDisassembly ? DisassemblyStep : CurrentPhase switch
    {
        Phase.PrepareMotherboard or Phase.OpenSocket or Phase.InstallCPU or Phase.CloseAndLockSocket or Phase.ApplyThermalPaste => "CPU",
        Phase.InstallCooler or Phase.TightenCooler => "CPUCooler",
        Phase.InstallRAM => "RAM",
        Phase.InsertAndLowerM2 or Phase.TightenM2 => "M2",
        Phase.PrepareCase or Phase.PlaceMotherboard or Phase.TightenMotherboard => "Motherboard",
        Phase.InstallRearFan => "FanRear",
        Phase.InstallFrontFans => !Seated("FanFront1") || !StepFastened("FanFront1") ? "FanFront1" : "FanFront2",
        Phase.InstallGpuConnector => "GPUConnector",
        Phase.InstallGpu => "GPU",
        Phase.InstallStorage => "Storage",
        Phase.InstallPsu => "PSU",
        _ => null
    };
    int GuideStep => CurrentPhase switch
    {
        Phase.PrepareMotherboard or Phase.OpenSocket or Phase.InstallCPU => 1,
        Phase.ValidateAirflow => 13,
        Phase.CloseCase => 18,
        Phase.InstallGpuConnector or Phase.InstallGpu or Phase.InstallStorage or Phase.InstallPsu => (int)CurrentPhase - 2,
        _ => (int)CurrentPhase - 1
    };
    public string Heading => BuildHeading;
    internal string LegacyInstruction => SetupError != null ? "Setup needs attention: " + SetupError : CurrentPhase switch
    {
        Phase.PrepareMotherboard => "Prepare the existing motherboard outside the case.",
        Phase.OpenSocket => "Open the CPU cover and retention arm before inserting the CPU.",
        Phase.InstallCPU => "The CPU socket is open. Align the CPU's keyed orientation, approach the socket, then release the grip. A backwards or rotated CPU will not snap.",
        Phase.CloseAndLockSocket => !Cover.Closed ? "Hold grip on the CPU cover and move your hand DOWN toward the motherboard. Release to close." : "Hold grip on the locking arm and move your hand DOWN toward the motherboard. Release to lock the CPU.",
        Phase.ApplyThermalPaste => "Grab the thermal-paste applicator. Aim its tip down at the centre of the CPU and hold the trigger until a visible paste blob is applied.",
        Phase.InstallCooler => "Align the cooler with the CPU's four mounting points and release. Thermal paste must be present.",
        Phase.TightenCooler => "Tighten each of the four cooler screws. Hold the screwdriver tip against one screw, align its shaft, then hold trigger. " + Count(CoolerScrews),
        Phase.InstallRAM => "Install all four RAM sticks. Match the notch and release to rest the stick above its slot. Grip it again, push straight down toward the motherboard, then release to seat it. Raised sticks are not installed.",
        Phase.InsertAndLowerM2 => !Seated("M2") ? "Match the angled SSD outline. Slide the connector into the slot with the free end raised (about 25 degrees), then release the grip." : "Grip the SSD's free-end handle and rotate your wrist to lower it onto the existing standoff. Release when flat.",
        Phase.TightenM2 => "Use the screwdriver on the M.2 retention screw. The SSD is not complete until the screw is fully tightened.",
        Phase.PrepareCase => "Grip the office-case side panel and remove it. The standoffs follow the motherboard's real mounting holes.",
        Phase.PlaceMotherboard => "Lift the prepared motherboard, align it with the case standoffs, then release into the mounting position.",
        Phase.TightenMotherboard => "Tighten every motherboard screw with the screwdriver, one at a time. " + Count(BoardScrews),
        Phase.InstallRearFan => "Install the rear fan at the CPU-area exhaust grille. Its airflow arrow must point out through the rear ventilation.",
        Phase.InstallFrontFans => "Install both front fans at their ventilation grilles. Their airflow arrows point from the front toward the interior.",
        Phase.ValidateAirflow => "Check all three fan directions against their mounting arrows. Reversed airflow cannot complete this phase.",
        Phase.InstallGpuConnector => "Install the GPU connector in its highlighted bracket. Align the connector, then release grip.",
        Phase.InstallGpu => "Install the graphics card (with its attached cooler). Align it with the highlighted GPU socket, then release grip.",
        Phase.InstallStorage => "Install the SATA SSD in the highlighted storage mount. Match its orientation and release grip.",
        Phase.InstallPsu => "Install the power supply in the highlighted lower case bracket. Its power inlet faces the rear opening. Align and release grip.",
        _ => "PC assembly complete: CPU, cooler, RAM, M.2 SSD, motherboard, fans, GPU connector, graphics card, SATA SSD and power supply installed. In Competition, press Done on the verification monitor to test the PC."
    };
    static string Count(List<TechWiseFastener> screws) => screws.Count(s => s.Progress >= 1) + " / " + screws.Count + " tight.";
    internal string PrerequisiteProblem(string step)
    {
        if (!Ready) return "Wait for the detailed assembly workbench to finish preparing.";
        if (TechWiseSimulationModeManager.IsDisassembly || TechWiseAssemblyHistory.Restoring || TechWiseDisassemblyRuntime.IsPreparing) return null;
        if (Seated(step)) return null;
        if (TechWiseTutorialRuntime.InTutorial && CurrentPartStep != step) return "First complete the current objective on the lesson panel.";
        return step switch
        {
            "CPU" when Cover.Angle < 60 || Cover.Closed => "Open the CPU retention cover first.",
            "CPUCooler" when !PasteApplied => "Lock the CPU and apply visible thermal paste before installing the cooler.",
            "RAM" when !StepFastened("CPUCooler") => "Tighten all cooler screws before installing RAM.",
            "M2" when !Seated("RAM") => "Install all RAM sticks before inserting the M.2 SSD.",
            "Motherboard" when !StepFastened("M2") || !StepFastened("CPUCooler") || !CasePrepared => "Finish the motherboard preparation and remove the case side panel first.",
            "FanRear" when !StepFastened("Motherboard") => "Tighten all motherboard screws before installing fans.",
            "FanFront1" or "FanFront2" when !Seated("FanRear") || !StepFastened("FanRear") => "Install the rear exhaust fan first.",
            "GPUConnector" or "GPU" or "PSU" or "Storage" when !PreparationComplete => "Finish motherboard, case and fan assembly first.",
            "GPU" when HasGpuConnector && !Seated("GPUConnector") => "Install the GPU connector first.",
            "Storage" when !Seated("GPU") || !StepFastened("GPU") => "Install the graphics card first.",
            "PSU" when !Seated("Storage") || !StepFastened("Storage") => "Install the SATA SSD first.",
            _ => null
        };
    }
    internal bool CanGrab(string step)
    {
        if (!Ready) return false;
        if (TechWiseSimulationModeManager.IsDisassembly) return CanRemove(step);
        if (TechWiseTutorialRuntime.InTutorial && step != CurrentPartStep) return false;
        return step switch
        {
            "CPU" => !Cover.Closed,
            "CPUCooler" => !CoolerScrews.Any(s => s.Inserted),
            "M2" => !Seated("M2"),
            "Motherboard" => !Seated("Motherboard") && StepFastened("M2") && StepFastened("CPUCooler"),
            "RAM" or "FanRear" or "FanFront1" or "FanFront2" => !Seated(step),
            _ => true
        };
    }
    void RevealCurrentTutorialPart()
    {
        if (!TechWiseTutorialRuntime.InTutorial || TechWiseSimulationModeManager.IsDisassembly) return;
        var current = CurrentPartStep;
        var part = state.Parts.Find(p => p != null && state.StepOf(p) == current && !state.IsPartInstalled(p));
        if (part != null && !part.gameObject.activeInHierarchy)
        {
            // The two authored scenes hide different levels of the component tray hierarchy.
            for (var item = part.transform; item != null && item.name != "Assembly Component"; item = item.parent)
                if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);
            TechWiseComponentRecovery.TrackTutorialPart(part);
        }
    }
}
