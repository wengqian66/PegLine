using System.Drawing;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeForm());
    }
}

internal sealed class ProbeForm : Form
{
    public ProbeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Bounds = new Rectangle(0, 0, Screen.PrimaryScreen!.WorkingArea.Width, 120);
        var label = new Label
        {
            Text = "PROBE OVERLAY VISIBLE",
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.DarkOrange,
            AutoSize = true,
            Location = new Point(40, 40),
        };
        Controls.Add(label);
    }
}
