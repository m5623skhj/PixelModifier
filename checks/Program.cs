using System.Numerics;
using PixelModifier.Core;

int count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    count++;
}
var settings = new GenerationSettings();
var variant = MotionVariant.Create(0, settings);
var image = Fixture(96, 96, false);
var rig = Rigging.Guess(image);
var regions = Rigging.GuessRegions(rig);
var sources = new[] { new RenderSource(image, rig, regions, 0) };

Check(MotionVariant.Create(0, settings) == MotionVariant.Create(0, settings), "Seed must reproduce the same variant.");
Check(variant != MotionVariant.Create(1, settings), "Candidates must vary.");
var start = Motion.Pose(rig, 0, variant, settings);
var end = Motion.Pose(rig, 1, variant, settings);
Check(Enum.GetValues<Joint>().All(j => start[j] == end[j]), "Gait must wrap continuously.");
var stance = Motion.Pose(rig, .25f, variant, settings);
Check(Math.Abs(stance[Joint.FootA].Y - rig[Joint.FootA].Y) < .00001f, "Planted foot must stay on ground.");
Check(stance[Joint.FootB].Y < rig[Joint.FootB].Y, "Swing foot must lift.");
for (int i = 0; i < 100; i++)
{
    var pose = Motion.Pose(rig, i / 100f, variant, settings);
    Check(pose.Joints.Values.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "Pose must stay finite.");
}
var frames = Enumerable.Range(0, 8).Select(i => Renderer.Render(sources, i / 8f, variant, settings, 96, 96)).ToArray();
Check(frames.All(f => f.Bounds().Height > 0 && f.HasTransparency()), "Rendered frames must retain character and transparency.");
Check(!frames[0].Pixels.SequenceEqual(frames[2].Pixels), "Motion must affect actual pixels.");
var palette = Palette(image);
Check(frames.All(f => Palette(f).IsSubsetOf(palette)), "Nearest sampling must preserve all source RGBA colors.");
Check(frames[0].Pixels.SequenceEqual(Renderer.Render(sources, 0, variant, settings, 96, 96).Pixels), "Rendering must be deterministic.");
var nearWrap = Renderer.Render(sources, .9999f, variant, settings, 96, 96);
int seamDifference = Difference(frames[0], nearWrap);
Check(seamDifference < image.Width * image.Height * .04, "Seam must approach the first pose.");
var image2 = Fixture(96, 96, true);
var rig2 = rig.Clone();
rig2[Joint.HandA] = new(.77f, .40f);
rig2[Joint.ElbowA] = new(.7f, .38f);
var multiple = new[] { sources[0], new RenderSource(image2, rig2, regions.Clone(), .5f) };
var first = Timeline.Plan(multiple, 0, variant, settings);
var second = Timeline.Plan(multiple, .5f, variant, settings);
Check(first.SourceIndex == 0 && second.SourceIndex == 1, "Reference images must be used at their phases.");
Check(Enum.GetValues<Joint>().All(j => first.Pose[j] == rig[j] && second.Pose[j] == rig2[j]), "Authored key poses must remain exact.");
var middle = Timeline.Plan(multiple, .25f, variant, settings);
Check(middle.Pose[Joint.HandA] != rig[Joint.HandA] && middle.Pose[Joint.HandA] != rig2[Joint.HandA], "Intermediate poses must interpolate.");
Check(Renderer.Render(multiple, .5f, variant, settings, 96, 96).Pixels.SequenceEqual(image2.Pixels), "Keyframe image must retain exact source pixels.");
bool duplicated = false;
try { Timeline.Plan([sources[0], sources[0]], 0, variant, settings); }
catch (InvalidDataException) { duplicated = true; }
Check(duplicated, "Duplicate reference phases must be rejected.");
var three = new[] { sources[0], new RenderSource(image2, rig2, regions.Clone(), 1f / 3),
    new RenderSource(image, rig, regions.Clone(), 2f / 3) };
Check(Timeline.Plan(three, 3f / 8, variant, settings).Pose[Joint.HandA] == rig2[Joint.HandA],
    "Reference phases must snap to an actual exported frame.");

string temporary = Path.Combine(Path.GetTempPath(), "PixelModifierChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    string inputPath = Path.Combine(temporary, "sprite.png"), projectPath = Path.Combine(temporary, "project.json");
    var project = new ProjectDocument { Settings = settings, Sources = [
        new() { Path = inputPath, Rig = rig, Regions = regions, Phase = 0 },
        new() { Path = inputPath, Rig = rig2, Regions = regions.Clone(), Phase = .5f }] };
    project.Save(projectPath);
    var loaded = ProjectDocument.Load(projectPath);
    Check(loaded.Sources[0].Path == inputPath && loaded.Sources.Count == 2, "Project must resolve relative sources.");
    Check(loaded.Sources[1].Rig[Joint.HandA] == rig2[Joint.HandA], "Edited landmarks must round-trip.");
    Check(loaded.Sources[0].Regions.Values.SequenceEqual(regions.Values), "Painted regions must round-trip.");
    var normalized = image.Normalize(128, 128);
    Check(Palette(normalized).IsSubsetOf(palette), "Alignment must preserve the source palette.");
    Check(normalized.Bounds().Y + normalized.Bounds().Height <= 116, "Alignment must retain lower canvas padding.");
    // A second silhouette verifies that rig guessing has no character-specific coordinate or color assumptions.
    var broad = Fixture(128, 96, true).Normalize(128, 128);
    var broadRig = Rigging.Guess(broad);
    broadRig.Validate();
    Check(Renderer.Render([new(broad, broadRig, Rigging.GuessRegions(broadRig), 0)], .6f,
        MotionVariant.Create(0, new() { Motion = MotionKind.Run }), new() { Motion = MotionKind.Run }, 128, 128).Bounds().Width > 0,
        "Different silhouettes must render.");
}
finally { Directory.Delete(temporary, true); }
bool canceled = false;
try { Renderer.Render(sources, 0, variant, settings, 96, 96, new CancellationToken(true)); }
catch (OperationCanceledException) { canceled = true; }
Check(canceled, "Rendering must support cancellation.");
bool invalidCount = false;
try { new GenerationSettings { FrameCount = 2 }.Validate(3); }
catch (InvalidDataException) { invalidCount = true; }
Check(invalidCount, "Output must have at least as many frames as references.");
Console.WriteLine($"PASS: {count} checks; loop boundary, foot contacts, palette, multi-image anchors, persistence, cancellation.");

static PixelImage Fixture(int width, int height, bool alternate)
{
    var pixels = new byte[width * height * 4];
    void Rect(float x, float y, float w, float h, byte r, byte g, byte b)
    {
        for (int py = (int)(y * height); py < Math.Min(height, (int)((y + h) * height)); py++)
            for (int px = (int)(x * width); px < Math.Min(width, (int)((x + w) * width)); px++)
            { int i = (py * width + px) * 4; pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255; }
    }
    Rect(.38f, .10f, .24f, .18f, 230, 198, 122);
    Rect(.33f, .28f, .34f, .30f, 61, 97, 155);
    Rect(.23f, .3f, .10f, .27f, 194, 94, 73);
    Rect(.67f, alternate ? .32f : .3f, alternate ? .20f : .10f, alternate ? .10f : .27f, 194, 94, 73);
    Rect(.35f, .57f, .11f, .32f, 53, 68, 89);
    Rect(.54f, .57f, .11f, .32f, 53, 68, 89);
    Rect(.33f, .86f, .15f, .04f, 190, 139, 84);
    Rect(.52f, .86f, .17f, .04f, 190, 139, 84);
    return new(width, height, pixels);
}
static HashSet<uint> Palette(PixelImage image)
{
    var result = new HashSet<uint>();
    for (int i = 0; i < image.Pixels.Length; i += 4)
        if (image.Pixels[i + 3] > 0)
            result.Add(BitConverter.ToUInt32(image.Pixels.Slice(i, 4)));
    return result;
}
static int Difference(PixelImage a, PixelImage b)
{
    int count = 0;
    for (int i = 0; i < a.Pixels.Length; i += 4)
        if (!a.Pixels.Slice(i, 4).SequenceEqual(b.Pixels.Slice(i, 4))) count++;
    return count;
}
