using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelModifier.Core;

namespace PixelModifier.App;

public sealed class RigEditor : FrameworkElement
{
    public PixelImage? ImageData { get; private set; }
    public Rig? Rig { get; private set; }
    public RegionMap? Regions { get; private set; }
    public bool PaintMode { get; set; }
    public BodyPart PaintPart { get; set; } = BodyPart.Torso;
    public float BrushRadius { get; set; } = .04f;
    public bool ShowRegions { get; set; }
    public event Action? Edited;
    public event Action<string>? SelectionChanged;
    private BitmapSource? bitmap;
    private BitmapSource? overlay;
    private Joint? dragging;
    private bool painting;
    private Rect imageRect;
    private Point2? cursor;

    public static Color PartColor(BodyPart part) => part switch
    {
        BodyPart.Head => Color.FromRgb(250, 205, 81),
        BodyPart.ArmA => Color.FromRgb(77, 207, 178), BodyPart.ArmB => Color.FromRgb(78, 156, 238),
        BodyPart.LegA => Color.FromRgb(248, 136, 120), BodyPart.LegB => Color.FromRgb(186, 133, 248),
        BodyPart.Coat => Color.FromRgb(227, 145, 219), _ => Color.FromRgb(148, 176, 207)
    };
    public static string JointLabel(Joint joint) => joint switch
    {
        Joint.Head => "머리", Joint.Neck => "목", Joint.Hip => "골반",
        Joint.ShoulderA => "어깨 A", Joint.ElbowA => "팔꿈치 A", Joint.HandA => "손 A",
        Joint.ShoulderB => "어깨 B", Joint.ElbowB => "팔꿈치 B", Joint.HandB => "손 B",
        Joint.KneeA => "무릎 A", Joint.FootA => "발 A", Joint.KneeB => "무릎 B", _ => "발 B"
    };
    public void SetSource(PixelImage? image, Rig? rig, RegionMap? regions)
    {
        ImageData = image; Rig = rig; Regions = regions;
        bitmap = image == null ? null : Imaging.Bitmap(image);
        overlay = null; dragging = null; cursor = null;
        InvalidateVisual();
    }
    public void RefreshRegions() { overlay = null; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle((Brush)Application.Current.FindResource("Checker"), null, new Rect(RenderSize));
        double size = Math.Max(0, Math.Min(ActualWidth - 32, ActualHeight - 32));
        imageRect = new((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size);
        if (bitmap == null || Rig == null)
        {
            DrawText(dc, "투명 PNG를 추가해 주세요", new Point(24, 28), Brushes.LightSlateGray);
            return;
        }
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(bitmap, imageRect);
        if ((ShowRegions || PaintMode) && Regions != null)
        {
            overlay ??= MakeOverlay();
            dc.DrawImage(overlay, imageRect);
        }
        var ground = ToScreen(new(.5f, .9f));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 120, 200, 210)), 1),
            new Point(imageRect.Left, ground.Y), new Point(imageRect.Right, ground.Y));
        if (!PaintMode)
        {
            foreach (var bone in Rigging.Bones)
                dc.DrawLine(new Pen(new SolidColorBrush(PartColor(bone.Part)), 2),
                    ToScreen(Rig[bone.Start]), ToScreen(Rig[bone.End]));
            foreach (Joint joint in Enum.GetValues<Joint>())
            {
                var p = ToScreen(Rig[joint]);
                dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), p, dragging == joint ? 6 : 4.5, dragging == joint ? 6 : 4.5);
                if (dragging == joint) DrawText(dc, JointLabel(joint), new Point(p.X + 9, p.Y - 20), Brushes.White);
            }
        }
        if (PaintMode && cursor.HasValue)
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), ToScreen(cursor.Value),
                BrushRadius * imageRect.Width, BrushRadius * imageRect.Height);
    }
    private BitmapSource MakeOverlay()
    {
        var map = Regions!;
        var pixels = new byte[map.Width * map.Height * 4];
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                int sx = x * ImageData!.Width / map.Width, sy = y * ImageData.Height / map.Height;
                if (ImageData.Pixels[(sy * ImageData.Width + sx) * 4 + 3] == 0) continue;
                int i = (y * map.Width + x) * 4;
                var color = PartColor((BodyPart)map.Values[y * map.Width + x]);
                pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; pixels[i + 3] = 110;
            }
        return Imaging.Bitmap(new(map.Width, map.Height, pixels));
    }
    private Point ToScreen(Point2 p) => new(imageRect.X + p.X * imageRect.Width, imageRect.Y + p.Y * imageRect.Height);
    private Point2 ToNormalized(Point p) => new((float)Math.Clamp((p.X - imageRect.X) / imageRect.Width, 0, 1),
        (float)Math.Clamp((p.Y - imageRect.Y) / imageRect.Height, 0, 1));
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left || Rig == null || !imageRect.Contains(e.GetPosition(this))) return;
        var position = e.GetPosition(this);
        if (PaintMode)
        {
            painting = true; CaptureMouse(); Paint(position);
        }
        else
        {
            var nearest = Enum.GetValues<Joint>().OrderBy(j => (ToScreen(Rig[j]) - position).LengthSquared).First();
            if ((ToScreen(Rig[nearest]) - position).Length <= 18)
            { dragging = nearest; SelectionChanged?.Invoke(JointLabel(nearest)); CaptureMouse(); InvalidateVisual(); }
        }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Rig == null || imageRect.Width <= 0) return;
        cursor = ToNormalized(e.GetPosition(this));
        if (dragging.HasValue)
        { Rig[dragging.Value] = cursor.Value; Edited?.Invoke(); }
        if (painting) Paint(e.GetPosition(this));
        InvalidateVisual();
    }
    private void Paint(Point position)
    {
        if (Regions == null) return;
        var p = ToNormalized(position);
        Regions.Paint(p.X, p.Y, BrushRadius, PaintPart);
        overlay = null; Edited?.Invoke();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = null; painting = false; ReleaseMouseCapture(); InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e); dragging = null; painting = false;
    }
    private void DrawText(DrawingContext dc, string text, Point point, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), 13, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, point);
    }
}
