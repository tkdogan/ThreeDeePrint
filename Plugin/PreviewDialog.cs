using Eto.Drawing;
using Eto.Forms;

namespace Go2Bed;

internal record PreviewSettings(double Scale, double Width, double Depth, double Height,
    JointOptions Joints, bool UseSourceColors = true)
{
    internal static PreviewSettings Defaults => new(PreviewDefaults.Scale, PrinterBeds.Default.Width,
        PrinterBeds.Default.Depth, PrinterBeds.Default.Height, JointOptions.Defaults);
}

// Labels match the Grasshopper inputs of Prepare Parts, Printer Bed, Flat Pack and Preview Plates.
internal sealed class PreviewDialog : Dialog<bool>
{
    private readonly NumericStepper scale, width, depth, height, clearance, pinHole;
    private readonly DropDown printer = new();
    private readonly CheckBox fingers = new() { Text = Labels.FingerJoints };
    private readonly CheckBox pins = new() { Text = Labels.FilamentPins };
    private readonly CheckBox marks = new() { Text = Labels.AssemblyMarks };
    private readonly CheckBox sourceColors = new() { Text = Labels.UseSourceColors };
    private bool choosingPrinter;

    internal PreviewSettings Values => new(scale.Value, width.Value, depth.Value, height.Value,
        new JointOptions(fingers.Checked == true, pins.Checked == true, marks.Checked == true, clearance.Value, pinHole.Value),
        sourceColors.Checked == true);

    internal PreviewDialog(PreviewSettings initial)
    {
        Title = "Go2Bed: Lay out print plates";
        Padding = 16;
        Resizable = false;
        scale = Number(initial.Scale, .01, 1000000, 1, 2);
        width = Number(initial.Width, 7, 10000, 1, 1);
        depth = Number(initial.Depth, 7, 10000, 1, 1);
        height = Number(initial.Height, 1, 10000, 1, 1);
        clearance = Number(initial.Joints.Clearance, JointOptions.MinClearance, JointOptions.MaxClearance, .05, 2);
        pinHole = Number(initial.Joints.PinHole, JointOptions.MinPinHole, JointOptions.MaxPinHole, .05, 2);
        fingers.Checked = initial.Joints.Fingers;
        pins.Checked = initial.Joints.Pins;
        marks.Checked = initial.Joints.Marks;
        sourceColors.Checked = initial.UseSourceColors;

        foreach (var bed in PrinterBeds.All) printer.Items.Add(bed.ToString(), bed.Name);
        printer.Items.Add(PrinterBeds.Custom, PrinterBeds.Custom);
        printer.SelectedKey = PrinterBeds.Matching(initial.Width, initial.Depth, initial.Height)?.Name ?? PrinterBeds.Custom;
        printer.SelectedKeyChanged += (_, _) =>
        {
            if (PrinterBeds.Find(printer.SelectedKey) is not { } bed) return;
            choosingPrinter = true;
            (width.Value, depth.Value, height.Value) = (bed.Width, bed.Depth, bed.Height);
            choosingPrinter = false;
        };
        void Edited(object? sender, EventArgs e)
        {
            if (!choosingPrinter) printer.SelectedKey = PrinterBeds.Matching(width.Value, depth.Value, height.Value)?.Name ?? PrinterBeds.Custom;
        }
        width.ValueChanged += Edited; depth.ValueChanged += Edited; height.ValueChanged += Edited;
        // Seam keys on split parts use the clearance too, so it stays editable without joints.
        pins.CheckedChanged += (_, _) => pinHole.Enabled = pins.Checked == true;
        pinHole.Enabled = pins.Checked == true;

        var layout = new DynamicLayout { Spacing = new Size(10, 7) };
        layout.AddRow(Labels.Scale + "  1 :", scale);
        layout.AddRow(Labels.Printer, printer);
        layout.AddRow(Labels.BedWidth + " (mm)", width);
        layout.AddRow(Labels.BedDepth + " (mm)", depth);
        layout.AddRow(Labels.BuildHeight + " (mm)", height);
        layout.AddRow(null, fingers);
        layout.AddRow(null, pins);
        layout.AddRow(Labels.PinHole + " (mm)", pinHole);
        layout.AddRow(null, marks);
        layout.AddRow(Labels.Clearance + " per side (mm)", clearance);
        layout.AddRow(null, sourceColors);
        DefaultButton = new Button { Text = "Bake plates" };
        AbortButton = new Button { Text = "Cancel" };
        DefaultButton.Click += (_, _) => Close(true);
        AbortButton.Click += (_, _) => Close(false);
        layout.AddSeparateRow(null, AbortButton, DefaultButton);
        Content = layout;
    }

    private static NumericStepper Number(double value, double min, double max, double step, int decimals)
        => new() { MinValue = min, MaxValue = max, Increment = step, DecimalPlaces = decimals, Value = value };
}
