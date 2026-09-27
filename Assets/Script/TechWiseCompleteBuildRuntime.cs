using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public sealed partial class TechWiseDetailedAssemblyRuntime
{
    internal readonly List<TechWiseFastener> ExtraScrews = new();
    public IEnumerable<TechWiseFastener> Fasteners => CoolerScrews.Concat(M2Screws).Concat(BoardScrews).Concat(ExtraScrews);
    internal Material MarkerMaterial { get { if (hardwareMarker == null) hardwareMarker = new Material(Resources.Load<Shader>("TechWisePlacementMarker")) { color = new Color(.2f,1,.6f) }; return hardwareMarker; } }
    Material hardwareMarker;
    TechWiseCasePanel casePanel;
    readonly Dictionary<GameObject, bool> conditionalObjects = new();
    public bool PanelInstalled => casePanel != null && casePanel.Installed;
    internal bool CaseAccessOpen => casePanel!=null && casePanel.AccessOpen;
    internal XRGrabInteractable SidePanel => panel;
    internal bool IsConditionallyHidden(Transform item) => conditionalObjects.TryGetValue(item.gameObject,out var hidden) && hidden;
    internal bool BuildComplete => Ready && TechWiseBuildDefinition.Components.All(c => Seated(c.id) && StepFastened(c.id)) && PanelInstalled;
    internal bool RemovalComplete => Ready && CaseAccessOpen && TechWiseBuildDefinition.Components.All(c => state.IsStepComplete(c.id)) && Fasteners.All(f => f.Removed) && !Cover.Closed && !Lever.Closed;
    internal string DisassemblyStep => !Ready ? null : TechWiseBuildDefinition.DisassemblyOrder.FirstOrDefault(id => !state.IsStepComplete(id));
    internal TechWiseFastener CurrentFastener
    {
        get
        {
            if (!Ready || CurrentPartStep == null || !Seated(CurrentPartStep) || TechWiseSimulationModeManager.IsDisassembly && !CaseAccessOpen) return null;
            if (CurrentPartStep == "M2" && !M2Lowered && TechWiseSimulationModeManager.IsAssembly) return null;
            return Fasteners.FirstOrDefault(f => f.ComponentId == CurrentPartStep &&
                (TechWiseSimulationModeManager.IsDisassembly ? !f.Removed : !f.Complete));
        }
    }
    void ClearCompleteBuild()
    {
        ExtraScrews.Clear(); conditionalObjects.Clear(); casePanel = null;
        if (hardwareMarker != null) Destroy(hardwareMarker); hardwareMarker = null;
        TechWiseAssemblyHistory.Clear();
    }
    void CreateAdditionalFasteners()
    {
        // Fan mounting screws removed: case fans snap directly into their mounting positions without screws.
        // Audited in the retained FBX's component-local coordinates (not overall renderer bounds).
        // GPU pCube1167: the bent retaining flange, +Y face. PSU pCube927: rear plate, +X face.
        // SSD: centres of the four modeled circular holes on its +Z mounting face.
        AddMounts("GPU",Vector3.up,new[]{new Vector3(.0395f,.01703615f,.0038f),new Vector3(.0395f,.01703615f,-.0045f)});
        // SATA SSD mounting screws removed: seats directly into case mount without screws.
        AddMounts("PSU",Vector3.right,new[]{new Vector3(.01861755f,-.009f,-.017f),new Vector3(.01861755f,-.009f,.017f),new Vector3(.01861755f,.009f,-.017f),new Vector3(.01861755f,.009f,.017f)});
    }
    void AddMounts(string id,Vector3 normal,Vector3[] points)
    {
        var part=state.FindPart(id);if(part==null)throw new InvalidOperationException("Missing required component: "+id);
        foreach(var point in points) ExtraScrews.Add(Screw(part.transform,part.transform.TransformPoint(point),part.transform.TransformDirection(normal),id+" mounting screw",()=>Seated(id)));
    }
    internal bool GuideActionFocus(out Transform item,out Transform target)
    {
        item=target=null;
        bool removing=TechWiseSimulationModeManager.IsDisassembly;
        if(removing && !CaseAccessOpen || !removing && (CurrentPhase==Phase.PrepareCase || CurrentPhase==Phase.CloseCase)) item=panel.transform;
        else if(!removing && CurrentPhase==Phase.ApplyThermalPaste)
        {
            item=TechWiseAssemblyTool.Tools.FirstOrDefault(t=>t!=null&&t.kind==TechWiseAssemblyTool.ToolKind.ThermalPaste)?.transform;
            var cpu=state.FindPart("CPU");target=state.FindSocket("CPU")?.GetAttachTransform(cpu);
        }
        else if(!removing && CurrentPhase==Phase.CloseAndLockSocket || removing && CurrentPartStep=="CPU" && (Cover.Closed||Lever.Closed))
            item=(removing?Lever.Closed?Lever:Cover:Cover.Closed?Lever:Cover).transform;
        else if(CurrentPartStep=="M2" && Seated("M2") && (removing?M2Lowered && M2Screws.All(f=>f.Removed):!M2Lowered)) item=M2Hinge!=null?M2Hinge.transform:null;
        if(target==null) target=item;
        return item!=null;
    }
    void InitializeManualScrews()
    {
        var groups = new Dictionary<string,int>();
        var inventory = board.position + Vector3.right*.65f + Vector3.back*.20f;
        inventory.y = TechWiseWorkbenchSurface.Height(inventory,board.position.y)+.025f;
        int index=0;
        foreach(var screw in Fasteners)
        {
            string component = CoolerScrews.Contains(screw)?"CPUCooler":M2Screws.Contains(screw)?"M2":BoardScrews.Contains(screw)?"Motherboard":screw.name.Split(' ')[0];
            int ordinal=groups.TryGetValue(component,out var n)?n:0; groups[component]=ordinal+1;
            var parent=screw.transform.parent; var pose=new Pose(screw.transform.position,screw.transform.rotation);
            var home=new Pose(inventory+new Vector3((index%9-4)*.043f,0,(index/9-1.5f)*.045f),Quaternion.Euler(90,0,0));
            screw.Initialize(component,TechWiseBuildDefinition.HoleId(component,ordinal),parent,pose,home); index++;
            Label(screw.transform,TechWiseSimulationRuntime.Label(component)+" screw "+(ordinal+1),.02f,
                ()=>highlightedFastener==screw && !TechWiseSimulationRuntime.IsHeld(screw.Grab) && !screw.Inserted && screw.gameObject.activeInHierarchy);
        }
        // A shallow physical tray keeps the individually grabbable hardware separated.
        var tray=Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); tray.name="Organized screw tray";
        tray.transform.position=inventory+Vector3.down*.02f; tray.transform.localScale=new Vector3(.43f,.012f,.22f);
        tray.GetComponent<Renderer>().sharedMaterial=assets.metal;
        foreach(var definition in TechWiseBuildDefinition.Components)
            if(Fasteners.Count(f=>f.ComponentId==definition.id)!=definition.screws)
                throw new InvalidOperationException("Screw count mismatch: "+definition.id);
    }
    void InitializePanelAndWorkspace()
    {
        casePanel=panel.gameObject.AddComponent<TechWiseCasePanel>();
        casePanel.Initialize(this,panel);
        TechWiseAssemblyHistory.Clear();
        UpdateAvailability();
    }
    void UpdateCompleteBuild()
    {
        UpdateAvailability();
        if(TechWiseSimulationModeManager.IsDisassembly && casePanel!=null && !casePanel.Installed) CasePrepared=true;
    }
    void UpdateAvailability()
    {
        if(!Ready) return;
        bool isFanPhase = CurrentPhase is TechWiseDetailedAssemblyRuntime.Phase.InstallRearFan or TechWiseDetailedAssemblyRuntime.Phase.InstallFrontFans;
        foreach(var id in new[]{"FanRear","FanFront1","FanFront2"})
        {
            var part=state.FindPart(id); if(part==null) continue;
            bool show=!TechWiseTutorialRuntime.InTutorial || TechWiseSimulationModeManager.IsDisassembly ||
                !TechWiseTutorialRuntime.ControlsPending && (isFanPhase || CurrentPartStep==id || Seated(id));
            conditionalObjects[part.gameObject]=!show;
            if(part.gameObject.activeSelf!=show) part.gameObject.SetActive(show);
        }
        foreach(var screw in Fasteners)
        {
            bool show=!TechWiseTutorialRuntime.InTutorial || TechWiseSimulationModeManager.IsDisassembly ||
                !TechWiseTutorialRuntime.ControlsPending && (screw.Inserted || CurrentPartStep==screw.ComponentId && Seated(screw.ComponentId));
            conditionalObjects[screw.gameObject]=!show;
            if(screw.gameObject.activeSelf!=show) screw.gameObject.SetActive(show);
        }
    }
    internal bool CanRemove(string id)
    {
        if(!Ready || !CaseAccessOpen || id!=DisassemblyStep) return false;
        if(Fasteners.Any(f=>f.ComponentId==id && !f.Removed)) return false;
        if(id=="M2" && M2Lowered) return false;
        if(id=="CPU" && (Cover.Closed || Lever.Closed)) return false;
        return true;
    }
    internal void PrepareDisassemblyHardware()
    {
        if(!m2HandleMade) CreateM2Handle();
        RestoreMechanics(true,true,true,0,0,0);
        foreach(var screw in Fasteners) screw.Seat(1);
        casePanel.Restore(true,new Pose(panel.transform.position,panel.transform.rotation));
        TechWiseAssemblyHistory.Clear();
    }
    internal void RestoreMechanics(bool paste,bool prepared,bool lowered,float cover,float lever,float m2)
    {
        PasteApplied=paste; pasteProgress=paste?1:0; CasePrepared=prepared; M2Lowered=lowered;
        Cover.RestoreAngle(cover); Lever.RestoreAngle(lever); SetM2Angle(m2);
        if(M2Hinge!=null) M2Hinge.RestoreAngle(m2);
        if(paste && blob==null)
        {
            var cpu=state.FindPart("CPU"); var mesh=cpu.GetComponentsInChildren<Renderer>().First();
            blob=Own(new GameObject("Visible thermal paste on CPU",typeof(MeshFilter),typeof(MeshRenderer)));
            blob.transform.SetParent(cpu.transform,true); blob.transform.position=mesh.bounds.center+board.forward*.006f;
            blob.transform.rotation=Quaternion.FromToRotation(Vector3.up,board.forward); SetWorldScale(blob.transform,Vector3.one);
            blob.GetComponent<MeshFilter>().sharedMesh=assets.pasteBlob; blob.GetComponent<MeshRenderer>().sharedMaterial=assets.paste;
        }
        if(blob!=null) blob.SetActive(paste);
    }
    internal void RestorePanel(bool installed,Pose pose) => casePanel.Restore(installed,pose);
    internal string BuildHeading => TechWiseSimulationModeManager.IsDisassembly
        ? RemovalComplete?"PC disassembly complete":"PC disassembly | "+(Array.IndexOf(TechWiseBuildDefinition.DisassemblyOrder,DisassemblyStep)+1)+" / "+TechWiseBuildDefinition.Components.Length
        : BuildComplete?"PC assembly complete":"PC assembly | "+GuideStep+" / 18";
    public string Instruction => TechWiseBuildLesson.Describe(this,state);
}

internal static class TechWiseWorkbenchSurface
{
    internal static float Height(Vector3 point,float fallback)
    {
        var hits=Physics.RaycastAll(point+Vector3.up*2,Vector3.down,5);
        foreach(var hit in hits.OrderBy(h=>h.distance))
            for(var t=hit.collider.transform;t!=null;t=t.parent)
                if(t.name=="Table Work Station") return hit.point.y;
        var table=GameObject.Find("Table Work Station");
        if(table!=null)
        {
            var surfaces=table.GetComponentsInChildren<Renderer>().Where(r=>r.bounds.size.x>.3f && r.bounds.size.z>.3f).ToArray();
            if(surfaces.Length>0) return surfaces.Max(r=>r.bounds.max.y);
        }
        return fallback-.04f;
    }
}
