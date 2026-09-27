using System;
using System.Linq;

/// <summary>Shared requirements, used by every lesson, live validator and assessment denominator.</summary>
public static class TechWiseBuildDefinition
{
    public sealed class Component
    {
        public readonly string id, name, target, handling, orientation;
        public readonly int quantity, screws;
        public Component(string id, string name, int quantity, int screws, string target, string handling, string orientation)
        { this.id = id; this.name = name; this.quantity = quantity; this.screws = screws; this.target = target; this.handling = handling; this.orientation = orientation; }
    }
    public static readonly Component[] Components = {
        new("CPU", "CPU", 1, 0, "open motherboard CPU socket", "Hold the edges, with the metal face up; avoid the contacts.", "Match the keyed corner and keep the CPU level above its socket."),
        new("CPUCooler", "CPU cooler", 1, 4, "four mounts around the CPU", "Hold the cooler body; keep its contact plate facing the pasted CPU.", "Align all four mounting arms with their holes, then lower vertically."),
        new("RAM", "RAM module", 4, 0, "four motherboard memory slots", "Hold each module by its ends, away from its gold contacts.", "Gold contacts go into the slot; match the keyed notch and keep the module upright."),
        new("M2", "M.2 SSD", 1, 1, "M.2 connector and free-end standoff", "Hold the PCB edges; raise the free end about 25 degrees.", "Slide the connector edge into the matching slot, release, then lower the free end."),
        new("Motherboard", "Motherboard", 1, 9, "case tray and nine matching PCB standoffs", "Support the board by its edges with the installed parts facing the case opening.", "Align the rear connectors with the rear case and all nine holes with standoffs."),
        new("FanRear", "Rear exhaust fan", 1, 0, "rear grille beside the CPU", "Hold the frame, not the blades.", "Airflow points OUT of the rear grille. Align the fan with its rear mounting position and release to seat. Screws are not required."),
        new("FanFront1", "Upper front intake fan", 1, 0, "upper front grille", "Hold the frame, not the blades.", "Airflow points INTO the case from the front. Align the fan with its upper front mount and release to seat. Screws are not required."),
        new("FanFront2", "Lower front intake fan", 1, 0, "lower front grille", "Hold the frame, not the blades.", "Airflow points INTO the case from the front. Align the fan with its lower front mount and release to seat. Screws are not required."),
        new("GPUConnector", "GPU connector", 1, 0, "existing GPU bracket", "Hold the connector body and align its keyed edge.", "Match the highlighted bracket pose; do not reverse the connector."),
        new("GPU", "Graphics card with attached cooler", 1, 2, "GPU socket and rear retaining bracket", "Hold the card edges and cooler housing, away from contacts.", "Align the gold edge with the expansion slot and rear outputs with the case opening."),
        new("Storage", "SATA SSD", 1, 0, "existing storage mount", "Hold the SSD housing.", "Align the SATA SSD with the case drive mount and release to seat. Screws are not required."),
        new("PSU", "Power supply", 1, 4, "lower case PSU bracket", "Hold the power-supply housing.", "The mains inlet and switch face OUT through the rear opening.")
    };
    public static readonly string[] AssemblyOrder = Components.Select(c => c.id).ToArray();
    public static readonly string[] DisassemblyOrder = AssemblyOrder.Reverse().ToArray();
    public static Component Find(string id) => Components.FirstOrDefault(c => c.id == id);
    public static int ScrewCount => Components.Sum(c => c.screws);
    public static string HoleId(string component, int index) => component + "/screw/" + (index + 1);
}
