using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Content.Interaction;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>Scene-local snapshots of completed manual actions, never a second assembly model.</summary>
internal static class TechWiseAssemblyHistory
{
    internal static bool Restoring { get; private set; }
    static readonly List<Snapshot> history=new();
    internal static int Count=>history.Count;
    internal sealed class Snapshot
    {
        internal readonly List<PartState> parts=new();
        internal readonly List<ScrewState> screws=new();
        internal readonly List<(Transform target,Pose pose)> tools=new();
        internal string[] performed;
        internal XRGrabInteractable[] touched;
        internal bool paste,prepared,lowered,panelInstalled;
        internal float cover,lever,m2;
        internal Pose panel;
    }
    internal sealed class PartState
    {
        internal XRGrabInteractable part;
        internal XRLockSocketInteractor socket;
        internal XRLockSocketInteractor raisedRamSocket;
        internal Pose pose;
        internal bool active,kinematic,gravity;
        internal readonly Dictionary<Collider,bool> triggers=new();
    }
    internal sealed class ScrewState { internal TechWiseFastener screw; internal bool inserted,active; internal float progress; internal Pose pose; }
    internal static Snapshot Capture()
    {
        var runtime=TechWiseDetailedAssemblyRuntime.Instance; var state=TechWiseSimulationRuntime.Instance;
        if(Restoring || runtime==null || !runtime.Ready || state==null || TechWiseDisassemblyRuntime.IsPreparing ||
            !TechWiseSimulationModeManager.IsCompetitionMode || state.ManipulationLocked) return null;
        var value=new Snapshot { paste=runtime.PasteApplied, prepared=runtime.CasePrepared,lowered=runtime.M2Lowered,
            cover=runtime.Cover.Angle,lever=runtime.Lever.Angle,m2=runtime.M2Lowered?0:25,panelInstalled=runtime.PanelInstalled,
            panel=new Pose(runtime.SidePanel.transform.position,runtime.SidePanel.transform.rotation),performed=state.CompletedActions(),touched=state.TouchedParts() };
        foreach(var part in state.Parts)
        {
            if(part==null) continue; var body=part.GetComponent<Rigidbody>();
            var saved=new PartState { part=part,socket=state.Sockets.Find(s=>s!=null&&s.IsSelecting(part)),
                raisedRamSocket=part.GetComponent<TechWiseRamInsertion>()?.Socket,
                pose=new Pose(part.transform.position,part.transform.rotation),active=part.gameObject.activeSelf,kinematic=body==null||body.isKinematic,gravity=body!=null&&body.useGravity };
            foreach(var c in part.colliders) if(c!=null) saved.triggers[c]=c.isTrigger;
            value.parts.Add(saved);
        }
        foreach(var screw in runtime.Fasteners) value.screws.Add(new ScrewState { screw=screw, inserted=screw.Inserted, progress=screw.Progress,pose=new Pose(screw.transform.position,screw.transform.rotation),active=screw.gameObject.activeSelf });
        foreach(var tool in TechWiseAssemblyTool.Tools) if(tool!=null) value.tools.Add((tool.transform,new Pose(tool.transform.position,tool.transform.rotation)));
        return value;
    }
    internal static void Commit(Snapshot value) { if(value==null||Restoring) return; history.Add(value); if(history.Count>256) history.RemoveAt(0); }
    internal static void Clear() { history.Clear(); Restoring=false; }
    internal static bool Undo()
    {
        if(history.Count==0) return false;
        var value=history[history.Count-1]; history.RemoveAt(history.Count-1); Restore(value); return true;
    }
    internal static void Restore(Snapshot value)
    {
        var state=TechWiseSimulationRuntime.Instance; var runtime=TechWiseDetailedAssemblyRuntime.Instance;
        Restoring=true;
        try
        {
            foreach(var part in state.Parts) if(part!=null) part.GetComponent<TechWiseRamInsertion>()?.ClearStage();
            foreach(var part in value.parts.Select(p=>p.part).Concat(value.screws.Select(s=>s.screw.Grab)).Concat(TechWiseAssemblyTool.Tools.Select(t=>t.GetComponent<XRGrabInteractable>())).Append(runtime.SidePanel).Where(p=>p!=null).Distinct())
                foreach(var owner in part.interactorsSelecting.ToArray()) part.interactionManager.SelectCancel(owner,part);
            runtime.RestoreMechanics(value.paste,value.prepared,value.lowered,value.cover,value.lever,value.m2);
            // Restore parent board before its socket-owned children.
            foreach(var saved in value.parts.OrderBy(p=>state.StepOf(p.part)=="Motherboard"?0:1))
            {
                if(saved.part==null) continue; var part=saved.part;
                part.gameObject.SetActive(saved.active); part.transform.SetPositionAndRotation(saved.pose.position,saved.pose.rotation);
                var body=part.GetComponent<Rigidbody>();
                if(body!=null) { body.isKinematic=true; body.useGravity=saved.gravity; }
                if(saved.socket!=null)
                {
                    var from=part.GetAttachTransform(saved.socket); var to=saved.socket.GetAttachTransform(part);
                    part.transform.rotation=to.rotation*Quaternion.Inverse(from.rotation)*part.transform.rotation;
                    part.transform.position+=to.position-from.position;
                    saved.socket.interactionManager.SelectEnter((IXRSelectInteractor)saved.socket,part);
                }
                else if(body!=null) body.isKinematic=saved.kinematic;
                if(saved.raisedRamSocket!=null) part.GetComponent<TechWiseRamInsertion>().Stage(saved.raisedRamSocket);
                foreach(var pair in saved.triggers) if(pair.Key!=null) pair.Key.isTrigger=pair.Value;
                if(body!=null&&!body.isKinematic) body.linearVelocity=body.angularVelocity=Vector3.zero;
            }
            foreach(var saved in value.screws) if(saved.screw!=null)
            { saved.screw.gameObject.SetActive(saved.active); saved.screw.Restore(saved.inserted,saved.progress,saved.pose); }
            foreach(var saved in value.tools) if(saved.target!=null)
            { saved.target.SetPositionAndRotation(saved.pose.position,saved.pose.rotation); var body=saved.target.GetComponent<Rigidbody>(); if(body!=null&&!body.isKinematic) body.linearVelocity=body.angularVelocity=Vector3.zero; }
            runtime.RestorePanel(value.panelInstalled,value.panel);
            state.RestoreActions(value.performed,value.touched);
            Physics.SyncTransforms();
        }
        finally { Restoring=false; }
    }
}
