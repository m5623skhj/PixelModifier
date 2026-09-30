using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PixelModifier.Core;

namespace PixelModifier.App;

public sealed class SourceView : INotifyPropertyChanged
{
    public required PixelImage Original { get; init; }
    public required PixelImage EditorImage { get; init; }
    public required SourceDefinition Definition { get; init; }
    public string Name => Path.GetFileName(Definition.Path);
    public string PhaseLabel => $"주기 {Definition.Phase * 100:0.#}%";
    public void Refresh() => PropertyChanged?.Invoke(this, new(nameof(PhaseLabel)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CandidateView : INotifyPropertyChanged
{
    public required MotionVariant Variant { get; init; }
    public required GenerationSettings Settings { get; init; }
    public required BitmapSource[] Frames { get; init; }
    public required InputSnapshot[] Inputs { get; init; }
    public required MotionQuality Quality { get; init; }
    public string Title => $"{(Settings.Motion == MotionKind.Walk ? "걷기" : "달리기")}  {Variant.Index + 1:00}";
    public string Details => $"보폭 {Variant.Stride * 100:0.#} · 발 높이 {Variant.Lift * 100:0.#}";
    public string QualityLabel => $"동작 안정성 {Quality.Score:0}/100";
    private BitmapSource? preview;
    private bool isSelected;
    public BitmapSource? Preview { get => preview; set { preview = value; Notify(); } }
    public bool IsSelected { get => isSelected; set { isSelected = value; Notify(); } }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record InputSnapshot(PixelImage Original, Rig Rig, RegionMap Regions, float Phase);

public partial class MainWindow : Window
{
    public ObservableCollection<SourceView> Sources { get; } = [];
    public ObservableCollection<CandidateView> Candidates { get; } = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastTick;
    private double elapsed;
    private bool playing = true;
    private bool settingControls;
    private bool dirty;
    private bool busy;
    private string? projectPath;
    private CandidateView? selectedCandidate;
    private CancellationTokenSource? cancellation;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        RigCanvas.Edited += () => { MarkDirty(); ClearCandidates(); };
        RigCanvas.SelectionChanged += label => EditorHint.Text = $"{label} 위치를 끌어서 조정하세요.";
        timer.Tick += PlaybackTick;
        timer.Start();
        FrameCountBox.TextChanged += GenerationInputChanged;
        SeedBox.TextChanged += GenerationInputChanged;
        FacingBox.SelectionChanged += GenerationInputChanged;
        IntensitySlider.ValueChanged += GenerationInputChanged;
        CandidateCountBox.TextChanged += (_, _) => MarkDirty();
        CellSizeBox.TextChanged += (_, _) => MarkDirty();
        ColumnsBox.TextChanged += (_, _) => MarkDirty();
        FpsBox.TextChanged += (_, _) => MarkDirty();
        dirty = false;
    }

    public void LoadInitial(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                OpenProject(args[0]);
            else AddSources(args);
        }
        catch (Exception error) { ShowError(error); }
    }
    private void MarkDirty() { if (!settingControls) dirty = true; }
    private void GenerationInputChanged(object sender, RoutedEventArgs e)
    {
        if (settingControls) return;
        MarkDirty(); ClearCandidates();
    }
    private void ClearCandidates()
    {
        if (Candidates.Count == 0) return;
        Candidates.Clear(); selectedCandidate = null; SelectedPreview.Source = null;
        CandidateSummary.Text = "0개"; ExportButton.IsEnabled = false;
        FrameLabel.Text = "설정이 바뀌었습니다. 후보를 다시 생성해 주세요.";
    }
    private void AddPng_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "투명 PNG (*.png)|*.png", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        try { AddSources(dialog.FileNames); } catch (Exception error) { ShowError(error); }
    }
    private void AddSources(IEnumerable<string> paths)
    {
        string[] names = paths.ToArray();
        if (Sources.Count + names.Length > 64) throw new InvalidDataException("기준 이미지는 최대 64장까지 추가할 수 있습니다.");
        var loaded = new List<SourceView>();
        foreach (string path in names)
        {
            var original = Imaging.LoadPng(path);
            var editorImage = original.Normalize(256, 256);
            var rig = Rigging.Guess(editorImage);
            loaded.Add(new SourceView
            {
                Original = original, EditorImage = editorImage,
                Definition = new() { Path = Path.GetFullPath(path), Rig = rig, Regions = Rigging.GuessRegions(rig) }
            });
        }
        foreach (var source in loaded) Sources.Add(source);
        DistributePhases();
        SourcesList.SelectedIndex = Sources.Count - 1;
        int currentCount = int.TryParse(FrameCountBox.Text, out int parsedCount) ? parsedCount : 8;
        FrameCountBox.Text = Math.Max(Math.Max(2, currentCount), Sources.Count).ToString();
        MarkDirty(); ClearCandidates();
        Status.Text = $"{Sources.Count}장 입력됨. 관절 위치를 확인하고 필요하면 부위 경계를 고쳐 주세요.";
    }
    private void DistributePhases()
    {
        for (int i = 0; i < Sources.Count; i++)
        { Sources[i].Definition.Phase = (float)i / Sources.Count; Sources[i].Refresh(); }
    }
    private void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshSelectedSource();
    private void RefreshSelectedSource()
    {
        if (RigCanvas == null) return;
        var source = SourcesList.SelectedItem as SourceView;
        RigCanvas.SetSource(source?.EditorImage, source?.Definition.Rig, source?.Definition.Regions);
        bool previous = settingControls;
        settingControls = true;
        PhaseSlider.Value = source?.Definition.Phase * 100 ?? 0;
        PhaseBox.Text = PhaseSlider.Value.ToString("0.#", CultureInfo.CurrentCulture);
        settingControls = previous;
    }
    private void SourceUp_Click(object sender, RoutedEventArgs e) => MoveSource(-1);
    private void SourceDown_Click(object sender, RoutedEventArgs e) => MoveSource(1);
    private void MoveSource(int delta)
    {
        int index = SourcesList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Sources.Count) return;
        Sources.Move(index, target); DistributePhases(); SourcesList.SelectedIndex = target;
        RefreshSelectedSource();
        MarkDirty(); ClearCandidates();
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        int index = SourcesList.SelectedIndex;
        if (index < 0) return;
        Sources.RemoveAt(index); DistributePhases();
        SourcesList.SelectedIndex = Math.Min(index, Sources.Count - 1);
        RefreshSelectedSource();
        MarkDirty(); ClearCandidates();
    }
    private void Phase_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PhaseBox == null || settingControls || SourcesList.SelectedItem is not SourceView source) return;
        source.Definition.Phase = (float)e.NewValue / 100;
        source.Refresh(); PhaseBox.Text = e.NewValue.ToString("0.#", CultureInfo.CurrentCulture);
        MarkDirty(); ClearCandidates();
    }
    private void Phase_LostFocus(object sender, RoutedEventArgs e)
    {
        if (float.TryParse(PhaseBox.Text, out float value) && float.IsFinite(value) && value >= 0 && value < 100)
            PhaseSlider.Value = value;
        else { PhaseBox.Text = PhaseSlider.Value.ToString("0.#"); Status.Text = "주기 위치는 0 이상 100 미만으로 입력해 주세요."; }
    }
    private void Motion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FrameCountBox == null || settingControls) return;
        FrameCountBox.Text = MotionBox.SelectedIndex == 1 ? "10" : "8";
        FpsBox.Text = MotionBox.SelectedIndex == 1 ? "16" : "10";
        ColumnsBox.Text = MotionBox.SelectedIndex == 1 ? "5" : "4";
        MarkDirty(); ClearCandidates();
    }
    private void EditMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RigCanvas == null) return;
        RigCanvas.PaintMode = EditModeBox.SelectedIndex == 1;
        BrushTools.Visibility = RigCanvas.PaintMode ? Visibility.Visible : Visibility.Collapsed;
        EditorHint.Text = RigCanvas.PaintMode ? "부위를 선택하고 캐릭터 위에 칠해 경계를 고쳐 주세요." :
            "흰 점을 실제 관절 위치로 끌어주세요. A는 앞쪽, B는 뒤쪽 부위입니다.";
        RigCanvas.InvalidateVisual();
    }
    private void RegionDisplay_Changed(object sender, RoutedEventArgs e)
    {
        if (RigCanvas == null) return;
        RigCanvas.ShowRegions = RegionCheck.IsChecked == true; RigCanvas.InvalidateVisual();
    }
    private void Part_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RigCanvas != null) RigCanvas.PaintPart = (BodyPart)PartBox.SelectedIndex;
    }
    private void Brush_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RigCanvas != null) RigCanvas.BrushRadius = (float)e.NewValue;
    }
    private void ResetRegions_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceView source) return;
        source.Definition.Regions = Rigging.GuessRegions(source.Definition.Rig);
        RigCanvas.SetSource(source.EditorImage, source.Definition.Rig, source.Definition.Regions);
        MarkDirty(); ClearCandidates(); Status.Text = "현재 관절 위치를 기준으로 부위를 다시 추정했습니다.";
    }
    private GenerationSettings ReadSettings()
    {
        int cell = ParseInt(CellSizeBox, "셀 크기");
        return new()
        {
            Motion = (MotionKind)MotionBox.SelectedIndex, Facing = (Facing)FacingBox.SelectedIndex,
            FrameCount = ParseInt(FrameCountBox, "프레임 수"), CandidateCount = ParseInt(CandidateCountBox, "후보 수"),
            Seed = ParseInt(SeedBox, "난수 기준값"), Intensity = (float)IntensitySlider.Value,
            FramesPerSecond = ParseFloat(FpsBox, "재생 속도"), CellWidth = cell, CellHeight = cell,
            Columns = ParseInt(ColumnsBox, "열 수")
        };
    }
    private static int ParseInt(TextBox box, string name) =>
        int.TryParse(box.Text, out int value) ? value : throw new InvalidDataException($"{name}에 정수를 입력해 주세요.");
    private static float ParseFloat(TextBox box, string name) =>
        float.TryParse(box.Text, out float value) && float.IsFinite(value) ? value :
            throw new InvalidDataException($"{name}에 숫자를 입력해 주세요.");
    private InputSnapshot[] CaptureInputs() => Sources.Select(s => new InputSnapshot(s.Original,
        s.Definition.Rig.Clone(), s.Definition.Regions.Clone(), s.Definition.Phase)).ToArray();
    private static RenderSource[] RenderSources(InputSnapshot[] inputs, int size) => inputs.Select(s =>
        new RenderSource(s.Original.Normalize(size, size), s.Rig, s.Regions, s.Phase)).ToArray();

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var settings = ReadSettings(); settings.Validate(Sources.Count);
            var inputs = CaptureInputs();
            // Validate duplicate phases before allocating all candidate frames.
            var testSources = inputs.Select(s => new RenderSource(s.Original, s.Rig, s.Regions, s.Phase)).ToArray();
            Timeline.Plan(testSources, 0, MotionVariant.Create(0, settings), settings);
            ClearCandidates(); SetBusy(true);
            int size = Math.Clamp((int)Math.Sqrt(48 * 1024 * 1024d /
                (settings.FrameCount * settings.CandidateCount * 4d)), 32, 256);
            Progress.Maximum = settings.CandidateCount; Progress.Value = 0;
            var token = cancellation!.Token;
            var progress = new Progress<int>(count =>
            {
                Progress.Value = count;
                Status.Text = $"후보 생성 중 {count}/{settings.CandidateCount} · 미리보기 {size}px";
            });
            var results = await Task.Run(() =>
            {
                var sources = RenderSources(inputs, size);
                var list = new List<CandidateView>();
                for (int i = 0; i < settings.CandidateCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var variant = MotionVariant.Create(i, settings);
                    var cycle = MotionCycle.Create(sources, variant, settings, token);
                    var frames = new BitmapSource[settings.FrameCount];
                    for (int f = 0; f < frames.Length; f++)
                        frames[f] = Imaging.Bitmap(Renderer.Render(sources, cycle, f, variant, size, size, token));
                    list.Add(new() { Variant = variant, Settings = settings.Clone(), Frames = frames,
                        Inputs = inputs, Preview = frames[0], Quality = cycle.Quality });
                    ((IProgress<int>)progress).Report(i + 1);
                }
                return list.OrderByDescending(c => c.Quality.Score).ThenBy(c => c.Variant.Index).ToList();
            }, token);
            foreach (var candidate in results) Candidates.Add(candidate);
            CandidateSummary.Text = $"{Candidates.Count}개 · 안정성순";
            SelectCandidate(Candidates[0]);
            Status.Text = "동작 안정성이 높은 순서로 표시했습니다. 후보를 선택해 실제 움직임을 비교하세요.";
        }
        catch (OperationCanceledException) { Status.Text = "후보 생성을 취소했습니다."; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        busy = value;
        SettingsPanel.IsEnabled = !value; ProjectToolbar.IsEnabled = !value;
        EditorTools.IsEnabled = !value; RigCanvas.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value) cancellation = new CancellationTokenSource();
        else { cancellation?.Dispose(); cancellation = null; ExportButton.IsEnabled = selectedCandidate != null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void Candidate_Click(object sender, MouseButtonEventArgs e)
    {
        if (busy || (sender as FrameworkElement)?.DataContext is not CandidateView candidate) return;
        SelectCandidate(candidate);
    }
    private void SelectCandidate(CandidateView candidate)
    {
        if (selectedCandidate != null) selectedCandidate.IsSelected = false;
        selectedCandidate = candidate; candidate.IsSelected = true;
        elapsed = 0; playing = true; PlayButton.Content = "일시 정지";
        settingControls = true; FrameSlider.Maximum = candidate.Frames.Length - 1; FrameSlider.Value = 0; settingControls = false;
        SelectedPreview.Source = candidate.Frames[0]; ExportButton.IsEnabled = !busy;
        EditorTabs.SelectedIndex = 1; CenterTitle.Text = candidate.Title;
        UpdateFrame(0);
    }
    private void PlaybackTick(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        double delta = now - lastTick; lastTick = now;
        if (selectedCandidate == null || busy || !playing) return;
        float fps = float.TryParse(FpsBox.Text, out float value) && value > 0 && value <= 120 ? value :
            selectedCandidate.Settings.FramesPerSecond;
        elapsed += delta;
        int frame = (int)(elapsed * fps) % selectedCandidate.Frames.Length;
        UpdateFrame(frame);
    }
    private void UpdateFrame(int frame)
    {
        foreach (var candidate in Candidates) candidate.Preview = candidate.Frames[frame % candidate.Frames.Length];
        if (selectedCandidate == null) return;
        SelectedPreview.Source = selectedCandidate.Frames[frame];
        FrameLabel.Text = $"프레임 {frame + 1} / {selectedCandidate.Frames.Length}";
        settingControls = true; FrameSlider.Value = frame; settingControls = false;
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        playing = !playing; PlayButton.Content = playing ? "일시 정지" : "재생";
    }
    private void Frame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (settingControls || selectedCandidate == null) return;
        playing = false; PlayButton.Content = "재생";
        int frame = (int)e.NewValue;
        float fps = float.TryParse(FpsBox.Text, out float value) && value > 0 ? value : 10;
        elapsed = frame / (double)fps; UpdateFrame(frame);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || selectedCandidate == null) return;
        try
        {
            var candidate = selectedCandidate;
            var settings = candidate.Settings.Clone();
            settings.CellWidth = settings.CellHeight = ParseInt(CellSizeBox, "셀 크기");
            settings.Columns = ParseInt(ColumnsBox, "열 수");
            settings.FramesPerSecond = ParseFloat(FpsBox, "재생 속도");
            settings.Validate(candidate.Inputs.Length);
            var dialog = new SaveFileDialog
            {
                Filter = "PNG 스프라이트 시트 (*.png)|*.png", DefaultExt = ".png",
                FileName = $"{settings.Motion.ToString().ToLowerInvariant()}_{candidate.Variant.Index + 1:00}.png"
            };
            if (dialog.ShowDialog(this) != true) return;
            if (Sources.Any(s => string.Equals(Path.GetFullPath(s.Definition.Path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(projectPath, Path.ChangeExtension(dialog.FileName, ".json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("입력 이미지나 프로젝트와 다른 출력 파일 이름을 사용해 주세요.");
            string metadataPath = Path.ChangeExtension(dialog.FileName, ".json");
            if (File.Exists(metadataPath) &&
                MessageBox.Show(this, $"{Path.GetFileName(metadataPath)}도 교체됩니다. 저장할까요?", "출력 정보 교체",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetBusy(true); Progress.Maximum = settings.FrameCount; Progress.Value = 0;
            var token = cancellation!.Token;
            var progress = new Progress<int>(count => { Progress.Value = count; Status.Text = $"PNG 출력 중 {count}/{settings.FrameCount}"; });
            await Task.Run(() => ExportService.Export(RenderSources(candidate.Inputs, Math.Min(settings.CellWidth, settings.CellHeight)),
                settings, candidate.Variant, dialog.FileName, token, count => ((IProgress<int>)progress).Report(count)), token);
            Status.Text = $"저장 완료: {dialog.FileName} · PNG와 JSON";
        }
        catch (OperationCanceledException) { Status.Text = "PNG 출력을 취소했습니다."; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        try { SaveProject(); } catch (Exception error) { ShowError(error); }
    }
    private bool SaveProject()
    {
        var settings = ReadSettings(); settings.Validate(Math.Max(1, Sources.Count));
        var dialog = new SaveFileDialog
        {
            Filter = "PixelModifier 프로젝트 (*.pixelmodifier.json)|*.pixelmodifier.json",
            DefaultExt = ".pixelmodifier.json", FileName = projectPath == null ? "character.pixelmodifier.json" : Path.GetFileName(projectPath),
            InitialDirectory = projectPath == null ? "" : Path.GetDirectoryName(projectPath)
        };
        if (dialog.ShowDialog(this) != true) return false;
        if (Sources.Any(s => string.Equals(Path.GetFullPath(s.Definition.Path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("입력 이미지와 다른 프로젝트 파일 이름을 사용해 주세요.");
        new ProjectDocument { Settings = settings, Sources = Sources.Select(s => s.Definition).ToList() }.Save(dialog.FileName);
        projectPath = dialog.FileName; dirty = false; Status.Text = $"프로젝트 저장 완료: {projectPath}";
        return true;
    }
    private bool ConfirmProjectChange()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "현재 프로젝트 변경 사항을 저장할까요?", "프로젝트 저장",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveProject());
    }
    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ConfirmProjectChange()) return;
            var dialog = new OpenFileDialog { Filter = "PixelModifier 프로젝트 (*.json)|*.json" };
            if (dialog.ShowDialog(this) == true) OpenProject(dialog.FileName);
        }
        catch (Exception error) { ShowError(error); }
    }
    private void OpenProject(string path)
    {
        var doc = ProjectDocument.Load(path); doc.Settings.Validate(Math.Max(1, doc.Sources.Count));
        var loaded = doc.Sources.Select(source =>
        {
            var original = Imaging.LoadPng(source.Path);
            return new SourceView { Original = original, EditorImage = original.Normalize(256, 256), Definition = source };
        }).ToArray();
        settingControls = true;
        try
        {
            Sources.Clear(); foreach (var source in loaded) Sources.Add(source);
            var s = doc.Settings;
            MotionBox.SelectedIndex = (int)s.Motion; FacingBox.SelectedIndex = (int)s.Facing;
            FrameCountBox.Text = s.FrameCount.ToString(); CandidateCountBox.Text = s.CandidateCount.ToString();
            SeedBox.Text = s.Seed.ToString(); IntensitySlider.Value = s.Intensity;
            FpsBox.Text = s.FramesPerSecond.ToString(CultureInfo.CurrentCulture);
            CellSizeBox.Text = s.CellWidth.ToString(); ColumnsBox.Text = s.Columns.ToString();
            SourcesList.SelectedIndex = Sources.Count > 0 ? 0 : -1;
        }
        finally { settingControls = false; }
        projectPath = Path.GetFullPath(path); dirty = false; ClearCandidates();
        Status.Text = $"프로젝트를 열었습니다: {Path.GetFileName(path)}";
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { cancellation?.Cancel(); e.Cancel = true; Status.Text = "작업을 취소하고 있습니다. 완료 후 창을 닫아 주세요."; return; }
        try { if (!ConfirmProjectChange()) e.Cancel = true; } catch (Exception error) { ShowError(error); e.Cancel = true; }
        if (!e.Cancel) timer.Stop();
    }
    private void ShowError(Exception error)
    {
        Status.Text = error.Message;
        MessageBox.Show(this, error.Message, "입력을 확인해 주세요", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
