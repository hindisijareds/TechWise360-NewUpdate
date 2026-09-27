using System.Linq;
using System.Text;

internal static class TechWiseBuildLesson
{
    internal static string Describe(TechWiseDetailedAssemblyRuntime runtime,TechWiseSimulationRuntime state)
    {
        if(runtime.SetupError!=null) return "Setup needs attention: "+runtime.SetupError;
        if(!runtime.Ready || state==null) return "Preparing the complete workbench and checking every required part and hole.";
        bool remove=TechWiseSimulationModeManager.IsDisassembly;
        if(remove?runtime.RemovalComplete:runtime.BuildComplete) return remove
            ?"All components and individual screws are removed and released. CPU locks are open. Use Menu for another activity."
            :"Every component, screw, CPU lock and thermal-paste requirement is complete. The side panel is reinstalled. In Competition press Done to submit the complete build.";
        string step=runtime.CurrentPartStep;
        var definition=TechWiseBuildDefinition.Find(step);
        string objective="",component=definition?.name??"case panel",handling=definition?.handling??"Hold the panel by its edge.",
            placement=definition?.target??"original case side opening",orientation=definition?.orientation??"Match the panel edges to the case opening.",
            action="",validation="",error="",correction="",next="";
        var screw=runtime.CurrentFastener;
        var screws=runtime.Fasteners.Where(f=>f.ComponentId==step).ToArray();
        string progress=screws.Length==0?"Complete the current action before moving on.":remove
            ?$"Screws: {screws.Count(f=>f.Progress==0)} / {screws.Length} loosened; {screws.Count(f=>f.Removed)} / {screws.Length} removed."
            :$"Screws: {screws.Count(f=>f.Inserted)} / {screws.Length} inserted; {screws.Count(f=>f.Complete)} / {screws.Length} tightened.";
        if(remove && !runtime.CaseAccessOpen || !remove && runtime.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.CloseCase)
        {
            component="Case side panel"; handling="Hold the side-panel edge with "+TechWiseControlLabels.Grab+".";
            objective=remove?"Open the case before servicing components.":"Close the completed PC.";
            action=remove?"Pull the panel away and release it on the workbench. Gravity takes over when loose.":"Align the panel with its original opening, keep it level, then release it.";
            validation=remove?"The panel is detached and clear of the case.":"The panel snaps to its original attachment after the entire build is complete.";
            error="A held or misaligned panel is not complete."; correction="Grip it again, move it clear or align it, then release.";
            next=remove?"Begin removing the power supply.":"Build complete; Competition users can press Done.";
        }
        else if(screw!=null)
        {
            component=screw.Marking+" ("+screw.Id+")"+(screw.Inserted?" and screwdriver":" (loose screw)"); placement="Hole marked "+screw.Marking;
            handling="Grip the highlighted screw head; keep its shaft pointing into the hole. Use either hand for the screwdriver.";
            orientation="Screw shaft along the hole axis, head outward. With the driver, point the tip INTO the screw head.";
            objective=remove?"Loosen and remove this individual screw.":"Insert and tighten this individual screw.";
            if(!screw.Inserted) action=remove?"Release the removed screw on the tray, away from its mounting hole.":"Grip this screw, move within 2 cm of its matching numbered hole, align within 20 degrees, then RELEASE to insert.";
            else if(remove && screw.Progress==0) action="The screw is loose. Grip it, lift it out and release it at least 6 cm away on the tray.";
            else action="Grab the screwdriver. Place its tip within 1.8 cm of this screw head and align its shaft within 18 degrees. Hold "+TechWiseControlLabels.Activate+" for 2.5 seconds of valid contact. The tool and screw rotate automatically; you do not need to twist your wrist.";
            validation=remove?"This screw is fully loose, out of its hole and released on the tray.":"This exact screw is seated in its own hole and its tightening progress reaches 100%. A completed screw changes to muted green.";
            error="A wrong hole, backwards screw, missing grip, distant tip or tilted driver does not count.";
            correction="Use the matching screw/hole number. Re-grip and align the screw, or bring the held screwdriver tip onto the head and keep its shaft straight.";
            next="Repeat for each remaining numbered hole. The component stays incomplete until every required screw is finished.";
        }
        else if(!remove && runtime.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.ApplyThermalPaste)
        {
            objective="Apply thermal paste before the cooler."; component="Thermal-paste applicator and locked CPU";
            placement="Centre of the CPU's metal top surface"; orientation="Applicator tip down toward the CPU, within 25 degrees of the surface normal.";
            handling="Grip the applicator body using "+TechWiseControlLabels.Grab+"; keep your fingers away from its tip.";
            action="Bring its tip within 2.5 cm of the CPU centre. Hold "+TechWiseControlLabels.Activate+" for one second of valid contact to grow the visible paste blob. Release the tool input when complete.";
            validation="One full visible blob is applied to the installed CPU after both locks close.";
            error="Paste on another surface does not count; the cooler cannot be installed without completed paste.";
            correction="Finish the CPU locks, centre the applicator tip, point it down, and continue holding the tool input.";
            next="Pick up, orient and seat the CPU cooler, then insert and tighten all four screws separately.";
        }
        else if(!remove && runtime.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.CloseAndLockSocket || remove && step=="CPU" && (runtime.Cover.Closed || runtime.Lever.Closed))
        {
            bool lever=remove?runtime.Lever.Closed:runtime.Cover.Closed;
            component=lever?"CPU locking arm":"CPU retention cover"; objective=(remove?"Open ":"Close ")+component;
            placement="CPU socket hinge"; orientation="Follow the hinge path; keep the motherboard supported.";
            handling="Point at the indicated hinge and hold "+TechWiseControlLabels.Grab+".";
            action=remove?"Lift your hand about 9 cm away from the motherboard, then release at the fully open stop. Open the arm before the cover.":"Move your hand about 9 cm DOWN toward the motherboard, then release at the closed stop. Close the cover before the arm.";
            validation="The hinge reaches its stop and you release your grip."; error="Touching it or releasing halfway does not finish the action.";
            correction="Grip the indicated hinge again and complete its travel before releasing."; next=remove?(lever?"Open the CPU cover next.":"Lift the CPU out by its edges."):(lever?"Apply thermal paste after both locks close.":"Lower and release the locking arm next.");
        }
        else if(step=="M2" && runtime.Seated("M2") && (remove?runtime.M2Lowered:!runtime.M2Lowered))
        {
            objective=remove?"Raise the M.2 free end after removing its screw.":"Lower the M.2 free end onto its standoff.";
            component="M.2 free-end handle"; handling="Hold the small free-end handle using "+TechWiseControlLabels.Grab+".";
            action=remove?"Turn your wrist to raise the free end about 25 degrees, then release.":"Turn your wrist to lower the free end until it is flat, then release.";
            validation="The end reaches its stop and the grip is released."; error="The M.2 must not be removed while its screw remains secured.";
            correction="Remove the screw first when disassembling; then move the free-end handle along its hinge."; next=remove?"Grip and slide the SSD connector out.":"Insert and tighten the single M.2 retention screw.";
        }
        else if(!remove && runtime.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.PrepareCase)
        {
            objective="Open the case"; component="Case side panel"; handling="Grip its edge using "+TechWiseControlLabels.Grab+".";
            action="Pull the side panel at least 20 cm clear of the case and release it on the table.";
            placement="Worktable beside the case"; orientation="Lay it flat out of the assembly area.";
            validation="The panel has been grabbed, moved clear and released."; error="Merely touching the panel does not open the case.";
            correction="Hold Grip, pull the panel away, and release it on the workbench."; next="Place the prepared motherboard on its nine matching standoffs.";
        }
        else
        {
            objective=(remove?"Remove ":"Install ")+component;
            action=remove?"After removing every assigned screw or lock, grip the component, withdraw it along its connector direction, move it at least 20 cm clear of the mount and RELEASE it on the workbench.":
                "Hold "+TechWiseControlLabels.Grab+" to pick up the highlighted component. Move/turn your wrist to align it. While holding, use "+TechWiseControlLabels.Both("Manipulation")+" to rotate or adjust distance. Bring the connector into its target and RELEASE to seat it.";
            validation=remove?"The component is clear of its socket, released, and all its fasteners have been removed.":"The correct component snaps only in its matching target and orientation. Mere proximity is not enough. All required screws must then be inserted and tightened.";
            error=remove?"Secured components cannot be pulled out; a held component is not a completed removal.":"Backwards, upside-down, misaligned or wrong-slot releases do not advance the lesson.";
            correction=remove?"Complete every screw/lock first, then re-grip, withdraw and release the component clear of the mount.":"Re-grip the loose component; align its keyed edge to the indicated target and release. Green alignment feedback means ready to seat.";
            if(step=="RAM") { int seated=state.Parts.Count(p=>state.StepOf(p)=="RAM"&&state.IsPartInstalled(p)); progress=remove?$"RAM modules: {4-seated} / 4 removed.":$"RAM modules: {seated} / 4 seated. Each module must be handled separately."; }
            if(step=="RAM" && !remove)
            {
                action="Align the keyed notch and RELEASE to rest the module slightly above its slot. Then hold "+TechWiseControlLabels.Grab+" on that module again, push straight DOWN toward the motherboard, and release when fully pressed.";
                validation="Each module needs an aligned release AND a separate downward press and release. Raised RAM does not count as installed. Seat all four modules.";
                error="Releasing above the slot alone, pressing sideways, or releasing before the module is fully down leaves it unseated.";
                correction="Grip the raised module and push down about 2 cm. Release at the bottom. Pull up or sideways clear of the slot to reposition it.";
            }
            if(step == "Storage")
            {
                progress=remove?"SATA SSD removed.":(runtime.Seated("Storage")?"SATA SSD installed. Screws are not required.":"SATA SSD uninstalled. Screws are not required.");
                if(!remove)
                {
                    action="Hold the SATA SSD housing with "+TechWiseControlLabels.Grab+". Match the drive mount orientation and release to seat. Screws are not required.";
                    validation="The SATA SSD snaps securely into the case mount. Screws are not required.";
                }
            }
            if(step is "FanRear" or "FanFront1" or "FanFront2")
            {
                int seated=(runtime.Seated("FanRear")?1:0)+(runtime.Seated("FanFront1")?1:0)+(runtime.Seated("FanFront2")?1:0);
                progress=remove?$"Case fans: {3-seated} / 3 removed.":$"Case fans: {seated} / 3 installed. Align airflow arrow with each grille. No screws required.";
                if(!remove)
                {
                    action="Hold the fan frame with "+TechWiseControlLabels.Grab+". Match the airflow arrow to the grille (rear exhaust points OUT, front intakes point IN). Bring to the mount and release to seat. Screws are not required.";
                    validation="All 3 case fans must be installed in their matching grilles. Screws are not required for fans.";
                }
            }
            int index=System.Array.IndexOf(remove?TechWiseBuildDefinition.DisassemblyOrder:TechWiseBuildDefinition.AssemblyOrder,step);
            var order=remove?TechWiseBuildDefinition.DisassemblyOrder:TechWiseBuildDefinition.AssemblyOrder;
            next=definition!=null&&definition.screws>0&&!remove?"Insert and tighten all "+definition.screws+" numbered screws, one by one.":index>=0&&index+1<order.Length?"Continue with "+TechWiseSimulationRuntime.Label(order[index+1])+".":remove?"Complete the disassembly review.":"Reinstall the case side panel.";
        }
        if(!remove && runtime.CurrentPhase==TechWiseDetailedAssemblyRuntime.Phase.InstallCPU) next="Lower and release the cover, then the locking arm. Apply thermal paste before the cooler.";
        return new StringBuilder().AppendLine("<b>Objective:</b> "+objective).AppendLine("<b>Component / tool:</b> "+component)
            .AppendLine("<b>Controls:</b> "+TechWiseControlLabels.Grab+" to grab / release; "+TechWiseControlLabels.Activate+" to use a held tool.")
            .AppendLine("<b>Handling:</b> "+handling).AppendLine("<b>Placement:</b> "+placement).AppendLine("<b>Orientation:</b> "+orientation)
            .AppendLine("<b>Action:</b> "+action).AppendLine("<b>Validation:</b> "+validation).AppendLine("<b>Common error:</b> "+error)
            .AppendLine("<b>Correction:</b> "+correction).AppendLine("<b>Progress:</b> "+progress).Append("<b>Next:</b> "+next).ToString();
    }
}
