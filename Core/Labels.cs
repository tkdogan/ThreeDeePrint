namespace Go2Bed;

// One wording for every setting, shared by the Rhino dialog, the Rhino command line and the
// Grasshopper parameters, so the same option is easy to find in both places.
internal static class Labels
{
    internal const string Scale = "Scale";
    internal const string Printer = "Printer";
    internal const string BedWidth = "Bed width";
    internal const string BedDepth = "Bed depth";
    internal const string BuildHeight = "Build height";
    internal const string FingerJoints = "Finger joints";
    internal const string FilamentPins = "Filament pins";
    internal const string PinHole = "Pin hole diameter";
    internal const string AssemblyMarks = "Assembly marks";
    internal const string Clearance = "Joint clearance";
    internal const string UseSourceColors = "Use source colors";

    internal const string ScaleHelp = "Scale denominator, e.g. 50 for 1:50.";
    internal const string BedWidthHelp = "Bed width (X) in mm.";
    internal const string BedDepthHelp = "Bed depth (Y) in mm.";
    internal const string BuildHeightHelp = "Build height (Z) in mm.";
    internal const string FingerJointsHelp = "Cut 5 mm tabs into eligible wall bottoms and matching sockets into the slab below.";
    internal const string FilamentPinsHelp = "Drill matching blind holes into mating faces for short pieces of 1.75 mm filament.";
    internal const string PinHoleHelp = "Pin hole diameter in mm (default 2.0 for 1.75 mm filament). Raise it if pins are too tight.";
    internal const string AssemblyMarksHelp = "Recess small matching squares into contacts too narrow for a pin, to line parts up when dry fitting.";
    internal const string ClearanceHelp = "Joint clearance per side in mm, for finger joints and seam keys.";
    internal const string UseSourceColorsHelp = "Copy each source object's Rhino display color to its baked meshes.";
}
