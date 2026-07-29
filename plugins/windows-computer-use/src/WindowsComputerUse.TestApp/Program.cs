namespace WindowsComputerUse.TestApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TestForm());
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
            Text = "Commit",
            Location = new Point(374, 74),
            Width = 110
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
