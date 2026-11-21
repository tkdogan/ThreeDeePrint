using Eto.Drawing;
using Eto.Forms;

namespace ThreeDeePrint;

internal record PreviewSettings(bool Barn, double Scale, double Width, double Depth, double Height,
    bool Fingers, double Clearance)
{
    internal static PreviewSettings Defaults => new(true,PreviewDefaults.Scale,PreviewDefaults.Width,
        PreviewDefaults.Depth,PreviewDefaults.Height,false,.20);
}

internal sealed class PreviewDialog : Dialog<bool>
{
    private readonly DropDown source = new();
    private readonly NumericStepper scale, width, depth, height, clearance;
    private readonly CheckBox fingers = new() { Text = "Add wall-to-slab fingers" };
    private readonly bool hasBarn;
    internal PreviewSettings Values => new(hasBarn && source.SelectedIndex==0,scale.Value,width.Value,
        depth.Value,height.Value,fingers.Checked==true,clearance.Value);

    internal PreviewDialog(bool hasBarn, PreviewSettings initial)
    {
        this.hasBarn = hasBarn;
        Title = "Flat-pack preview";
        Padding = 16;
        Resizable = false;
        if (hasBarn) source.Items.Add("Simplified barn layers");
        source.Items.Add("Select components in Rhino");
        source.SelectedIndex = hasBarn && !initial.Barn ? 1 : 0;
        scale = Number(initial.Scale,.01,1000000,1,2);
        width = Number(initial.Width,7,10000,1,1);
        depth = Number(initial.Depth,7,10000,1,1);
        height = Number(initial.Height,1,10000,1,1);
        clearance = Number(initial.Clearance,.05,.60,.05,2);
        fingers.Checked = initial.Fingers;
        clearance.Enabled = initial.Fingers;
        fingers.CheckedChanged += (_,_) => clearance.Enabled = fingers.Checked==true;

        var layout = new DynamicLayout {Spacing = new Size(10,7)};
        layout.AddRow("Components",source);
        layout.AddRow("Scale  1 :",scale);
        layout.AddRow("Bed width (mm)",width);
        layout.AddRow("Bed depth (mm)",depth);
        layout.AddRow("Build height (mm)",height);
        layout.AddRow(null,fingers);
        layout.AddRow("Clearance per side (mm)",clearance);
        DefaultButton = new Button {Text="Bake preview"};
        AbortButton = new Button {Text="Cancel"};
        DefaultButton.Click += (_,_) => Close(true);
        AbortButton.Click += (_,_) => Close(false);
        layout.AddSeparateRow(null,AbortButton,DefaultButton);
        Content = layout;
    }

    private static NumericStepper Number(double value,double min,double max,double step,int decimals)
        => new() { MinValue=min, MaxValue=max, Increment=step, DecimalPlaces=decimals, Value=value };
}
