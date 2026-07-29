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
        ClientSize = new Size(520, 500);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        KeyPreview = true;

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
        var readOnlyPaste = new TextBox
        {
            Name = "ReadOnlyPasteBox",
            AccessibleName = "Read-only paste target",
            Text = "Locked",
            ReadOnly = true,
            Location = new Point(30, 108),
            Width = 180
        };
        var status = new Label
        {
            Name = "StatusLabel",
            AccessibleName = "Idle",
            Text = "Idle",
            AutoSize = true,
            Location = new Point(30, 140)
        };
        var openDialog = new Button
        {
            Name = "DialogButton",
            AccessibleName = "Open dialog",
            Text = "OPEN DIALOG",
            Location = new Point(30, 174),
            Size = new Size(150, 38)
        };
        var featureToggle = new CheckBox
        {
            Name = "FeatureToggle",
            AccessibleName = "Enable feature",
            Text = "Enable feature",
            AutoSize = true,
            Location = new Point(30, 232)
        };
        var modeList = new ListBox
        {
            Name = "ModeList",
            AccessibleName = "Mode list",
            Location = new Point(250, 166),
            Size = new Size(230, 105)
        };
        modeList.Items.AddRange(["Alpha", "Beta", "Gamma"]);
        modeList.SelectedIndex = 0;
        var recreateWindow = new Button
        {
            Name = "RecreateWindowButton",
            AccessibleName = "Recreate window handle",
            Text = "RECREATE HWND",
            Location = new Point(30, 286),
            Size = new Size(150, 36)
        };
        var delayedToggle = new Button
        {
            Name = "DelayedToggleButton",
            AccessibleName = "Enable feature later",
            Text = "ENABLE LATER",
            Location = new Point(200, 286),
            Size = new Size(145, 36)
        };
        var keyStatus = new Label
        {
            Name = "KeyStatusLabel",
            AccessibleName = "Key idle",
            Text = "Key idle",
            AutoSize = true,
            Location = new Point(30, 345)
        };
        var mouseSurface = new Label
        {
            Name = "MouseSurface",
            AccessibleName = "Mouse interaction surface",
            Text = "MOUSE INTERACTION SURFACE",
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.AliceBlue,
            Location = new Point(30, 375),
            Size = new Size(250, 44)
        };
        var mouseStatus = new Label
        {
            Name = "MouseStatusLabel",
            AccessibleName = "Mouse idle",
            Text = "Mouse idle",
            AutoSize = true,
            Location = new Point(30, 440)
        };
        commit.Click += (_, _) =>
        {
            status.Text = $"Saved: {input.Text}";
            status.AccessibleName = status.Text;
        };
        openDialog.Click += (_, _) => BeginInvoke(new Action(() =>
        {
            var dialog = new Form
            {
                Text = "Windows Computer Use Dialog",
                Name = "OwnedDialog",
                AccessibleName = "Windows Computer Use Dialog",
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(360, 150),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            dialog.Controls.Add(new Label
            {
                Text = "Owned transient dialog",
                AccessibleName = "Owned transient dialog",
                AutoSize = true,
                Location = new Point(28, 28)
            });
            var close = new Button
            {
                Name = "DialogCloseButton",
                AccessibleName = "Close dialog",
                Text = "CLOSE",
                Location = new Point(235, 82),
                Size = new Size(95, 34)
            };
            close.Click += (_, _) => dialog.Close();
            dialog.Controls.Add(close);
            dialog.Show(this);
        }));
        recreateWindow.Click += (_, _) => RecreateHandle();
        delayedToggle.Click += (_, _) =>
        {
            var phase = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    featureToggle.Checked = true;
                    timer.Interval = 1200;
                }
                else
                {
                    timer.Stop();
                    featureToggle.Checked = false;
                    timer.Dispose();
                }
            };
            timer.Start();
        };
        KeyDown += (_, eventArgs) =>
        {
            keyStatus.Text = $"Key down: {eventArgs.KeyCode}";
            keyStatus.AccessibleName = keyStatus.Text;
        };
        KeyUp += (_, eventArgs) =>
        {
            keyStatus.Text = $"Key up: {eventArgs.KeyCode}";
            keyStatus.AccessibleName = keyStatus.Text;
        };
        mouseSurface.MouseDown += (_, eventArgs) =>
        {
            mouseStatus.Text = $"Mouse down: {eventArgs.Button}";
            mouseStatus.AccessibleName = mouseStatus.Text;
        };
        mouseSurface.MouseUp += (_, eventArgs) =>
        {
            mouseStatus.Text = $"Mouse up: {eventArgs.Button}";
            mouseStatus.AccessibleName = mouseStatus.Text;
        };
        Controls.AddRange([heading, input, commit, readOnlyPaste, status, openDialog, featureToggle, modeList, recreateWindow, delayedToggle, keyStatus, mouseSurface, mouseStatus]);
        AcceptButton = commit;
    }
}
