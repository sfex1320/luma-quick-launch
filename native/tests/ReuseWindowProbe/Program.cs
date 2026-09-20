using System.Diagnostics;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        // Unique output directory for every smoke run: no user app, files or settings.
        File.WriteAllText(Path.Combine(root, $"launch-{Environment.ProcessId}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, args }));
        Thread.Sleep(1200); // Exercise repeated clicks before the first HWND appears.
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "Luma reuse test fixture", Width = 400, Height = 200 };
        form.Controls.Add(new Label { Text = "Isolated Luma window reuse test", Dock = DockStyle.Fill });
        Application.Run(form);
    }
}
