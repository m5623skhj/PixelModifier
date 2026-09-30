using System.IO;
using System.Windows;
using PixelModifier.Core;

namespace PixelModifier.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--render-example")
        {
            try { RenderExample(e.Args); Shutdown(0); }
            catch (Exception error)
            {
                if (e.Args.Length > 2) File.WriteAllText(Path.Combine(e.Args[2], "error.txt"), error.ToString());
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) window.LoadInitial(e.Args);
    }
    private static void RenderExample(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("--render-example input.png output-folder");
        Directory.CreateDirectory(args[2]);
        var original = Imaging.LoadPng(args[1]);
        var normalized = original.Normalize(256, 256);
        var rig = Rigging.Guess(normalized);
        var regions = Rigging.GuessRegions(rig);
        var sources = new[] { new RenderSource(normalized, rig, regions, 0) };
        var summary = new List<object>();
        foreach (MotionKind motion in Enum.GetValues<MotionKind>())
        {
            var settings = new GenerationSettings { Motion = motion, FrameCount = motion == MotionKind.Walk ? 8 : 10,
                CellWidth = 256, CellHeight = 256, Columns = motion == MotionKind.Walk ? 4 : 5 };
            var variant = MotionVariant.Create(0, settings);
            string path = Path.Combine(args[2], motion.ToString().ToLowerInvariant() + ".png");
            ExportService.Export(sources, settings, variant, path);
            summary.Add(new { motion, settings.FrameCount, path });
        }
        new ProjectDocument
        {
            Sources = [new SourceDefinition { Path = Path.GetFullPath(args[1]), Rig = rig, Regions = regions, Phase = 0 }]
        }.Save(Path.Combine(args[2], "example.pixelmodifier.json"));
        File.WriteAllText(Path.Combine(args[2], "result.json"),
            System.Text.Json.JsonSerializer.Serialize(summary, ProjectDocument.JsonOptions));
    }
}
