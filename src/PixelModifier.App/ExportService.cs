using System.IO;
using System.Text.Json;
using PixelModifier.Core;

namespace PixelModifier.App;

public static class ExportService
{
    public static void Export(IReadOnlyList<RenderSource> sources, GenerationSettings settings,
        MotionVariant variant, string path, CancellationToken cancellation = default, Action<int>? progress = null)
    {
        settings.Validate(sources.Count);
        var cycle = MotionCycle.Create(sources, variant, settings, cancellation);
        int rows = (settings.FrameCount + settings.Columns - 1) / settings.Columns;
        int width = settings.Columns * settings.CellWidth, height = rows * settings.CellHeight;
        int pixelScale = Math.Min(settings.CellWidth, settings.CellHeight) / settings.WorkingSize;
        int fittedSize = settings.WorkingSize * pixelScale;
        var sheet = new byte[checked(width * height * 4)];
        for (int frame = 0; frame < settings.FrameCount; frame++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = Renderer.Render(sources, cycle, frame, variant, settings.CellWidth, settings.CellHeight, cancellation);
            int cellX = (frame % settings.Columns) * settings.CellWidth;
            int cellY = (frame / settings.Columns) * settings.CellHeight;
            for (int y = 0; y < settings.CellHeight; y++)
                image.Pixels.Slice(y * settings.CellWidth * 4, settings.CellWidth * 4)
                    .CopyTo(sheet.AsSpan(((cellY + y) * width + cellX) * 4, settings.CellWidth * 4));
            progress?.Invoke(frame + 1);
        }
        var metadata = new
        {
            format = "PixelModifier.sprite.v1", image = Path.GetFileName(path),
            motion = settings.Motion.ToString().ToLowerInvariant(), loop = true,
            frame_count = settings.FrameCount, columns = settings.Columns, rows,
            cell_width = settings.CellWidth, cell_height = settings.CellHeight,
            working_size = settings.WorkingSize,
            pixel_scale = pixelScale,
            frames_per_second = settings.FramesPerSecond, frame_seconds = 1 / settings.FramesPerSecond,
            anchor_x = ((settings.CellWidth - fittedSize) / 2 + fittedSize * .5f) / settings.CellWidth,
            anchor_y = ((settings.CellHeight - fittedSize) / 2 + fittedSize * .90f) / settings.CellHeight,
            facing = settings.Facing.ToString(), variant, motion_quality = cycle.Quality
        };
        string pngTemp = path + ".tmp", jsonPath = Path.ChangeExtension(path, ".json"), jsonTemp = jsonPath + ".tmp";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Imaging.SavePng(new(width, height, sheet), pngTemp);
            File.WriteAllText(jsonTemp, JsonSerializer.Serialize(metadata, ProjectDocument.JsonOptions));
            cancellation.ThrowIfCancellationRequested();
            File.Move(pngTemp, path, true);
            File.Move(jsonTemp, jsonPath, true);
        }
        finally
        {
            if (File.Exists(pngTemp)) File.Delete(pngTemp);
            if (File.Exists(jsonTemp)) File.Delete(jsonTemp);
        }
    }
}
