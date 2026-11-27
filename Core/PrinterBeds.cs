namespace Go2Bed;

// Usable build volume in millimetres: width (X) × depth (Y) × height (Z).
internal record PrinterBed(string Name, double Width, double Depth, double Height)
{
    // Rhino command-line options accept letters and digits only.
    internal string OptionName => new(Name.Where(char.IsLetterOrDigit).ToArray());
    public override string ToString() => $"{Name}  ({Width:0} × {Depth:0} × {Height:0} mm)";
}

// Common FDM printers, from the manufacturers' published build volumes. Pick the closest one,
// or enter the bed size by hand. The packer keeps a 3 mm margin inside every bed.
internal static class PrinterBeds
{
    internal const string Custom = "Custom";

    internal static readonly PrinterBed[] All =
    {
        new("Prusa MK4S", 250, 210, 220),
        new("Prusa MK3S+", 250, 210, 210),
        new("Prusa CORE One", 250, 220, 270),
        new("Prusa MINI+", 180, 180, 180),
        new("Prusa XL", 360, 360, 360),
        new("Bambu Lab X1C / P1S / A1", 256, 256, 256),
        new("Bambu Lab A1 mini", 180, 180, 180),
        new("Bambu Lab H2D", 325, 320, 325),
        new("Creality Ender-3 V3", 220, 220, 250),
        new("Creality K1", 220, 220, 250),
        new("Creality K1 Max", 300, 300, 300),
        new("Elegoo Neptune 4 Pro", 225, 225, 265),
        new("Anycubic Kobra 3", 250, 250, 260),
        new("Ultimaker S5", 330, 240, 300),
        new("Voron 2.4 (350)", 350, 350, 340),
    };

    internal static PrinterBed Default => All[0];

    // Accepts the display name or the command-line option name, ignoring case.
    internal static PrinterBed? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return All.FirstOrDefault(p => string.Equals(p.OptionName, key, StringComparison.OrdinalIgnoreCase));
    }

    // The library entry with exactly this build volume, if any.
    internal static PrinterBed? Matching(double width, double depth, double height) =>
        All.FirstOrDefault(p => Math.Abs(p.Width - width) < .01 && Math.Abs(p.Depth - depth) < .01 && Math.Abs(p.Height - height) < .01);
}
