namespace WindowsComputerUse.TestApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(args.Contains("--occluder", StringComparer.OrdinalIgnoreCase) ? new OccluderForm() : new TestForm());
    }
}

internal sealed class OccluderForm : Form
{
    public OccluderForm()
    {
        Text = "Windows Computer Use Occluder";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 230);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.Black;
        Controls.Add(new Label
        {
            Text = "OCCLUDER",
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.Black,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 22, FontStyle.Bold),
            Location = new Point(170, 85)
        });
    }
}

internal sealed class TestForm : Form
{
    public TestForm()
    {
        Text = "Windows Computer Use Test App";
        Name = "TestWindow";
        AccessibleName = "Windows Computer Use Test App";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 230);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var heading = new Label
        {
            Text = "Semantic UI automation test",
            AccessibleName = "Semantic UI automation test",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 14, FontStyle.Bold),
            Location = new Point(28, 24)
        };
        var input = new TextBox
        {
            Name = "InputBox",
            AccessibleName = "Input",
            PlaceholderText = "Type text",
            Location = new Point(30, 76),
            Width = 330
        };
        var commit = new Button
        {
            Name = "CommitButton",
            AccessibleName = "Commit",
            Text = "SAVE",
            Location = new Point(374, 66),
            Size = new Size(110, 42),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 14, FontStyle.Bold)
        };
        var status = new Label
        {
            Name = "StatusLabel",
            AccessibleName = "Idle",
            Text = "Idle",
            AutoSize = true,
            Location = new Point(30, 132)
        };
        commit.Click += (_, _) =>
        {
            status.Text = $"Saved: {input.Text}";
            status.AccessibleName = status.Text;
        };
        Controls.AddRange([heading, input, commit, status]);
        AcceptButton = commit;
    }
}
