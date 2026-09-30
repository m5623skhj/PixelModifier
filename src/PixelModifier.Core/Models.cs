using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelModifier.Core;

public enum MotionKind { Walk, Run }
public enum Facing { Right, Left, Front }
public enum BodyPart : byte { Torso, Head, ArmA, ArmB, LegA, LegB, Coat }
public enum Joint
{
    Head, Neck, Hip, ShoulderA, ElbowA, HandA, ShoulderB, ElbowB, HandB,
    KneeA, FootA, KneeB, FootB
}

public readonly record struct Point2(float X, float Y)
{
    public Vector2 Vector => new(X, Y);
    public static Point2 From(Vector2 value) => new(value.X, value.Y);
    public static Point2 Lerp(Point2 a, Point2 b, float amount) =>
        From(Vector2.Lerp(a.Vector, b.Vector, amount));
}

public sealed class Rig
{
    public Dictionary<Joint, Point2> Joints { get; set; } = [];
    public Point2 this[Joint joint] { get => Joints[joint]; set => Joints[joint] = value; }
    public Rig Clone() => new() { Joints = new(Joints) };
    public void Validate()
    {
        foreach (Joint joint in Enum.GetValues<Joint>())
        {
            if (!Joints.TryGetValue(joint, out var point) ||
                !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
                throw new InvalidDataException($"관절 좌표가 올바르지 않습니다: {joint}");
        }
    }
}

public sealed class RegionMap
{
    public int Width { get; set; } = 128;
    public int Height { get; set; } = 128;
    public byte[] Values { get; set; } = new byte[128 * 128];
    public RegionMap Clone() => new() { Width = Width, Height = Height, Values = (byte[])Values.Clone() };
    public BodyPart At(float x, float y)
    {
        int px = Math.Clamp((int)(x * Width), 0, Width - 1);
        int py = Math.Clamp((int)(y * Height), 0, Height - 1);
        return (BodyPart)Values[py * Width + px];
    }
    public void Paint(float x, float y, float radius, BodyPart part)
    {
        int minX = Math.Max(0, (int)((x - radius) * Width));
        int maxX = Math.Min(Width - 1, (int)((x + radius) * Width));
        int minY = Math.Max(0, (int)((y - radius) * Height));
        int maxY = Math.Min(Height - 1, (int)((y + radius) * Height));
        for (int py = minY; py <= maxY; py++)
            for (int px = minX; px <= maxX; px++)
                if (Vector2.DistanceSquared(new((px + .5f) / Width, (py + .5f) / Height), new(x, y)) <= radius * radius)
                    Values[py * Width + px] = (byte)part;
    }
}

public sealed class SourceDefinition
{
    public string Path { get; set; } = "";
    public float Phase { get; set; }
    public Rig Rig { get; set; } = new();
    public RegionMap Regions { get; set; } = new();
}

public sealed class GenerationSettings
{
    public MotionKind Motion { get; set; } = MotionKind.Walk;
    public Facing Facing { get; set; } = Facing.Right;
    public int FrameCount { get; set; } = 8;
    public int CandidateCount { get; set; } = 12;
    public int Seed { get; set; } = 1000;
    public float Intensity { get; set; } = 1;
    public float FramesPerSecond { get; set; } = 10;
    public int CellWidth { get; set; } = 256;
    public int CellHeight { get; set; } = 256;
    public int Columns { get; set; } = 4;
    public void Validate(int sourceCount)
    {
        if (sourceCount < 1) throw new InvalidDataException("PNG 이미지를 먼저 추가해 주세요.");
        if (FrameCount < Math.Max(2, sourceCount) || FrameCount > 240)
            throw new InvalidDataException($"프레임 수는 {Math.Max(2, sourceCount)}~240 범위여야 합니다.");
        if (CandidateCount < 1 || CandidateCount > 64)
            throw new InvalidDataException("후보 수는 1~64 범위여야 합니다.");
        if (!float.IsFinite(Intensity) || Intensity < .1f || Intensity > 2)
            throw new InvalidDataException("움직임 강도는 0.1~2 범위여야 합니다.");
        if (!float.IsFinite(FramesPerSecond) || FramesPerSecond < 1 || FramesPerSecond > 120)
            throw new InvalidDataException("재생 속도는 1~120 FPS 범위여야 합니다.");
        if (CellWidth < 32 || CellHeight < 32 || CellWidth > 4096 || CellHeight > 4096)
            throw new InvalidDataException("셀 크기는 32~4096 픽셀 범위여야 합니다.");
        if (Columns < 1 || Columns > FrameCount)
            throw new InvalidDataException("열 수는 1~프레임 수 범위여야 합니다.");
        long sheetPixels = (long)(Columns * CellWidth) * (((FrameCount + Columns - 1) / Columns) * CellHeight);
        if (sheetPixels > 64_000_000 || Columns * CellWidth > 32768 ||
            ((FrameCount + Columns - 1) / Columns) * CellHeight > 32768)
            throw new InvalidDataException("출력 시트가 너무 큽니다. 셀 크기나 프레임 수를 줄여 주세요. (최대 6,400만 픽셀)");
    }
    public GenerationSettings Clone() => (GenerationSettings)MemberwiseClone();
}

public sealed class ProjectDocument
{
    public int Version { get; set; } = 1;
    public List<SourceDefinition> Sources { get; set; } = [];
    public GenerationSettings Settings { get; set; } = new();
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public void Save(string path)
    {
        string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var copy = new ProjectDocument { Settings = Settings.Clone() };
        foreach (var source in Sources)
            copy.Sources.Add(new SourceDefinition
            {
                Path = System.IO.Path.GetRelativePath(root, source.Path),
                Phase = source.Phase, Rig = source.Rig.Clone(), Regions = source.Regions.Clone()
            });
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(tempPath, path, true);
    }
    public static ProjectDocument Load(string path)
    {
        var doc = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("프로젝트 파일을 읽을 수 없습니다.");
        if (doc.Version != 1) throw new InvalidDataException("지원하지 않는 프로젝트 버전입니다.");
        if (doc.Sources.Count > 64) throw new InvalidDataException("기준 이미지는 최대 64장입니다.");
        string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        foreach (var source in doc.Sources)
        {
            source.Path = System.IO.Path.GetFullPath(source.Path, root);
            source.Rig.Validate();
            if (!float.IsFinite(source.Phase) || source.Phase < 0 || source.Phase >= 1)
                throw new InvalidDataException("기준 자세의 주기 위치는 0 이상 1 미만이어야 합니다.");
            if (source.Regions.Width < 1 || source.Regions.Width > 512 || source.Regions.Height < 1 ||
                source.Regions.Height > 512 || source.Regions.Values.Length != source.Regions.Width * source.Regions.Height ||
                source.Regions.Values.Any(v => v > (byte)BodyPart.Coat))
                throw new InvalidDataException("부위 영역 데이터가 올바르지 않습니다.");
        }
        return doc;
    }
}

public sealed record MotionVariant(int Index, int Seed, float Stride, float Lift,
    float Bob, float ArmSwing, float Lean, float Cloth, float Timing)
{
    public static MotionVariant Create(int index, GenerationSettings settings)
    {
        int seed = unchecked(settings.Seed + index * 7919);
        var random = new Random(seed);
        float Pick(float low, float high) => low + (high - low) * (float)random.NextDouble();
        bool run = settings.Motion == MotionKind.Run;
        float strength = settings.Intensity;
        // One effort value coordinates stride, clearance, arm swing and body response.
        float effort = Pick(0, 1);
        float stride = (run ? .065f + .045f * effort : .025f + .025f * effort) * strength;
        float lift = stride * Pick(run ? .48f : .36f, run ? .62f : .50f);
        return new(index, seed, stride, lift,
            lift * Pick(run ? .25f : .18f, run ? .36f : .28f),
            (run ? .65f + .30f * effort : .16f + .20f * effort) * strength * Pick(.92f, 1.08f),
            (run ? .075f + .055f * effort : .008f + .022f * effort) * strength,
            stride * Pick(.08f, .14f), Pick(-.04f, .04f));
    }
}
