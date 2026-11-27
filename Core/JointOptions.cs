namespace Go2Bed;

// How parts are cut so they line up during assembly. All lengths are print-scale millimetres.
// Rhino's BedLayout dialog and the Grasshopper Prepare Parts component use the same labels:
//   Finger joints, Filament pins, Pin hole diameter, Assembly marks, Joint clearance.
internal record JointOptions(bool Fingers = true, bool Pins = true, bool Marks = true,
    double Clearance = JointOptions.DefaultClearance, double PinHole = JointOptions.DefaultPinHole)
{
    internal const double DefaultClearance = .20, MinClearance = .05, MaxClearance = .60;
    // 1.75 mm filament plus 0.25 mm of play. Raise it if pins do not go in, lower it if they fall out.
    internal const double DefaultPinHole = 2.0, MinPinHole = 1.75, MaxPinHole = 3.5;

    internal static JointOptions Defaults => new();

    internal void Validate()
    {
        if ((Fingers || Pins) && !(Clearance >= MinClearance && Clearance <= MaxClearance))
            throw new ArgumentException($"Joint clearance must be {MinClearance:0.00} to {MaxClearance:0.00} mm.");
        if (Pins && !(PinHole >= MinPinHole && PinHole <= MaxPinHole))
            throw new ArgumentException($"Pin hole diameter must be {MinPinHole:0.00} to {MaxPinHole:0.00} mm.");
    }
}
