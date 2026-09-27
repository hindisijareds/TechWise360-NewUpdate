using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Content.Interaction;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public static class TechWiseCompleteBuildVerification
{
    const string Key = "TechWise.CompleteBuild.Verification";
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static IEnumerator flow;
    static readonly Stack<IEnumerator> routines = new();
    static int frame = -1, checks;
    static double deadline;
    static readonly List<string> errors = new();
    static readonly List<string> frameworkErrors = new();
    static readonly string[] Prefs = { MainMenu.ControlModeKey, TechWiseSimulationModeManager.GameModeKey, TechWiseSimulationModeManager.SimulationTypeKey, TechWiseSimulationModeManager.CompetitionIdKey, TechWiseSimulationModeManager.CompetitionTitleKey };
    static TechWiseSimulationRuntime state;
    static TechWiseDetailedAssemblyRuntime phase;
    static XRDirectInteractor hand;
    public static void Run()
    {

        foreach (var key in Prefs) { SessionState.SetBool(Key + key, PlayerPrefs.HasKey(key)); SessionState.SetString(Key + key + "value", PlayerPrefs.GetString(key)); }
        PlayerPrefs.SetString(MainMenu.ControlModeKey,MainMenu.VrModeValue);
        TechWiseSimulationModeManager.SetMode("assembly", "practice");
        EditorSceneManager.OpenScene("Assets/Scenes/Multiplayer.unity");
        SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
    }
    public static void RunCompetition() { SessionState.SetBool(Key+"competitionOnly",true); Run(); }
    [InitializeOnLoadMethod]
    static void Resume()
    {
        EditorApplication.playModeStateChanged += change =>
        {
            if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
            TechWiseAttemptRecorder.LocalVerificationMode = true;
            TechWiseOfflineAttemptQueue.VerificationQueuePath = Path.GetFullPath("Logs/CompleteBuildAudit/test-queue.json");
            var queue = TechWiseOfflineAttemptQueue.EnsureInstance(); queue.StopAllCoroutines(); queue.enabled = false;
            foreach (var interactor in UnityEngine.Object.FindObjectsByType<XRBaseInteractor>()) if (interactor is not XRSocketInteractor) interactor.enabled = false;
            Application.logMessageReceived += Log;
            deadline = EditorApplication.timeSinceStartup + 1200; flow = SessionState.GetBool(Key+"competitionOnly",false)?CompetitionAssembly():Scenarios(); routines.Push(flow); EditorApplication.update += Tick;
        };
    }
    static void Log(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception) return;
        // Report the installed toolkit's known, unused UI Toolkit registry diagnostics separately;
        // they are not suppressed from the Unity log or the final report.
        if (message.StartsWith("No available indices for interactor registration.") || message.TrimStart().StartsWith("- Slot "))
            frameworkErrors.Add(message + "\n" + stack);
        else errors.Add(message + "\n" + stack);
    }
    static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Phase 1 verification timed out");
            EditorApplication.isPaused = false; Application.runInBackground = true; EditorApplication.QueuePlayerLoopUpdate();
            if (frame == Time.frameCount) return; frame = Time.frameCount;
            while (routines.Count > 0)
            {
                var current = routines.Peek();
                if (!current.MoveNext()) { routines.Pop(); continue; }
                if (current.Current is IEnumerator nested) { routines.Push(nested); continue; }
                return;
            }
            Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Debug.Log("COMPLETE_BUILD_PASS " + description); }
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);
    static void Finish(Exception error)
    {
        EditorApplication.update -= Tick; Application.logMessageReceived -= Log; SessionState.SetBool(Key, false); SessionState.SetBool(Key+"competitionOnly",false);
        foreach (var key in Prefs) if (SessionState.GetBool(Key + key, false)) PlayerPrefs.SetString(key, SessionState.GetString(Key + key + "value", "")); else PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save(); Directory.CreateDirectory("Logs/CompleteBuildAudit");
        File.WriteAllText("Logs/CompleteBuildAudit/results.txt", (error == null ? "PASS: Complete build feature checks" : error.ToString()) + $"\nChecks: {checks}\nCore runtime errors: {errors.Count}\nKnown XR framework error messages: {frameworkErrors.Count}\nPhysical Quest interaction not tested.\n" + string.Join("\n", errors) + "\nFramework diagnostics:\n" + string.Join("\n",frameworkErrors));
        EditorApplication.Exit(error == null && errors.Count == 0 ? 0 : 1);
    }
    static void Align(XRGrabInteractable part, XRLockSocketInteractor socket)
    {
        part.gameObject.SetActive(true); var body = part.GetComponent<Rigidbody>(); body.isKinematic = true;
        var attach = part.GetAttachTransform(socket); var target = socket.GetAttachTransform(part);
        part.transform.rotation = target.rotation * Quaternion.Inverse(attach.rotation) * part.transform.rotation;
        part.transform.position += target.position - attach.position; Physics.SyncTransforms();
    }
    static IEnumerator Install(string id)
    {
        var part = state.Parts.First(p => state.StepOf(p) == id && !state.IsPartInstalled(p));
        var socket = state.Sockets.First(s => state.StepOf(s) == id && !s.hasSelection && TechWiseSimulationRuntime.Matches(s, part.transform));
        Align(part, socket);
        var correct = part.transform.rotation;
        foreach (float angle in new[] {90f,180f})
        {
            part.transform.rotation = Quaternion.AngleAxis(angle, socket.transform.up) * correct;
            part.transform.position += socket.GetAttachTransform(part).position - part.GetAttachTransform(socket).position;
            socket.interactionManager.HoverEnter((IXRHoverInteractor)socket,part);
            Check(state.PlacementProblem(socket, part, out _, out _) != null, id + " rejects " + angle + " degree orientation");
            Check(!socket.placementPreviewValid(part),id + " invalid hover preview remains invalid");
            Call(state, "AttachPartToSocket", part, socket);
            Check(!state.IsPartInstalled(part), id + " invalid orientation does not snap");
            socket.interactionManager.HoverExit((IXRHoverInteractor)socket,part);
        }
        Align(part, socket); part.transform.position += Vector3.up * .12f;
        Check(state.PlacementProblem(socket, part, out _, out _) != null, id + " rejects distant placement");
        Align(part, socket);
        Check(state.PlacementProblem(socket, part, out _, out var correction) == null, id + " correct placement allowed: " + correction);
        var manager = socket.interactionManager;
        Check(manager.IsSelectPossible((IXRSelectInteractor)hand,part), id + " selectable by existing XR controller interface");
        manager.SelectEnter((IXRSelectInteractor)hand,part);
        Check(TechWiseSimulationRuntime.IsHeld(part),id + " controller owns grab");
        if (id == "CPU")
        {
            var presentation = UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>(); Call(presentation, "LateUpdate");
            var label = presentation.GetComponentsInChildren<TMPro.TMP_Text>(true).First(t => t.name == "Component name: CPU");
            Check(!label.gameObject.activeSelf, "Blue component name hides while gripped");
            Check(label.color.b > .9f && label.color.g > .7f && label.GetComponent<UnityEngine.UI.Image>() == null, "Component name is blue without a black panel");
            var line = new GameObject("Outline check", typeof(LineRenderer)).GetComponent<LineRenderer>();
            Geometry("Outline", line, part.transform); Check(!line.enabled && line.positionCount == 0, "Component outline hides on grip"); UnityEngine.Object.Destroy(line.gameObject);
        }
        Call(state,"AttachPartToSocket",part,socket);
        Check(!state.IsPartInstalled(part),id + " cannot snap while controller holds it");
        manager.SelectExit((IXRSelectInteractor)hand,part);
        for (int i = 0; i < 4; i++) yield return null;
        Check(state.IsPartInstalled(part), id + " seated through existing XR socket");
        Check(Vector3.Distance(part.GetAttachTransform(socket).position, socket.GetAttachTransform(part).position) < .001f, id + " exact final attachment");
        if (id == "CPU")
        {
            Check(UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>().GetComponentsInChildren<TMPro.TMP_Text>().Any(t => t.name == "Component name: CPU"), "Component name returns after release");
            var label = UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>().GetComponentsInChildren<TMPro.TMP_Text>().First(t => t.name == "Component name: CPU");
            var meshes = part.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.GetComponent<TMPro.TMP_Text>() == null).ToArray();
            var bounds = meshes[0].bounds; foreach (var mesh in meshes) bounds.Encapsulate(mesh.bounds);
            Check(Mathf.Abs(label.transform.position.y - bounds.max.y - .018f) < .002f && Vector2.Distance(new Vector2(label.transform.position.x,label.transform.position.z),new Vector2(bounds.center.x,bounds.center.z)) < .002f, "Name sits directly 18 mm above component, without camera-dependent stacking");
            var line = new GameObject("Outline check", typeof(LineRenderer)).GetComponent<LineRenderer>(); Geometry("Outline", line, part.transform);
            Check(line.enabled && line.positionCount == 16, "Component outline returns on release"); bounds.Expand(.008f);
            var points = new Vector3[line.positionCount]; line.GetPositions(points); Check(points.All(bounds.Contains), "Outline fits actual mesh without including FBX pivot"); UnityEngine.Object.Destroy(line.gameObject);
        }
    }
    static void Geometry(string method, params object[] args) => typeof(TechWiseDetailedAssemblyRuntime).Assembly.GetType("TechWiseComponentGeometry").GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    static void Tighten(List<TechWiseFastener> screws, string id)
    {
        Check(!phase.StepFastened(id), id + " incomplete before screws");
        foreach (var screw in screws)
        {
            Check(!screw.Inserted && screw.Progress==0,id+" has a separate loose manual screw "+screw.Id);
            var hole=screw.Hole; var grab=screw.Grab; var manager=grab.interactionManager;
            Check(manager.IsSelectPossible((IXRSelectInteractor)hand,grab),screw.Id+" can be gripped");
            manager.SelectEnter((IXRSelectInteractor)hand,grab);
            grab.transform.SetPositionAndRotation(hole.transform.position,hole.transform.rotation);
            Check(!screw.Inserted,screw.Id+" held screw cannot count as inserted");
            manager.SelectExit((IXRSelectInteractor)hand,grab); Call(screw,"LateUpdate");
            Check(screw.Inserted,screw.Id+" release inserts only into its keyed hole");
            Check(!phase.StepFastened(id),id+" remains incomplete with an untightened screw");
            Vector3 Head() => (Vector3)typeof(TechWiseFastener).GetProperty("HeadPosition", Hidden).GetValue(screw);
            Call(screw, "Advance", Head() + Vector3.one, screw.transform.forward, true, .1f);
            Call(screw, "Advance", Head(), -screw.transform.forward, true, .1f);
            Call(screw, "Advance", Head(), screw.transform.forward, false, .1f);
            Check(screw.Progress == 0, id + " screw rejects distance, reversed driver and no trigger");
            Call(screw, "Advance", Head(), screw.transform.forward, true, .1f);
            Check(screw.State == TechWiseFastener.TighteningState.PartiallyTightened, id + " screw progresses individually");
            for (int i = 0; i < 30; i++) Call(screw, "Advance", Head(), screw.transform.forward, true, .1f);
            Check(screw.State == TechWiseFastener.TighteningState.FullyTightened, id + " screw fully tightened");
        }
        Check(phase.StepFastened(id), id + " complete after all screws");
    }
    static void Blocked(string id, string description)
    {
        var part=state.FindPart(id); var position=part.transform.position; var rotation=part.transform.rotation;
        var socket=state.FindSocket(id); Align(part,socket);
        Check(state.PlacementProblem(socket,part,out _,out _) != null,description);
        part.transform.SetPositionAndRotation(position,rotation);
    }
    static IEnumerator Scenarios()
    {
        for (int i = 0; i < 35; i++) yield return null;
        state = TechWiseSimulationRuntime.Instance; phase = TechWiseDetailedAssemblyRuntime.Instance;
        var handObject = new GameObject("Left verification controller",typeof(SphereCollider),typeof(Rigidbody));
        handObject.GetComponent<SphereCollider>().isTrigger = true; handObject.GetComponent<Rigidbody>().isKinematic = true;
        hand = handObject.AddComponent<XRDirectInteractor>(); hand.interactionLayers = ~0;
        Check(phase != null && phase.Ready, "Phase 1 setup ready: " + phase?.SetupError);
        Check(state.ConfigurationError == null, "No missing component/socket configuration");
        CaptureLabels();
        var presentationRoot = UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>();
        var placards = (List<GameObject>)typeof(TechWiseAssemblyPresentation).GetField("retiredPlacards",Hidden).GetValue(presentationRoot);
        Check(placards.Count >= 8 && placards.All(p => !p.activeSelf), "Old component placard panels, including their black rectangles, are hidden");
        Check(placards.Any(p => p.name == "GPU") && placards.Any(p => p.name == "PSU"), "Legacy GPU and PSU signs on the component table are retired");
        Check(phase.Cover.Angle >= 60 && !phase.Cover.Closed && !phase.Lever.Closed, "Existing CPU cover and arm initially open");
        foreach (var hinge in new[] { phase.Cover, phase.Lever })
            Check(Vector3.Dot(hinge.GetComponentsInChildren<MeshRenderer>().First(r => r.GetComponent<TMPro.TMP_Text>() == null).bounds.center - hinge.transform.position, state.FindPart("Motherboard").transform.forward) > .005f,
                hinge.name + " opens above the PCB, not underneath it");
        Check(UnityEngine.Object.FindObjectsByType<TechWiseAssemblyTool>().Count(t => t.kind == TechWiseAssemblyTool.ToolKind.ThermalPaste) == 1,"Exactly one working paste applicator");
        Check(!UnityEngine.Object.FindObjectsByType<Transform>().Any(t => t.name == "Thermal Paste" || t.name == "Thermal Paste (1)"),"Legacy paste and its label stay hidden from recovery");
        Check(phase.CoolerScrews.Count == 4 && phase.BoardScrews.Count == 9 && phase.M2Screws.Count == 1, "Real model mounting positions provide 4/9/1 individual screws");
        foreach (var hinge in new[]{phase.Cover,phase.Lever})
        {
            var interactable = hinge.GetComponent<XRSimpleInteractable>();
            Check(interactable.colliders.All(c => interactable.interactionManager.TryGetInteractableForCollider(c,out var registered) && ReferenceEquals(registered,interactable)),"Hinge collider maps to its active XR interaction");
        }
        foreach(var tool in UnityEngine.Object.FindObjectsByType<TechWiseAssemblyTool>())
        {
            var grab=tool.GetComponent<XRGrabInteractable>();
            Check(grab.colliders.All(c=>grab.interactionManager.TryGetInteractableForCollider(c,out var registered)&&ReferenceEquals(registered,grab)),"Tool mesh collider registered for XR rays and direct grips");
            grab.interactionManager.SelectEnter((IXRSelectInteractor)hand,grab); Check(TechWiseSimulationRuntime.IsHeld(grab),"Tool uses existing grip selection");
            grab.interactionManager.SelectExit((IXRSelectInteractor)hand,grab);
        }
        Check(!(bool)Call(phase, "CanGrab", "Motherboard"), "Motherboard stays on workbench during preparation");
        Capture("open-socket.png", state.FindPart("Motherboard").transform, new Vector3(.1f,1,.25f));
        var cooler = state.FindPart("CPUCooler"); var coolerSocket = state.FindSocket("CPUCooler"); Align(cooler,coolerSocket);
        Check(state.PlacementProblem(coolerSocket,cooler,out _,out _) != null, "Cooler blocked before CPU lock and paste"); cooler.transform.position += Vector3.right * .5f;
        yield return Install("CPU");
        Check(!state.IsInstalled("CPU"), "Seating alone does not complete CPU");
        Call(phase.Lever,"Observe",0f); Call(phase.Lever,"Commit"); Check(!phase.Lever.Closed,"Arm cannot lock before cover");
        Call(phase.Cover,"Observe",30f); Call(phase.Cover,"Commit"); Check(!phase.Cover.Closed,"Cover must be lowered before release");
        foreach (var hinge in new[] { phase.Cover, phase.Lever })
        {
            Call(hinge, "Observe", hinge.maximumAngle);
            var interaction = hinge.GetComponent<XRSimpleInteractable>(); var manager = interaction.interactionManager;
            manager.SelectEnter((IXRSelectInteractor)hand, interaction);
            var start = hand.transform.position; var rotation = hand.transform.rotation; var down = -state.FindPart("Motherboard").transform.forward;
            Call(hinge, "ObserveHandMotion", start - down * .03f, rotation);
            Check(hinge.Angle >= hinge.maximumAngle - 1, hinge.name + " upward hand movement does not close it");
            Call(hinge, "ObserveHandMotion", start + down * .1f, rotation);
            manager.SelectExit((IXRSelectInteractor)hand, interaction);
            Check(hinge.Closed, hinge.name + " closes with downward hand travel and grip release");
        }
        Check(state.IsInstalled("CPU"), "CPU completed after cover and arm lock");
        Call(phase,"ApplyPaste",Vector3.one*20,Vector3.down,true,.1f); Check(!phase.PasteApplied,"Paste rejects wrong location");
        var cpu = state.FindPart("CPU"); var normal = state.FindPart("Motherboard").transform.forward;
        var bounds = cpu.GetComponentsInChildren<Renderer>()[0].bounds; foreach(var r in cpu.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
        var centre = bounds.center + normal * Vector3.Dot(bounds.extents,new Vector3(Mathf.Abs(normal.x),Mathf.Abs(normal.y),Mathf.Abs(normal.z)));
        for(int i=0;i<11;i++) Call(phase,"ApplyPaste",centre,-normal,true,.1f);
        Check(phase.PasteApplied && GameObject.Find("Visible thermal paste on CPU").GetComponent<MeshFilter>().sharedMesh.vertexCount > 0,"Actual visible paste mesh applied");
        Capture("paste.png", state.FindPart("Motherboard").transform, new Vector3(.1f,1,.25f));
        yield return Install("CPUCooler");
        Check(string.IsNullOrEmpty(phase.Feedback), "Paste success message clears as soon as cooler is seated");
        Check(phase.CoolerScrews.All(s => s.GetComponentsInChildren<MeshRenderer>().Any(r => r.name == "Steel head and threads") && s.GetComponentsInChildren<MeshRenderer>().Any(r => r.name == "Phillips recess")), "Each screw has a solid threaded model and visible Phillips head");
        Capture("cooler-screws.png",state.FindPart("Motherboard").transform,new Vector3(.1f,1,.25f));
        Blocked("RAM","RAM blocked until all four cooler screws are tight"); Tighten(phase.CoolerScrews,"CPUCooler");
        var assets = Resources.Load<TechWisePhaseOneAssets>("TechWisePhaseOneAssets");
        for (int i = 0; i < phase.CoolerScrews.Count; i++)
            Check(Vector3.Distance(phase.CoolerScrews[i].transform.TransformPoint(new Vector3(0,0,-.0035f)), state.FindPart("Motherboard").transform.TransformPoint(assets.coolerScrews[i])) < .0005f,
                "Tightened cooler screw head seats against bracket " + i);
        Capture("cooler-screws-tight.png",state.FindPart("Motherboard").transform,new Vector3(.1f,1,.25f));
        foreach (var screw in phase.CoolerScrews)
        {
            var mesh = screw.transform.Find("Steel head and threads").GetComponent<MeshFilter>();
            Check(mesh.sharedMesh.bounds.max.z > .006f, "Cooler screw shaft extends beyond short original into motherboard");
        }
        var driver = UnityEngine.Object.FindObjectsByType<TechWiseAssemblyTool>().First(t => t.kind == TechWiseAssemblyTool.ToolKind.Screwdriver);
        var driverPose = driver.transform.rotation; var driverTip = driver.tip.position;
        Call(driver, "RotateWhileDriving", 90f);
        Check(Quaternion.Angle(driver.transform.Find("Rotating screwdriver model").localRotation, Quaternion.identity) > 80 && driver.transform.rotation == driverPose && driver.tip.position == driverTip, "Screwdriver model rotates while grip pose and tip remain stable");
        Check(driver.GetComponentsInChildren<MeshRenderer>().SelectMany(r => r.sharedMaterials).Any(m => m.name.Contains("yellow")), "Screwdriver has visible yellow and grey finish");
        var detail = UnityEngine.Object.FindAnyObjectByType<TechWiseScrewDetail>();
        Check(detail != null && !detail.GetComponentsInChildren<UnityEngine.UI.RawImage>().Any(), "Existing full-view magnification retained without a blocking inset");
        foreach (var id in new[]{"FanRear","FanFront1","FanFront2"})
        {
            var arrow = new GameObject("Fan arrow test", typeof(LineRenderer)).GetComponent<LineRenderer>(); var target = state.FindSocket(id).transform;
            Geometry("TargetArrow", arrow, target, .12f);
            Check(Vector3.Distance(arrow.GetPosition(1),target.position) < .001f && Mathf.Abs(arrow.GetPosition(0).y-target.position.y) < .001f, id + " arrow is horizontal and ends at the exact mount");
            Check(Vector3.Dot(arrow.GetPosition(0)-target.position, id=="FanRear" ? -target.forward : target.forward) > .1f, id + " arrow starts inside the enclosure"); UnityEngine.Object.Destroy(arrow.gameObject);
        }
        for(int i=0;i<4;i++) { if(i<4) Blocked("M2","M2 blocked until all RAM sticks are installed"); yield return Install("RAM"); } Check(state.IsInstalled("RAM"),"All four RAM sticks required");
        var ssd = state.FindPart("M2"); var ssdSocket = state.FindSocket("M2");
        Align(ssd,ssdSocket); var ssdRotation = ssd.transform.rotation; var ssdPosition = ssd.transform.position;
        ssd.transform.position += state.FindPart("Motherboard").transform.forward * .035f;
        Check(state.PlacementProblem(ssdSocket,ssd,out _,out _) == null, "M2 accepts a 3.5 cm near-slot approach");
        ssd.transform.position=ssdPosition; ssd.transform.rotation=Quaternion.AngleAxis(15f,state.FindPart("Motherboard").transform.forward)*ssdRotation;
        Check(state.PlacementProblem(ssdSocket,ssd,out _,out _) == null,"M2 accepts realistic controller orientation variation");
        ssd.transform.rotation=ssdRotation;
        yield return null;
        Check(GameObject.Find("M.2 angled insertion outline") != null,"M2 displays its actual angled insertion target");
        Check(string.IsNullOrEmpty(phase.Feedback), "M2 step does not retain cooler-ready message");
        yield return Install("M2");
        for(int i=0;i<3;i++) yield return null;
        Check(phase.M2Hinge != null && !phase.M2Lowered && !state.IsInstalled("M2"),"M2 inserted angled but incomplete");
        Call(phase.M2Hinge,"Observe",0f); Call(phase.M2Hinge,"Commit"); yield return null;
        Check(phase.M2Lowered,"M2 lowered onto actual board standoff"); Tighten(phase.M2Screws,"M2");
        Capture("prepared-board.png",state.FindPart("Motherboard").transform,new Vector3(.1f,1,.25f));
        Check((bool)Call(phase,"CanGrab","Motherboard"),"Prepared motherboard can use existing grip system");
        var panel = (XRGrabInteractable)typeof(TechWiseDetailedAssemblyRuntime).GetField("panel",Hidden).GetValue(phase);
        panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel); panel.transform.position += Vector3.right*.5f;
        panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel); yield return null;
        Check(phase.CasePrepared,"Side panel removal prepares case");
        yield return Install("Motherboard"); Blocked("FanRear","Rear fan blocked until all motherboard screws are tight"); Blocked("FanFront1","Front fan blocked until all motherboard screws are tight"); Tighten(phase.BoardScrews,"Motherboard");
        yield return Install("FanRear"); yield return Install("FanFront1"); yield return Install("FanFront2");
        Check(phase.CurrentPhase != TechWiseDetailedAssemblyRuntime.Phase.Complete,"Fans do not finish the build while GPU/storage/PSU are missing");
        yield return FinishRemainingComponents();
        Check(state.FindPart("GPU") != null && state.FindPart("PSU") != null && state.FindPart("Storage") != null,"Existing downstream PC components preserved");
        var caseRoot = GameObject.Find("Office PC enclosure").transform.parent;
        panel.gameObject.SetActive(false); // Move the detached access panel out of the diagnostic camera's view.
        Capture("assembled-case.png", caseRoot, caseRoot.forward * 1.8f - caseRoot.right * .8f + Vector3.up * .35f);
        Capture("psu-rear-opening.png", caseRoot, caseRoot.right * 2 + caseRoot.forward * .15f);
        var enclosure = GameObject.Find("Office PC enclosure").transform;
        var opening = enclosure.Find("PSU rear opening");
        Check(opening != null, "Case includes dedicated PSU rear opening");
        var psu = state.FindPart("PSU"); var psuBounds = psu.GetComponentsInChildren<MeshRenderer>()[0].bounds;
        foreach (var mesh in psu.GetComponentsInChildren<MeshRenderer>()) psuBounds.Encapsulate(mesh.bounds);
        var localPsu = enclosure.InverseTransformPoint(psuBounds.center); var hole = opening.localPosition;
        Check(Mathf.Abs(localPsu.y - hole.y) < opening.localScale.y * .5f && Mathf.Abs(localPsu.z - hole.z) < opening.localScale.z * .5f, "Installed PSU is aligned with rear opening");
        var rearMesh = enclosure.Find("Rear exhaust grille").GetComponent<MeshFilter>().sharedMesh;
        var vertices = rearMesh.vertices; var triangles = rearMesh.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            var triangleCentre = (vertices[triangles[i]] + vertices[triangles[i+1]] + vertices[triangles[i+2]]) / 3;
            if (Mathf.Abs(triangleCentre.y - hole.y) < opening.localScale.y * .5f && Mathf.Abs(triangleCentre.z - hole.z) < opening.localScale.z * .5f) throw new Exception("Sheet metal obstructs PSU opening");
        }
        Check(true, "PSU opening has no obstructing sheet-metal triangles");
        Check(UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>().GetComponentsInChildren<Light>().Count(l => l.name == "Workbench fill" || l.name == "Case interior fill") == 2, "Assembly has shadowless workbench and case fill lights");
        Check(GameObject.Find("Practice target indicator").GetComponent<LineRenderer>().sharedMaterial.shader.name == "TechWise/Visible Placement Marker", "Placement target uses through-case marker shader");
        panel.gameObject.SetActive(true);
        var board = state.FindPart("Motherboard").transform;
        TechWiseSimulationModeManager.SetMode("disassembly","practice"); yield return null; yield return null;
        Check(TechWiseDetailedAssemblyRuntime.Active,"Detailed mechanics remain active for disassembly");
        TechWiseSimulationModeManager.SetMode("assembly","tutorial");
        // Unity Learn's editor-only GUID registry retains the editor scene's copy in Play Mode.
        // Clear that lesson-authoring metadata before loading another scene with the same sample GUIDs.
        var guidType = AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Unity.Tutorials.Core.SceneObjectGuidManager")).FirstOrDefault(t=>t!=null);
        if(guidType!=null) { var manager=guidType.GetProperty("Instance").GetValue(null); ((IDictionary)guidType.GetField("m_Components",Hidden).GetValue(manager)).Clear(); }
        var loading = SceneManager.LoadSceneAsync("Singleplayer"); while(!loading.isDone) yield return null;
        for(int i=0;i<40;i++) yield return null;
        foreach(var interactor in UnityEngine.Object.FindObjectsByType<XRBaseInteractor>()) if(interactor is not XRSocketInteractor) interactor.enabled=false;
        var lesson=UnityEngine.Object.FindAnyObjectByType<TechWiseTutorialRuntime>();
        Check(lesson != null,"Existing controller tutorial remains available");
        Call(lesson,"BeginControls"); yield return null;
        Check((bool)typeof(TechWiseTutorialRuntime).GetProperty("ControlsPending",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null),"Controller training still locks assembly");
        Call(lesson,"SkipControls");
        for(int i=0;i<12;i++) yield return null;
        state=TechWiseSimulationRuntime.Instance; phase=TechWiseDetailedAssemblyRuntime.Instance;
        Check(phase.Ready && phase.Cover.Angle >=60 && !phase.PasteApplied,"Tutorial starts fresh detailed assembly after controls: Ready="+phase.Ready+" Error="+phase.SetupError+" Board="+(state.FindPart("Motherboard")!=null)+" CPU socket="+(state.FindSocket("CPU")!=null));
        Check(state.FindPart("CPU").gameObject.activeInHierarchy,"Tutorial CPU is visible and active after controls");
        Check((string)typeof(TechWiseTutorialRuntime).GetProperty("Heading",Hidden).GetValue(lesson) == phase.Heading,"Existing tutorial board displays detailed phase guidance");
        Check(state.Parts.Count(p=>state.StepOf(p)=="RAM")==4,"Tutorial preserves all four initially dormant RAM sticks");
        var tutorialHand=new GameObject("Left tutorial verification controller",typeof(SphereCollider),typeof(Rigidbody));
        tutorialHand.GetComponent<SphereCollider>().isTrigger=true; tutorialHand.GetComponent<Rigidbody>().isKinematic=true;
        hand=tutorialHand.AddComponent<XRDirectInteractor>();hand.interactionLayers=~0;
        yield return Install("CPU");
        Call(phase.Cover,"Observe",0f);Call(phase.Cover,"Commit");Call(phase.Lever,"Observe",0f);Call(phase.Lever,"Commit");
        cpu=state.FindPart("CPU");normal=state.FindPart("Motherboard").transform.forward;
        bounds=cpu.GetComponentsInChildren<Renderer>()[0].bounds;foreach(var r in cpu.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
        centre=bounds.center+normal*Vector3.Dot(bounds.extents,new Vector3(Mathf.Abs(normal.x),Mathf.Abs(normal.y),Mathf.Abs(normal.z)));
        for(int i=0;i<11;i++) Call(phase,"ApplyPaste",centre,-normal,true,.1f);
        yield return null;
        Check(state.FindPart("CPUCooler").gameObject.activeInHierarchy,"Tutorial reveals cooler through its authored tray hierarchy");
        yield return Install("CPUCooler");Tighten(phase.CoolerScrews,"CPUCooler");yield return null;
        for(int i=0;i<4;i++) yield return Install("RAM");
        yield return Install("M2");Call(phase.M2Hinge,"Observe",0f);Call(phase.M2Hinge,"Commit");yield return null;Tighten(phase.M2Screws,"M2");
        panel=(XRGrabInteractable)typeof(TechWiseDetailedAssemblyRuntime).GetField("panel",Hidden).GetValue(phase);
        panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel);panel.transform.position+=Vector3.right*.5f;
        panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel);yield return null;
        yield return Install("Motherboard");Tighten(phase.BoardScrews,"Motherboard");
        yield return Install("FanRear");yield return Install("FanFront1");yield return Install("FanFront2");
        yield return FinishRemainingComponents();
        Check(phase.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.Complete,"Tutorial completes the full PC assembly mechanics");
        yield return DisassemblyModes();
        yield return CompetitionAssembly();
        lesson.enabled=false;phase.enabled=false;
        yield return null;yield return null;
        Check(!phase.Ready,"Tutorial and phase teardown release their runtime objects");
        Check(errors.Count == 0,"No Phase 1 runtime exceptions; known framework diagnostics reported separately");
    }
    static IEnumerator FinishRemainingComponents()
    {
        foreach (var id in new[]{"GPUConnector","GPU","Storage","PSU"})
        {
            yield return null;
            Check(state.FindPart(id) != null, id + " is included in the full assembly lesson");
            Check(phase.CurrentPartStep == id, "Guide highlights " + id + " as the next objective");
            Check(!phase.Heading.Contains("complete"), "No premature completion before " + id);
            yield return Install(id);
            if(TechWiseBuildDefinition.Find(id).screws>0) Tighten(phase.Fasteners.Where(f=>f.ComponentId==id).ToList(),id);
        }
        Check(phase.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.CloseCase,"Full build waits for side panel");
        var panel=(XRGrabInteractable)typeof(TechWiseDetailedAssemblyRuntime).GetField("panel",Hidden).GetValue(phase);
        var panelState=panel.GetComponent<TechWiseCasePanel>();var home=(Transform)typeof(TechWiseCasePanel).GetField("home",Hidden).GetValue(panelState);
        panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel);panel.transform.SetPositionAndRotation(home.position,home.rotation);
        panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel);yield return null;yield return null;
        Check(phase.PanelInstalled,"Correct release reattaches side panel: phase="+phase.CurrentPhase+" distance="+Vector3.Distance(panel.transform.position,home.position)+" angle="+Quaternion.Angle(panel.transform.rotation,home.rotation)+" held="+panel.isSelected);
        Check(state.CaptureAssessmentState().All(r=>r.complete),"All atomic requirements validate");
        Check(phase.CurrentPhase == TechWiseDetailedAssemblyRuntime.Phase.Complete,"Complete only after the final power supply is seated");
        Check(phase.Heading == "PC assembly complete", "Completion no longer says Phase 1");
    }
    static IEnumerator DisassemblyModes()
    {
        foreach(string mode in new[]{"practice","tutorial","competition"})
        {
            TechWiseSimulationModeManager.SetMode("disassembly",mode);
            var guidType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Unity.Tutorials.Core.SceneObjectGuidManager")).FirstOrDefault(t=>t!=null);
            if(guidType!=null){var manager=guidType.GetProperty("Instance").GetValue(null);((IDictionary)guidType.GetField("m_Components",Hidden).GetValue(manager)).Clear();}
            var loading=SceneManager.LoadSceneAsync(mode=="tutorial"?"Singleplayer":"Multiplayer");while(!loading.isDone)yield return null;
            for(int i=0;i<65;i++)yield return null;
            state=TechWiseSimulationRuntime.Instance;phase=TechWiseDetailedAssemblyRuntime.Instance;
            Check(phase.Ready&&!TechWiseDisassemblyRuntime.IsPreparing&&TechWiseDisassemblyRuntime.LastSkippedSteps.Length==0,mode+" disassembly prepares every required component");
            if(mode=="tutorial") {var lesson=UnityEngine.Object.FindAnyObjectByType<TechWiseTutorialRuntime>();Call(lesson,"BeginControls");Call(lesson,"SkipControls");yield return null;}
            if(mode=="competition") UnityEngine.Object.FindAnyObjectByType<TechWiseAttemptRecorder>().StartCompetitionAttempt();
            foreach(var interactor in UnityEngine.Object.FindObjectsByType<XRBaseInteractor>()) if(interactor is not XRSocketInteractor)interactor.enabled=false;
            var handObject=new GameObject("Right disassembly verification controller",typeof(SphereCollider),typeof(Rigidbody));handObject.GetComponent<SphereCollider>().isTrigger=true;handObject.GetComponent<Rigidbody>().isKinematic=true;hand=handObject.AddComponent<XRDirectInteractor>();hand.interactionLayers=~0;
            Check(phase.Fasteners.Count()==36 && phase.Fasteners.All(f=>f.Complete),mode+" assembled seed has all individually tracked screws");
            var panel=(XRGrabInteractable)typeof(TechWiseDetailedAssemblyRuntime).GetField("panel",Hidden).GetValue(phase);
            panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel);panel.transform.position+=Vector3.right*.6f;
            Check(!state.CaptureAssessmentState().First(r=>r.component_id=="Case/panel").complete,"Held panel cannot complete removal");
            Check(phase.Instruction.Contains("Open the case"),"Disassembly keeps panel objective until it is released clear");
            panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel);yield return null;yield return null;
            Check(!phase.PanelInstalled&&!panel.GetComponent<Rigidbody>().isKinematic&&panel.GetComponent<Rigidbody>().useGravity,"Detached panel uses gravity");
            foreach(string id in TechWiseBuildDefinition.DisassemblyOrder)
            {
                var screws=phase.Fasteners.Where(f=>f.ComponentId==id).ToArray();
                if(screws.Length>0)Check(!(bool)Call(phase,"CanGrab",id),id+" locked while screws secured");
                foreach(var screw in screws)
                {
                    Vector3 Head()=>(Vector3)typeof(TechWiseFastener).GetProperty("HeadPosition",Hidden).GetValue(screw);
                    for(int i=0;i<30;i++)Call(screw,"Advance",Head(),screw.transform.forward,true,.1f);
                    Check(screw.Progress==0&&screw.Inserted,screw.Id+" loosened before removal");
                    Check(screw.Grab.interactionManager.IsSelectPossible((IXRSelectInteractor)hand,screw.Grab),screw.Id+" loosened screw can be gripped");
                    screw.Grab.interactionManager.SelectEnter((IXRSelectInteractor)hand,screw.Grab);screw.transform.position+=Vector3.up*.15f+Vector3.right*.1f;
                    screw.Grab.interactionManager.SelectExit((IXRSelectInteractor)hand,screw.Grab);Call(screw,"LateUpdate");Check(!screw.Inserted,screw.Id+" manually removed");
                }
                if(id=="M2") {Call(phase.M2Hinge,"Observe",25f);Call(phase.M2Hinge,"Commit");}
                if(id=="CPU") {Call(phase.Lever,"Observe",phase.Lever.maximumAngle);Call(phase.Lever,"Commit");Call(phase.Cover,"Observe",phase.Cover.maximumAngle);Call(phase.Cover,"Commit");}
                foreach(var part in state.Parts.Where(p=>state.StepOf(p)==id).ToArray())
                {
                    Check(part.interactionManager.IsSelectPossible((IXRSelectInteractor)hand,part),id+" removable only after prerequisites");
                    part.interactionManager.SelectEnter((IXRSelectInteractor)hand,part);part.transform.position+=Vector3.up*.35f+Vector3.right*.4f;
                    part.interactionManager.SelectExit((IXRSelectInteractor)hand,part);yield return null;
                }
                Check(state.IsStepComplete(id),mode+" correctly completes removal of "+id);
            }
            Check(state.CaptureAssessmentState().All(r=>r.complete),mode+" complete disassembly validates every atomic requirement");
        }
    }
    static IEnumerator CompetitionScene()
    {
        TechWiseSimulationModeManager.SetMode("assembly","competition");
        var guidType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Unity.Tutorials.Core.SceneObjectGuidManager")).FirstOrDefault(t=>t!=null);
        if(guidType!=null){var manager=guidType.GetProperty("Instance").GetValue(null);((IDictionary)guidType.GetField("m_Components",Hidden).GetValue(manager)).Clear();}
        var loading=SceneManager.LoadSceneAsync("Multiplayer");while(!loading.isDone)yield return null;
        for(int i=0;i<60;i++)yield return null;
        state=TechWiseSimulationRuntime.Instance;phase=TechWiseDetailedAssemblyRuntime.Instance;
        foreach(var interactor in UnityEngine.Object.FindObjectsByType<XRBaseInteractor>())if(interactor is not XRSocketInteractor)interactor.enabled=false;
        var handObject=new GameObject("Left competition verification controller",typeof(SphereCollider),typeof(Rigidbody));handObject.GetComponent<SphereCollider>().isTrigger=true;handObject.GetComponent<Rigidbody>().isKinematic=true;hand=handObject.AddComponent<XRDirectInteractor>();hand.interactionLayers=~0;
        UnityEngine.Object.FindAnyObjectByType<TechWiseAttemptRecorder>().StartCompetitionAttempt();
    }
    static IEnumerator SeatPart(string id)
    {
        var part=state.Parts.First(p=>state.StepOf(p)==id&&!state.IsPartInstalled(p));
        var socket=state.Sockets.First(s=>state.StepOf(s)==id&&!s.hasSelection&&TechWiseSimulationRuntime.Matches(s,part.transform));
        Check(part.interactionManager.IsSelectPossible((IXRSelectInteractor)hand,part),"Competition can grab "+id);
        part.interactionManager.SelectEnter((IXRSelectInteractor)hand,part);Align(part,socket);
        part.interactionManager.SelectExit((IXRSelectInteractor)hand,part);
        for(int i=0;i<4;i++)yield return null;
        Check(state.IsPartInstalled(part),"Competition released "+id+" snaps correctly");
    }
    static void CloseCpuAndPaste()
    {
        Call(phase.Cover,"Observe",0f);Call(phase.Cover,"Commit");Call(phase.Lever,"Observe",0f);Call(phase.Lever,"Commit");
        var cpu=state.FindPart("CPU");var normal=state.FindPart("Motherboard").transform.forward;
        var bounds=cpu.GetComponentsInChildren<Renderer>()[0].bounds;foreach(var r in cpu.GetComponentsInChildren<Renderer>())if(r.GetComponent<TMPro.TMP_Text>()==null)bounds.Encapsulate(r.bounds);
        var centre=bounds.center+normal*Vector3.Dot(bounds.extents,new Vector3(Mathf.Abs(normal.x),Mathf.Abs(normal.y),Mathf.Abs(normal.z)));
        for(int i=0;i<11;i++)Call(phase,"ApplyPaste",centre,-normal,true,.1f);
        Check(phase.PasteApplied,"Competition requires explicit paste contact");
    }
    static IEnumerator CompetitionAssembly()
    {
        yield return CompetitionScene();
        var recorder=UnityEngine.Object.FindAnyObjectByType<TechWiseAttemptRecorder>();
        Check(phase.Ready&&!state.ManipulationLocked&&phase.Fasteners.All(f=>!f.Inserted),"Competition starts with all 36 screws loose");
        Check(recorder.GetLiveMetrics().score<100,"Empty build cannot score 100");
        Check(TechWiseControlLabels.Grab.Contains("Grip")&&TechWiseControlLabels.Ui.Contains("Trigger"),"Displayed controller instructions resolve actual active bindings");
        foreach(var turn in UnityEngine.Object.FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.ContinuousTurnProvider>())
            Check(turn.turnSpeed==60&&!turn.enableTurnAround,"Smooth turn is 60 degrees/sec with 180-degree snap disabled");
        var hud=GameObject.Find("TechWise Competition HUD");var pose=hud.transform.position+Vector3.right*.2f;hud.transform.position=pose;yield return null;yield return null;
        Check(Vector3.Distance(hud.transform.position,pose)<.001f,"Competition panel stays where placed");
        var monitorDisplay=GameObject.Find("Verification display");
        var scroll=monitorDisplay.GetComponentsInChildren<UnityEngine.UI.ScrollRect>().Single();
        Check(scroll!=null,"Monitor instructions/results have a scroll viewport");
        var monitorBase=GameObject.Find("Monitor base");var baseCollider=monitorBase.GetComponent<Collider>();
        float tableY=(float)typeof(TechWiseDetailedAssemblyRuntime).Assembly.GetType("TechWiseWorkbenchSurface").GetMethod("Height",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{monitorBase.transform.position,monitorBase.transform.position.y});
        Check(baseCollider!=null && Mathf.Abs(baseCollider.bounds.min.y-tableY)<.002f,"Monitor base contacts tabletop and retains solid collider");
        var cpu=state.FindPart("CPU");var target=state.FindSocket("CPU");var mistakes=(List<TechWiseVrMistakeDetail>)typeof(TechWiseAttemptRecorder).GetField("mistakeDetails",Hidden).GetValue(recorder);
        cpu.interactionManager.SelectEnter((IXRSelectInteractor)hand,cpu);Align(cpu,target);cpu.transform.rotation=Quaternion.AngleAxis(90,target.transform.up)*cpu.transform.rotation;
        cpu.interactionManager.SelectExit((IXRSelectInteractor)hand,cpu);for(int i=0;i<6;i++)yield return null;
        Check(!state.IsPartInstalled(cpu)&&mistakes.Count==1&&mistakes[0].kind=="incorrect_orientation","One backwards release records one orientation mistake");
        yield return SeatPart("CPU");recorder.ResetToPreviousPoint();yield return null;
        Check(!state.IsPartInstalled(cpu)&&mistakes.Last().kind=="reset_previous"&&mistakes.Last().score_penalty==3,"Previous reset restores CPU before placement and applies configured penalty");
        yield return SeatPart("CPU");CloseCpuAndPaste();yield return SeatPart("CPUCooler");
        var first=phase.CoolerScrews[0];var other=phase.CoolerScrews[1];
        first.Grab.interactionManager.SelectEnter((IXRSelectInteractor)hand,first.Grab);first.transform.SetPositionAndRotation(other.Hole.transform.position,other.Hole.transform.rotation);first.Grab.interactionManager.SelectExit((IXRSelectInteractor)hand,first.Grab);Call(first,"LateUpdate");
        Check(!first.Inserted&&mistakes.Last().kind=="wrong_screw_hole","Wrong numbered hole rejects screw and records one error");
        first.Grab.interactionManager.SelectEnter((IXRSelectInteractor)hand,first.Grab);first.transform.SetPositionAndRotation(first.Hole.transform.position,first.Hole.transform.rotation);first.Grab.interactionManager.SelectExit((IXRSelectInteractor)hand,first.Grab);Call(first,"LateUpdate");
        Vector3 head=(Vector3)typeof(TechWiseFastener).GetProperty("HeadPosition",Hidden).GetValue(first);
        for(int i=0;i<30;i++)Call(first,"Advance",head,first.transform.forward,true,.1f);
        Check(first.Complete,"First screw manually tightened");recorder.ResetToPreviousPoint();yield return null;
        Check(first.Inserted&&first.Progress==0,"Previous reset restores inserted-but-untightened screw");
        recorder.ResetToPreviousPoint();yield return null;Check(!first.Inserted&&first.Progress==0,"Previous reset restores loose screw before insertion");
        Call(recorder,"FinishAttempt");var frozen=(TechWiseVrComponentResult[])typeof(TechWiseAttemptRecorder).GetProperty("SubmittedResults",Hidden).GetValue(recorder);int submitted=recorder.GetLiveMetrics().score;
        Check(recorder.IsFinished&&submitted<100&&frozen.Length==91&&frozen.Any(r=>!r.complete),"Done on partial build validates full denominator and cannot award 100");
        Call(recorder,"FinishAttempt");Check(recorder.GetLiveMetrics().score==submitted,"Duplicate Done preserves immutable score");
        var guidType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Unity.Tutorials.Core.SceneObjectGuidManager")).FirstOrDefault(t=>t!=null);
        if(guidType!=null){var manager=guidType.GetProperty("Instance").GetValue(null);((IDictionary)guidType.GetField("m_Components",Hidden).GetValue(manager)).Clear();}
        recorder.ResetToBeginning();for(int i=0;i<75;i++)yield return null;
        state=TechWiseSimulationRuntime.Instance;phase=TechWiseDetailedAssemblyRuntime.Instance;recorder=UnityEngine.Object.FindAnyObjectByType<TechWiseAttemptRecorder>();
        Check(phase.Ready&&!phase.PasteApplied&&phase.Fasteners.Count()==36&&phase.Fasteners.All(f=>!f.Inserted),"Reset Beginning restores all loose screws and clears paste without duplicates");
        Check(recorder.GetLiveMetrics().mistakes==0,"Reset Beginning starts a new attempt without direct penalty");
        foreach(var interactor in UnityEngine.Object.FindObjectsByType<XRBaseInteractor>())if(interactor is not XRSocketInteractor)interactor.enabled=false;
        var handObject=new GameObject("Right competition verification controller",typeof(SphereCollider),typeof(Rigidbody));handObject.GetComponent<SphereCollider>().isTrigger=true;handObject.GetComponent<Rigidbody>().isKinematic=true;hand=handObject.AddComponent<XRDirectInteractor>();hand.interactionLayers=~0;recorder.StartCompetitionAttempt();
        yield return SeatPart("CPU");CloseCpuAndPaste();yield return SeatPart("CPUCooler");Tighten(phase.CoolerScrews,"CPUCooler");
        for(int i=0;i<4;i++)yield return SeatPart("RAM");yield return SeatPart("M2");Call(phase.M2Hinge,"Observe",0f);Call(phase.M2Hinge,"Commit");Tighten(phase.M2Screws,"M2");
        var panel=(XRGrabInteractable)typeof(TechWiseDetailedAssemblyRuntime).GetField("panel",Hidden).GetValue(phase);panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel);panel.transform.position+=Vector3.right*.6f;
            Check(!state.CaptureAssessmentState().First(r=>r.component_id=="Case/panel").complete,"Held panel cannot complete removal");
            Check(phase.Instruction.Contains("Open the case"),"Disassembly keeps panel objective until it is released clear");
            panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel);yield return null;yield return null;
        foreach(string id in new[]{"Motherboard","FanRear","FanFront1","FanFront2","GPUConnector","GPU","Storage","PSU"})
        {yield return SeatPart(id);if(TechWiseBuildDefinition.Find(id).screws>0)Tighten(phase.Fasteners.Where(f=>f.ComponentId==id).ToList(),id);}
        var panelState=panel.GetComponent<TechWiseCasePanel>();var home=(Transform)typeof(TechWiseCasePanel).GetField("home",Hidden).GetValue(panelState);panel.interactionManager.SelectEnter((IXRSelectInteractor)hand,panel);panel.transform.SetPositionAndRotation(home.position,home.rotation);panel.interactionManager.SelectExit((IXRSelectInteractor)hand,panel);yield return null;yield return null;
        Check(state.CaptureAssessmentState().All(r=>r.complete),"Competition complete build validates all requirements");
        var one=phase.CoolerScrews[0];Call(one,"ResetProgress");Check(state.CaptureAssessmentState().Any(r=>!r.complete)&&recorder.GetLiveMetrics().score<100,"One untightened screw prevents a perfect score");head=(Vector3)typeof(TechWiseFastener).GetProperty("HeadPosition",Hidden).GetValue(one);for(int i=0;i<30;i++)Call(one,"Advance",head,one.transform.forward,true,.1f);
        typeof(TechWiseDetailedAssemblyRuntime).GetProperty("PasteApplied").SetValue(phase,false);Check(recorder.GetLiveMetrics().score<100,"Missing thermal paste prevents a perfect score");typeof(TechWiseDetailedAssemblyRuntime).GetProperty("PasteApplied").SetValue(phase,true);
        Call(recorder,"FinishAttempt");Check(recorder.IsFinished&&recorder.GetLiveMetrics().score==100,"Complete accurate fresh competition attempt receives 100: score="+recorder.GetLiveMetrics().score+" mistakes="+recorder.GetLiveMetrics().mistakes+" issues="+string.Join(" | ",state.CaptureAssessmentState().Where(r=>!r.complete).Select(r=>r.component+": "+r.explanation))+" penalties="+string.Join(" | ",((List<TechWiseVrMistakeDetail>)typeof(TechWiseAttemptRecorder).GetField("mistakeDetails",Hidden).GetValue(recorder)).Select(m=>m.kind+":"+m.attempted_step+":"+m.explanation)));
    }
    static void CaptureLabels()
    {
        var camera = Camera.main; var position = camera.transform.position; var rotation = camera.transform.rotation; var fov = camera.fieldOfView;
        var parts = state.Parts.Where(p => p.gameObject.activeInHierarchy).ToArray(); var center = Vector3.zero;
        foreach(var part in parts) center += part.transform.position; center /= parts.Length;
        camera.transform.position = center + Vector3.back * 1.7f + Vector3.up * .8f; camera.transform.LookAt(center); camera.fieldOfView = 65;
        Call(UnityEngine.Object.FindAnyObjectByType<TechWiseAssemblyPresentation>(), "LateUpdate"); Canvas.ForceUpdateCanvases();
        var target = new RenderTexture(1400,900,24); var previousTarget = camera.targetTexture; camera.targetTexture = target; camera.Render();
        var previous = RenderTexture.active; RenderTexture.active = target; var picture = new Texture2D(1400,900,TextureFormat.RGB24,false);
        picture.ReadPixels(new Rect(0,0,1400,900),0,0); picture.Apply(); File.WriteAllBytes("Logs/CompleteBuildAudit/component-labels.png",picture.EncodeToPNG());
        RenderTexture.active = previous; camera.targetTexture = previousTarget; camera.fieldOfView = fov; camera.transform.SetPositionAndRotation(position,rotation);
        UnityEngine.Object.Destroy(picture); UnityEngine.Object.Destroy(target);
    }
    static void Capture(string name, Transform root, Vector3 direction)
    {
        var renderers = root.GetComponentsInChildren<MeshRenderer>().Where(r=>r.enabled && r.GetComponent<TMPro.TMP_Text>() == null).ToList();
        renderers.AddRange(state.Parts.Where(p=>p != null && state.IsPartInstalled(p)).SelectMany(p=>p.GetComponentsInChildren<MeshRenderer>()).Where(r=>r.enabled && r.GetComponent<TMPro.TMP_Text>() == null));
        renderers.AddRange(phase.Fasteners.Where(f=>f.Inserted).SelectMany(f=>f.GetComponentsInChildren<MeshRenderer>()).Where(r=>r.GetComponent<TMPro.TMP_Text>()==null));
        renderers = renderers.Distinct().ToList();
        var layers = renderers.ToDictionary(r=>r.gameObject,r=>r.gameObject.layer); foreach(var r in renderers) r.gameObject.layer=31;
        var bounds=renderers[0].bounds; foreach(var r in renderers) bounds.Encapsulate(r.bounds);
        var obj=new GameObject("Phase 1 QA camera"); var camera=obj.AddComponent<Camera>(); camera.cullingMask=1<<31; camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.15f,.18f);camera.nearClipPlane=.01f;camera.fieldOfView=40;
        camera.transform.position=bounds.center+direction.normalized*bounds.size.magnitude*1.2f;camera.transform.LookAt(bounds.center);
        var rt=new RenderTexture(1200,1000,24);camera.targetTexture=rt;camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;
        var tex=new Texture2D(1200,1000,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1200,1000),0,0);tex.Apply();File.WriteAllBytes("Logs/CompleteBuildAudit/"+name,tex.EncodeToPNG());
        RenderTexture.active=previous;camera.targetTexture=null;foreach(var pair in layers) pair.Key.layer=pair.Value;
        UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(obj);
    }
}
