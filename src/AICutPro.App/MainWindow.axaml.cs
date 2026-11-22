using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using AICutPro.Core;

namespace AICutPro.App;

public partial class MainWindow : Window
{
    // One fail-closed, preview-only policy for DXF edits and exported geometry.
    // This does not grant a CAM or equipment-motion authorization.
    private static PreviewTopologyResult ValidatePreviewDrawing(
        IReadOnlyList<PreviewPath> rings, double toleranceMm = 0.01) =>
        PreviewGeometryTolerancePolicy.Create(toleranceMm, toleranceMm)
            .ValidateDrawing(rings);

    private NestingResult _result = ShelfNester.Arrange(SampleJob.Sheet, SampleJob.Parts);
    private int _step;
    private WorkspaceGeometry _workspace = WorkspaceGeometry.Default;
    private VirtualMachine _virtualMachine = VirtualMachine.Create(WorkspaceGeometry.Default);
    private DxfImportResult? _dxfPreview;
    private DxfPreviewHistory? _dxfHistory;
    private IReadOnlyList<DxfDiagnosticFinding> _dxfDiagnostics = [];
    private int? _selectedPlacement;
    private double _zoom = 1;
    private double _panX, _panY;
    private double _viewScale, _viewOx, _viewOy;
    private bool _viewInvertsY;
    private double _viewMaxY;
    private bool _panning;
    private Avalonia.Point _lastPointer;

    public MainWindow()
    {
        InitializeComponent();
        SheetCanvas.PointerWheelChanged += CanvasPointerWheelChanged;
        SheetCanvas.PointerPressed += CanvasPointerPressed;
        SheetCanvas.PointerMoved += CanvasPointerMoved;
        SheetCanvas.PointerReleased += CanvasPointerReleased;
        UpdateSummary();
        // Open a DXF directly from the command line, without a file-picker.
        // This is also useful for file associations and UI acceptance tests.
        Opened += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length != 3 || args[1] != "--open-dxf") return;
            try
            {
                var file = new FileInfo(args[2]);
                if (file.Length is <= 0 or > 8_000_000)
                    throw new FormatException("DXF file must contain 1..8000000 bytes.");
                ImportDxfBytes(File.ReadAllBytes(file.FullName));
            }
            catch (Exception ex) { ReportDxfImportFailure(ex); }
        };
    }

    private void UpdateSummary()
    {
        WorkspaceText.Text = $"Стол: {_workspace.Table.WidthMm:0.###} × {_workspace.Table.HeightMm:0.###} мм\\nЛист: {_workspace.MaterialSheet.WidthMm:0.###} × {_workspace.MaterialSheet.HeightMm:0.###} мм".Replace("\\n", "\n");
        PartsText.Text = $"Деталей на листе: {_result.Placements.Count}";
        UtilizationText.Text = $"Использовано: {_result.UtilizationPercent:F1} %";
        WasteText.Text = $"Свободно: {_result.WasteAreaMm2 / 1_000_000:F2} м²";
        UpdatePartCard();
        UpdateHoleSummary();
        UpdateMachineCard();
        RenderSheet();
    }

    private void UpdateMachineCard()
    {
        var head = _virtualMachine.MachineCoordinates;
        var relative = _virtualMachine.WorkCoordinates;
        CoordinatesText.Text = $"Машинные: X {head.X:0.###}, Y {head.Y:0.###}, Z {head.Z:0.###} мм\n" +
            $"Рабочие: X {relative.X:0.###}, Y {relative.Y:0.###}, Z {relative.Z:0.###} мм";
        CheckpointText.Text = "Точки листа:\n" + string.Join("\n",
            _virtualMachine.Checkpoints.Select(p => $"{p.Name}: X {p.Position.X:0.###}, Y {p.Position.Y:0.###}"));
    }

    private void UpdatePartCard()
    {
        if (_dxfPreview is not null)
        {
            SelectedPartText.Text = "DXF: просмотр геометрии. Выбор и редактор отдельных CAD-профилей пока не реализованы.";
            return;
        }
        if (_selectedPlacement is not int index || index < 0 || index >= _result.Placements.Count)
        {
            SelectedPartText.Text = "Не выбрана. Щёлкните по прямоугольнику на листе.";
            return;
        }
        var p = _result.Placements[index];
        SelectedPartText.Text = $"{p.Name} №{p.Index + 1}\nРазмер: {p.WidthMm:0.###} × {p.HeightMm:0.###} мм\nX: {p.Xmm:0.###} мм; Y: {p.Ymm:0.###} мм\nПоворот: {(p.Rotated ? "90°" : "нет")}\nГеометрия: демонстрационный прямоугольник";
    }

    private void UpdateHoleSummary()
    {
        if (_dxfPreview is null)
        {
            HoleSummaryText.Text = "Демонстрация: отверстия не заданы.";
            return;
        }
        var circles = _dxfPreview.Shapes.OfType<DxfCircle>().ToArray();
        if (circles.Length == 0)
        {
            HoleSummaryText.Text = "Круговых элементов нет.";
            return;
        }
        HoleSummaryText.Text = string.Join("\n", circles
            .GroupBy(circle => Math.Round(circle.Radius * 2, 3))
            .OrderBy(group => group.Key)
            .Select(group => $"Ø {group.Key:0.###} мм: {group.Count()} шт."))
            + "\nЭто сводка всех CIRCLE в DXF, классификация отверстий требует проверки.";
    }

    private void SetView(double minX, double minY, double maxX, double maxY, bool invertY)
    {
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var fit = Math.Min(Math.Max(1, SheetCanvas.Bounds.Width - 28) / width,
                           Math.Max(1, SheetCanvas.Bounds.Height - 28) / height);
        _viewScale = fit * _zoom;
        _viewOx = 14 - minX * _viewScale + _panX;
        _viewOy = 14 + (invertY ? maxY : -minY) * _viewScale + _panY;
        _viewInvertsY = invertY;
        _viewMaxY = maxY;
    }

    private double ScreenX(double x) => _viewOx + x * _viewScale;
    private double ScreenY(double y) => _viewOy + (_viewInvertsY ? -y : y) * _viewScale;

    private void RenderSheet()
    {
        if (SheetCanvas.Bounds.Width < 40 || SheetCanvas.Bounds.Height < 40) return;
        SheetCanvas.Children.Clear();
        if (_dxfPreview is not null) { RenderDxf(); return; }
        SetView(0, 0, _workspace.Table.WidthMm, _workspace.Table.HeightMm, false);
        var table = new Rectangle
        {
            Width = _workspace.Table.WidthMm * _viewScale,
            Height = _workspace.Table.HeightMm * _viewScale,
            Stroke = Brush.Parse("#A6B6C7"), StrokeThickness = 2,
            Fill = Brush.Parse("#101D2A")
        };
        Canvas.SetLeft(table, ScreenX(0));
        Canvas.SetTop(table, ScreenY(0));
        SheetCanvas.Children.Add(table);
        var sheet = new Rectangle
        {
            Width = _result.Sheet.WidthMm * _viewScale,
            Height = _result.Sheet.HeightMm * _viewScale,
            Stroke = Brush.Parse("#2EA5FF"), StrokeThickness = 2,
            Fill = Brush.Parse("#1D344D")
        };
        Canvas.SetLeft(sheet, ScreenX(0));
        Canvas.SetTop(sheet, ScreenY(0));
        SheetCanvas.Children.Add(sheet);
        foreach (var checkpoint in _virtualMachine.Checkpoints)
        {
            var px = ScreenX(checkpoint.Position.X);
            var py = ScreenY(checkpoint.Position.Y);
            SheetCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(px - 5, py),
                EndPoint = new Avalonia.Point(px + 5, py),
                Stroke = Brush.Parse("#FFD36B"), StrokeThickness = 2
            });
            SheetCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(px, py - 5),
                EndPoint = new Avalonia.Point(px, py + 5),
                Stroke = Brush.Parse("#FFD36B"), StrokeThickness = 2
            });
        }
        var headPos = _virtualMachine.Head;
        var headMarker = new Ellipse
        {
            Width = 10, Height = 10, Stroke = Brush.Parse("#FFFFFF"),
            StrokeThickness = 2, Fill = Brush.Parse("#F27067")
        };
        Canvas.SetLeft(headMarker, ScreenX(headPos.X) - 5);
        Canvas.SetTop(headMarker, ScreenY(headPos.Y) - 5);
        SheetCanvas.Children.Add(headMarker);
        foreach (var (item, index) in _result.Placements.Select((p, i) => (p, i)))
        {
            var selected = _selectedPlacement == index;
            var rect = new Rectangle
            {
                Width = item.WidthMm * _viewScale, Height = item.HeightMm * _viewScale,
                RadiusX = 4, RadiusY = 4,
                Stroke = Brush.Parse(selected ? "#FFD36B" : "#71D1FF"),
                StrokeThickness = selected ? 3 : 1.5,
                Fill = Brush.Parse(index < _step ? "#42AD89" : "#557A9D")
            };
            Canvas.SetLeft(rect, ScreenX(item.Xmm));
            Canvas.SetTop(rect, ScreenY(item.Ymm));
            SheetCanvas.Children.Add(rect);
        }
    }

    private void ResetView()
    {
        _zoom = 1;
        _panX = _panY = 0;
        _selectedPlacement = null;
    }

    private void CanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pointer = e.GetPosition(SheetCanvas);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 40);
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;
        var ratio = newZoom / _zoom;
        _panX = (pointer.X - 14) * (1 - ratio) + _panX * ratio;
        _panY = (pointer.Y - 14) * (1 - ratio) + _panY * ratio;
        _zoom = newZoom;
        RenderSheet();
        e.Handled = true;
    }

    private void CanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var current = e.GetCurrentPoint(SheetCanvas);
        if (current.Properties.IsRightButtonPressed || current.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _lastPointer = current.Position;
            e.Pointer.Capture(SheetCanvas);
            e.Handled = true;
            return;
        }
        if (!current.Properties.IsLeftButtonPressed || _dxfPreview is not null) return;
        var pt = current.Position;
        _selectedPlacement = null;
        for (var i = _result.Placements.Count - 1; i >= 0; i--)
        {
            var p = _result.Placements[i];
            if (pt.X >= ScreenX(p.Xmm) && pt.X <= ScreenX(p.Xmm + p.WidthMm) &&
                pt.Y >= ScreenY(p.Ymm) && pt.Y <= ScreenY(p.Ymm + p.HeightMm))
            {
                _selectedPlacement = i;
                break;
            }
        }
        UpdatePartCard();
        RenderSheet();
        e.Handled = true;
    }

    private void CanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var pt = e.GetPosition(SheetCanvas);
        _panX += pt.X - _lastPointer.X;
        _panY += pt.Y - _lastPointer.Y;
        _lastPointer = pt;
        RenderSheet();
    }

    private void CanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void RenderDxf()
    {
        if (_dxfPreview == null) return;
        var points = new List<Point2>();
        foreach (var shape in _dxfPreview.Shapes)
        {
            switch (shape)
            {
                case DxfLine line:
                    points.Add(line.Start); points.Add(line.End);
                    break;
                case DxfArc arc:
                    points.AddRange(DxfArcPreview.Sample(arc));
                    break;
                case DxfCircle circle:
                    points.Add(new Point2(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius));
                    points.Add(new Point2(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius));
                    break;
                case DxfPolyline poly:
                    points.AddRange(poly.Vertices);
                    break;
                case DxfBulgedPolyline curved:
                    var curvedPoints = VirtualToolpath.Sample(DxfBulgePreview.Build(curved));
                    points.AddRange(curvedPoints.Points.Select(p => new Point2(p.X, p.Y)));
                    break;
            }
        }
        if (points.Count == 0) return;
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        SetView(minX, minY, maxX, maxY, true);
        var scale = _viewScale;
        if (!double.IsFinite(scale) || scale <= 0) return;
        double X(double x) => ScreenX(x);
        double Y(double y) => ScreenY(y);
        var stroke = Brush.Parse("#75D2FF");
        void AddLine(Point2 a, Point2 b)
        {
            SheetCanvas.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(X(a.X), Y(a.Y)),
                EndPoint = new Avalonia.Point(X(b.X), Y(b.Y)),
                Stroke = stroke,
                StrokeThickness = 1.6
            });
        }
        foreach (var shape in _dxfPreview.Shapes)
        {
            switch (shape)
            {
                case DxfLine line:
                    AddLine(line.Start, line.End);
                    break;
                case DxfArc arc:
                    var arcPoints = DxfArcPreview.Sample(arc);
                    for (var i = 1; i < arcPoints.Count; i++) AddLine(arcPoints[i-1], arcPoints[i]);
                    break;
                case DxfCircle circle:
                    var ellipse = new Ellipse
                    {
                        Width = 2 * circle.Radius * scale,
                        Height = 2 * circle.Radius * scale,
                        Stroke = stroke, StrokeThickness = 1.6
                    };
                    Canvas.SetLeft(ellipse, X(circle.Center.X - circle.Radius));
                    Canvas.SetTop(ellipse, Y(circle.Center.Y + circle.Radius));
                    SheetCanvas.Children.Add(ellipse);
                    break;
                case DxfPolyline poly:
                    for (var i = 1; i < poly.Vertices.Count; i++) AddLine(poly.Vertices[i - 1], poly.Vertices[i]);
                    if (poly.Closed) AddLine(poly.Vertices[^1], poly.Vertices[0]);
                    break;
                case DxfBulgedPolyline curved:
                    var curvedSample = VirtualToolpath.Sample(DxfBulgePreview.Build(curved));
                    for (var i = 1; i < curvedSample.Points.Count; i++)
                        AddLine(new Point2(curvedSample.Points[i - 1].X, curvedSample.Points[i - 1].Y),
                                new Point2(curvedSample.Points[i].X, curvedSample.Points[i].Y));
                    break;
            }
        }
    }

    private void ShowDxfDiagnosticsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var diagnostics = new DxfDiagnosticsWindow(_dxfDiagnostics);
        diagnostics.Show();
        StatusText.Text = $"Открыта диагностика DXF: {_dxfDiagnostics.Count} замечаний. CAM и резка запрещены.";
    }

    private async void ImportDxfClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть DXF для просмотра",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("DXF") { Patterns = ["*.dxf"] }]
            });
            if (selected.Count == 0) return;
            _dxfDiagnostics = []; // A new attempt must not show stale findings.
            await using var stream = await selected[0].OpenReadAsync();
            using var data = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory())) > 0)
            {
                if (data.Length + read > 8_000_000)
                    throw new FormatException("DXF exceeds maximum supported size (8 MB).");
                data.Write(buffer, 0, read);
            }
            ImportDxfBytes(data.ToArray());
        }
        catch (Exception ex) { ReportDxfImportFailure(ex); }
    }

    private void ImportDxfBytes(byte[] bytes)
    {
        try
        {
            _dxfDiagnostics = [];
            if (bytes.Length is <= 0 or > 8_000_000)
                throw new FormatException("DXF file must contain 1..8000000 bytes.");
            var loaded = DxfFileReader.Read(bytes);
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
            {
                StatusText.Text = "Укажите допуск DXF от 0,0001 до 1 мм. Импорт отклонён.";
                return;
            }
            var imported = loaded.Document;
            var conversionNote = "";
            var confirmedUnits = DxfUnitsBox.Text?.Trim();
            if (!string.IsNullOrEmpty(confirmedUnits))
            {
                if (!int.TryParse(confirmedUnits, NumberStyles.None, CultureInfo.InvariantCulture,
                                  out var unitCode))
                {
                    StatusText.Text = "Единицы DXF: используйте код 1, 2, 4, 5 или 6.";
                    return;
                }
                var conversion = DxfUnitConverter.ToMillimetres(imported, unitCode);
                imported = conversion.Document;
                conversionNote = $" Подтверждены исходные единицы {unitCode}; масштаб ×{conversion.MillimetresPerUnit:0.####} в мм.";
            }
            var mixedTopology = imported.InsUnits == 4
                ? MixedContourAssembler.Assemble(imported.Shapes, toleranceMm)
                : new MixedContourAssembly([], ["Единицы не подтверждены как миллиметры."], false);
            var gate = GeometryPreflight.Inspect(imported, _workspace.MaterialSheet);
            var fullDiagnosticIssues = new List<string>();
            fullDiagnosticIssues.AddRange(gate.BlockingIssues);
            fullDiagnosticIssues.AddRange(mixedTopology.Warnings);
            var contourMeasurements = mixedTopology.Complete
                ? mixedTopology.ClosedPaths.Select(path => PreviewContourMetrics.Measure(path, toleranceMm)).ToArray()
                : [];
            var contourNote = contourMeasurements.Length > 0 &&
                              contourMeasurements.All(m => m.IsValid)
                ? " Контуры LINE/ARC/bulge: " +
                  string.Join("; ", contourMeasurements.Take(3).Select((m, i) =>
                      $"№{i + 1}: {m.AreaMm2:0.###} мм², {m.PerimeterMm:0.###} мм, {m.Winding}")) +
                  (contourMeasurements.Length > 3 ? "; ..." : "") +
                  ". Только отдельные площади контуров, не чистая площадь детали."
                : "";
            if (mixedTopology.Complete && mixedTopology.ClosedPaths.Count > 0)
            {
                var bounds = mixedTopology.ClosedPaths
                    .Select(PreviewContourBounds.Measure).ToArray();
                if (bounds.All(b => b.IsValid))
                {
                    var left=bounds.Min(b=>b.MinX);
                    var bottom=bounds.Min(b=>b.MinY);
                    var right=bounds.Max(b=>b.MaxX);
                    var top=bounds.Max(b=>b.MaxY);
                    contourNote += $" Аналитический габарит контуров: " +
                        $"{right-left:0.###} × {top-bottom:0.###} мм " +
                        $"(X {left:0.###}…{right:0.###}; Y {bottom:0.###}…{top:0.###}).";
                }
                else contourNote += " Габариты не подтверждены: " +
                    string.Join("; ",bounds.Where(b=>!b.IsValid)
                        .SelectMany(b=>b.Issues).Take(2));
            }
            // Separate read-only analytic nesting inspection. Do not confuse it with
            // the stricter manufacturing preflight or a cutting permit.
            if (mixedTopology.Complete && mixedTopology.ClosedPaths.Count > 0)
            {
                var policy = PreviewGeometryTolerancePolicy.Create(toleranceMm, toleranceMm);
                var nested = policy.ValidateDrawing(mixedTopology.ClosedPaths);
                if (!nested.IsValid)
                    fullDiagnosticIssues.AddRange(nested.Issues);
                contourNote += nested.IsValid
                    ? $" Аналитическая вложенность: {nested.Rings.Count} контур(ов), " +
                      $"площадь с учётом внутренних контуров {nested.NetAreaMm2:0.###} мм². " +
                      "Не является допуском CAD/CAM."
                    : " Аналитическая вложенность не подтверждена: " +
                      string.Join("; ", nested.Issues.Take(2));
            }
            _dxfHistory = new DxfPreviewHistory(imported);
            _dxfPreview = _dxfHistory.Current;
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: только просмотр контуров, не для резки";
            var encodingNote = $"DXF: {loaded.EncodingName}; версия: {loaded.AcadVersion ?? "не указана"}. Допуск {toleranceMm:0.####} мм." + conversionNote + " ";
            var issues = gate.BlockingIssues.Take(2).ToArray();
            StatusText.Text = encodingNote + (gate.Status == GeometryPreflightStatus.Rejected
                ? $"DXF: проверка геометрии НЕ пройдена. {string.Join("; ", issues)}. Резка запрещена."
                : $"DXF: {gate.Profiles.Count} деталь(ей), чистая площадь {gate.NetAreaMm2 / 1_000_000:F3} м². " +
                  $"Геометрия прошла предварительную проверку; ещё требуется CAM и проверка станка. Резка запрещена.");
            StatusText.Text += contourNote;
            // Preview remains visible even for rejected geometry. Surface a
            // stable, offline diagnostic with a repair explanation; do not
            // mistake a readable DXF for an approved manufacturing profile.
            var geometryIssues = fullDiagnosticIssues
                .Distinct(StringComparer.Ordinal).ToArray();
            _dxfDiagnostics = geometryIssues
                .Select(issue => new DxfDiagnosticFinding(
                    DxfDiagnosticCatalog.ClassifyGeometry(issue), issue)).ToArray();
            if (geometryIssues.Length > 0)
            {
                StatusText.Text += " Диагностика: " +
                    string.Join(" | ", geometryIssues.Take(3)
                        .Select(DxfDiagnosticCatalog.ExplainGeometryRu));
                if (geometryIssues.Length > 3)
                    StatusText.Text += $" (ещё {geometryIssues.Length - 3} замечаний; полная панель диагностики в разработке).";
            }
            RenderSheet();
        }
        catch (Exception ex) { ReportDxfImportFailure(ex); }
    }

    private void ReportDxfImportFailure(Exception ex)
    {
        _dxfDiagnostics =
        [
            new DxfDiagnosticFinding(DxfDiagnosticCatalog.Classify(ex), ex.Message)
        ];
        StatusText.Text = "Не удалось прочитать DXF. " + DxfDiagnosticCatalog.ExplainRu(ex) +
                          " Откройте «Диагностика DXF (все замечания)» для подробностей.";
    }

    // Only call after an edit has completed its existing topology and DXF
    // round-trip checks. Commit both the preview and its undo snapshot together.
    private void CommitDxfPreview(DxfImportResult verified)
    {
        if (_dxfHistory is null)
            throw new InvalidOperationException("No active DXF editing session.");
        var rules=_dxfHistory.CurrentAxisRules;
        if(rules.Count>0)
        {
            if(_dxfPreview is null)
                throw new FormatException("No source drawing for pinned constraints.");
            if(!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Invalid geometric tolerance.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var after=PreviewDxfExporter.ValidateDocument(verified,toleranceMm);
            PreviewPersistentAxisConstraints.RequireCompatibleEdit(
                before,after,rules,toleranceMm);
        }
        var lengths=_dxfHistory.CurrentLengthRules;
        if(lengths.Count>0)
        {
            if(_dxfPreview is null)
                throw new FormatException("No source drawing for pinned lengths.");
            if(!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Invalid geometric tolerance.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var after=PreviewDxfExporter.ValidateDocument(verified,toleranceMm);
            PreviewPersistentLengthConstraints.RequireCompatibleEdit(
                before,after,lengths,toleranceMm);
        }
        _dxfHistory.Push(verified,rules,lengths);
        _dxfPreview = _dxfHistory.Current;
    }

    private void TranslateWholeDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyRigidDxfEdit("перенос");

    private void RotateWholeDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyRigidDxfEdit("поворот");

    private void MirrorWholeDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyRigidDxfEdit("зеркало");

    private void MirrorHorizontalDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyRigidDxfEdit("горизонтальное зеркало");

    private void ScaleWholeDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyRigidDxfEdit("масштабирование");

    private void ApplyRigidDxfEdit(string operation)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте 2D-эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Неверный геометрический допуск.");
            static bool ParseValue(string? value, out double result) =>
                (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
                double.IsFinite(result) && Math.Abs(result) <= 1_000_000;

            var before = PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            PreviewVertexEditResult edited;
            switch (operation)
            {
                case "перенос":
                    if (!ParseValue(ExportMoveXBox.Text,out var dx) ||
                        !ParseValue(ExportMoveYBox.Text,out var dy))
                        throw new FormatException("Неверные значения переноса X/Y.");
                    edited = PreviewRigidTransformEditor.Translate(before,dx,dy,toleranceMm);
                    break;
                case "поворот":
                    if (!ParseValue(ExportRotationBox.Text,out var angle) ||
                        Math.Abs(angle)>360_000)
                        throw new FormatException("Неверный угол поворота.");
                    if (!ParseValue(RotationPivotXBox.Text,out var pivotX) ||
                        !ParseValue(RotationPivotYBox.Text,out var pivotY))
                        throw new FormatException("Неверные координаты центра поворота X/Y.");
                    edited = PreviewRigidTransformEditor.Rotate(
                        before,new PreviewPoint(pivotX,pivotY),angle,toleranceMm);
                    break;
                case "зеркало":
                    if (!ParseValue(MirrorAxisXBox.Text,out var axisX))
                        throw new FormatException("Неверная вертикальная ось зеркала.");
                    edited = PreviewRigidTransformEditor.MirrorVertical(
                        before,axisX,toleranceMm);
                    break;
                case "горизонтальное зеркало":
                    if (!ParseValue(MirrorAxisYBox.Text,out var axisY))
                        throw new FormatException("Неверная горизонтальная ось зеркала.");
                    edited = PreviewRigidTransformEditor.MirrorHorizontal(
                        before,axisY,toleranceMm);
                    break;
                case "масштабирование":
                    if (!ParseValue(ScaleFactorBox.Text,out var factor) ||
                        factor < 0.01 || factor > 100 ||
                        !ParseValue(ScalePivotXBox.Text,out var scalePivotX) ||
                        !ParseValue(ScalePivotYBox.Text,out var scalePivotY))
                        throw new FormatException("Неверный масштаб или центр X/Y.");
                    edited = PreviewRigidTransformEditor.ScaleUniform(
                        before,new PreviewPoint(scalePivotX,scalePivotY),
                        factor,toleranceMm);
                    break;
                default:
                    throw new FormatException("Неизвестное геометрическое преобразование.");
            }
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if (imported.Warnings.Count>0)
                throw new FormatException("Повторно импортированный DXF содержит предупреждения.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if (!topology.IsValid || reopened.Count!=edited.Rings.Count ||
                Math.Abs(topology.NetAreaMm2-edited.NetAreaMm2)>
                    Math.Max(1e-6,edited.NetAreaMm2*1e-9))
                throw new FormatException("Преобразование не прошло повторную проверку DXF.");
            CommitDxfPreview(imported);
            // Export uses these same controls for optional output-only transforms.
            // Clear parameters already applied to the active drawing so saving
            // cannot accidentally repeat the move or rotation.
            if (operation == "перенос")
                ExportMoveXBox.Text = ExportMoveYBox.Text = "0";
            else if (operation == "поворот")
                ExportRotationBox.Text = "0";
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: "+operation+" всех контуров, только предпросмотр";
            StatusText.Text=$"Преобразование «{operation}» применено. " +
                $"Контуров: {reopened.Count}; площадь {topology.NetAreaMm2:0.###} мм². " +
                "Доступны Undo/Redo и сохранение DXF. CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Преобразование DXF отклонено: "+ex.Message;
        }
    }

    private void UndoDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_dxfHistory is null || !_dxfHistory.CanUndo)
        {
            StatusText.Text = "Нет изменений DXF для отмены.";
            return;
        }
        _dxfPreview = _dxfHistory.Undo();
        ResetView();
        UpdatePartCard();
        UpdateHoleSummary();
        PanelTitle.Text = "DXF: отмена изменения, только предпросмотр";
        StatusText.Text = $"Изменение отменено. Доступно отмен: {_dxfHistory.UndoCount}, повторов: {_dxfHistory.RedoCount}. CAM и резка запрещены.";
        RenderSheet();
    }

    private void RedoDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_dxfHistory is null || !_dxfHistory.CanRedo)
        {
            StatusText.Text = "Нет изменений DXF для повтора.";
            return;
        }
        _dxfPreview = _dxfHistory.Redo();
        ResetView();
        UpdatePartCard();
        UpdateHoleSummary();
        PanelTitle.Text = "DXF: повтор изменения, только предпросмотр";
        StatusText.Text = $"Изменение повторено. Доступно отмен: {_dxfHistory.UndoCount}, повторов: {_dxfHistory.RedoCount}. CAM и резка запрещены.";
        RenderSheet();
    }

    private void EditDxfVertexClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте 2D-эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var vertexNumber) ||
                ringNumber < 1 || vertexNumber < 1)
                throw new FormatException("Номер контура и вершины должен быть положительным.");
            static bool Number(string? raw,out double value) =>
                (double.TryParse(raw,NumberStyles.Float,CultureInfo.CurrentCulture,out value) ||
                 double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out value)) &&
                double.IsFinite(value);
            if (!Number(EditVertexXBox.Text,out var x) ||
                !Number(EditVertexYBox.Text,out var y))
                throw new FormatException("Неверные координаты вершины.");
            var verified=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var rules=_dxfHistory?.CurrentAxisRules ?? Array.Empty<PreviewAxisRule>();
            var lengths=_dxfHistory?.CurrentLengthRules ??
                Array.Empty<PreviewLengthRule>();
            var selectedVertex=vertexNumber-1;
            var selectedRing=ringNumber-1;
            if(selectedRing<0||selectedRing>=verified.Count ||
               selectedVertex<0||selectedVertex>=verified[selectedRing].Segments.Count)
                throw new FormatException("Номер контура или вершины вне диапазона.");
            var n=verified[selectedRing].Segments.Count;
            var touchingLength=lengths.Any(rule=>rule.RingIndex==selectedRing &&
                (rule.SegmentIndex==selectedVertex ||
                 rule.SegmentIndex==(selectedVertex+n-1)%n));
            var edited=touchingLength
                ? PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
                    verified,selectedRing,selectedVertex,new PreviewPoint(x,y),
                    lengths,rules,toleranceMm)
                : PreviewPersistentAxisConstraints.MoveVertexConstrained(
                    verified,selectedRing,selectedVertex,new PreviewPoint(x,y),
                    rules,toleranceMm);
            // Reimport the proposed drawing before replacing current preview.
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            var reopened=PreviewDxfRoundTripVerifier.Verify(edited.Rings,serialized,toleranceMm);
            var checkedTopology=ValidatePreviewDrawing(reopened,toleranceMm);
            if (!checkedTopology.IsValid || reopened.Count != edited.Rings.Count ||
                Math.Abs(checkedTopology.NetAreaMm2-edited.NetAreaMm2)>
                    Math.Max(1e-6,edited.NetAreaMm2*1e-9))
                throw new FormatException("Изменённый DXF не прошёл повторную проверку.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: проверенное изменение вершины, только предпросмотр";
            var actualVertex=((PreviewLine)edited.Rings[ringNumber-1].Segments[vertexNumber-1]).Start;
            StatusText.Text=$"Контур №{ringNumber}, вершина №{vertexNumber}: " +
                $"X {actualVertex.X:0.###}, Y {actualVertex.Y:0.###} мм; " +
                $"чистая площадь {checkedTopology.NetAreaMm2:0.###} мм². " +
                "Изменение готово к отдельному сохранению DXF, CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text="Редактирование вершины отклонено: "+ex.Message;
        }
    }

    private void PropagateConstrainedVerticesClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if(_dxfPreview is null||_dxfHistory is null)
                throw new FormatException("Сначала откройте DXF.");
            if(!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if(!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber)||
               !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var vertexNumber)||
               ringNumber<1||vertexNumber<1)
                throw new FormatException("Номера контура и вершины должны быть положительными.");
            static bool Number(string? raw,out double value)=>
                (double.TryParse(raw,NumberStyles.Float,CultureInfo.CurrentCulture,out value)||
                 double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out value))&&
                double.IsFinite(value);
            if(!Number(EditVertexXBox.Text,out var x)||
               !Number(EditVertexYBox.Text,out var y))
                throw new FormatException("Некорректные координаты вершины.");
            var original=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var rules=_dxfHistory.CurrentAxisRules;
            var updated=PreviewAxisPropagationEditor.MoveConnectedVertices(
                original,ringNumber-1,vertexNumber-1,new PreviewPoint(x,y),
                rules,toleranceMm);
            var serialized=PreviewDxfExporter.Export(updated.Rings,toleranceMm);
            var candidate=DxfReader.Parse(serialized);
            if(candidate.Warnings.Count>0)
                throw new FormatException("DXF содержит предупреждения после правки.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                updated.Rings,serialized,toleranceMm);
            PreviewPersistentAxisConstraints.RequireCompatibleEdit(
                original,reopened,rules,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if(!topology.IsValid||reopened.Count!=updated.Rings.Count||
               Math.Abs(topology.NetAreaMm2-updated.NetAreaMm2)>
                   Math.Max(1e-6,Math.Abs(updated.NetAreaMm2)*1e-9))
                throw new FormatException("Пересчёт не прошёл проверку DXF.");
            CommitDxfPreview(candidate);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: пересчёт связанных вершин по ограничениям";
            StatusText.Text=$"Контур №{ringNumber}, вершина №{vertexNumber}: "+
                $"X {x:0.###}, Y {y:0.###} мм; "+
                $"площадь {topology.NetAreaMm2:0.###} мм². "+
                "Связанные координаты обновлены; Undo/Redo доступны. Резка запрещена.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Пересчёт связанных вершин отклонён: "+ex.Message;
        }
    }

    private void ChamferDxfVertexClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var vertexNumber) ||
                ringNumber<1 || vertexNumber<1)
                throw new FormatException("Номера контура и вершины должны быть положительными.");
            if (!(double.TryParse(ChamferSetbackBox.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var setback) ||
                  double.TryParse(ChamferSetbackBox.Text,NumberStyles.Float,
                    CultureInfo.InvariantCulture,out setback)) ||
                !double.IsFinite(setback))
                throw new FormatException("Неверный отступ фаски.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var edited=PreviewChamferEditor.ChamferVertex(
                before,ringNumber-1,vertexNumber-1,setback,toleranceMm);
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if (imported.Warnings.Count>0)
                throw new FormatException("DXF после фаски содержит предупреждения.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if (!topology.IsValid || reopened.Count!=edited.Rings.Count ||
                Math.Abs(topology.NetAreaMm2-edited.NetAreaMm2)>
                    Math.Max(1e-6,Math.Abs(edited.NetAreaMm2)*1e-9))
                throw new FormatException("Фаска не прошла независимый повторный импорт DXF.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: фаска выбранного угла, только предпросмотр";
            StatusText.Text=$"Фаска: контур №{ringNumber}, вершина №{vertexNumber}, "+
                $"отступ {setback:0.###} мм; площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны, CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Создание фаски отклонено: "+ex.Message;
        }
    }

    private void FilletDxfVertexClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var vertexNumber) ||
                ringNumber<1 || vertexNumber<1)
                throw new FormatException("Номера контура и вершины должны быть положительными.");
            if (!(double.TryParse(FilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var radius) ||
                  double.TryParse(FilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.InvariantCulture,out radius)) ||
                !double.IsFinite(radius))
                throw new FormatException("Неверный радиус скругления.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var edited=PreviewCornerFilletEditor.FilletVertex(
                before,ringNumber-1,vertexNumber-1,radius,toleranceMm);
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if (imported.Warnings.Count>0)
                throw new FormatException("DXF после скругления содержит предупреждения.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if (!topology.IsValid || reopened.Count!=edited.Rings.Count ||
                Math.Abs(topology.NetAreaMm2-edited.NetAreaMm2)>
                    Math.Max(1e-6,Math.Abs(edited.NetAreaMm2)*1e-9))
                throw new FormatException("Скругление не прошло повторную проверку DXF.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: точное скругление ARC, только предпросмотр";
            StatusText.Text=$"Скругление: контур №{ringNumber}, вершина №{vertexNumber}, "+
                $"R {radius:0.###} мм; площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны, CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Скругление угла отклонено: "+ex.Message;
        }
    }

    private void FilletLineArcClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyLineArcFillet(reverse:false);

    private void FilletArcLineClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyLineArcFillet(reverse:true);

    private void ApplyLineArcFillet(bool reverse)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var segmentNumber) ||
                ringNumber<1 || segmentNumber<1)
                throw new FormatException("Номера контура и ARC-сегмента должны быть положительными.");
            if (!(double.TryParse(LineArcFilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var radius) ||
                  double.TryParse(LineArcFilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.InvariantCulture,out radius)) ||
                !double.IsFinite(radius))
                throw new FormatException("Неверный радиус LINE/ARC сопряжения.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var edited=reverse
                ? PreviewLineArcFilletEditor.FilletOutgoingLine(
                    before,ringNumber-1,segmentNumber-1,radius,toleranceMm)
                : PreviewLineArcFilletEditor.FilletIncomingLine(
                    before,ringNumber-1,segmentNumber-1,radius,toleranceMm);
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if (imported.Warnings.Count>0)
                throw new FormatException("DXF содержит неподдерживаемые данные.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if(!topology.IsValid||reopened.Count!=edited.Rings.Count)
                throw new FormatException("LINE/ARC сопряжение не прошло проверку.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text=reverse
                ? "DXF: точное касательное ARC/LINE сопряжение"
                : "DXF: точное касательное LINE/ARC сопряжение";
            StatusText.Text=$"{(reverse?"ARC/LINE":"LINE/ARC")}: контур №{ringNumber}, сегмент №{segmentNumber}, "+
                $"R {radius:0.###} мм; площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны. CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="LINE/ARC сопряжение отклонено: "+ex.Message;
        }
    }

    private void FilletArcArcClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный геометрический допуск.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var segmentNumber) ||
                ringNumber<1 || segmentNumber<1)
                throw new FormatException("Номера контура и второй ARC должны быть положительными.");
            if (!(double.TryParse(ArcArcFilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var radius) ||
                  double.TryParse(ArcArcFilletRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.InvariantCulture,out radius)) ||
                !double.IsFinite(radius))
                throw new FormatException("Неверный радиус ARC/ARC сопряжения.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var edited=PreviewArcArcFilletEditor.Fillet(
                before,ringNumber-1,segmentNumber-1,radius,toleranceMm);
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if(imported.Warnings.Count>0)
                throw new FormatException("ARC/ARC DXF содержит предупреждения.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if(!topology.IsValid||reopened.Count!=edited.Rings.Count ||
               Math.Abs(topology.NetAreaMm2-edited.NetAreaMm2)>
                   Math.Max(1e-6,Math.Abs(edited.NetAreaMm2)*1e-9))
                throw new FormatException("ARC/ARC не прошло повторную проверку DXF.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: точное касательное ARC/ARC сопряжение";
            StatusText.Text=$"ARC/ARC: контур №{ringNumber}, сегмент №{segmentNumber}, "+
                $"R {radius:0.###} мм; площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны. CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="ARC/ARC сопряжение отклонено: "+ex.Message;
        }
    }

    // Numbered segment and vertex editing is deliberately restricted to
    // fully verified, closed straight-line rings. Nothing is sent to a machine.
    private void InsertDxfVertexClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyLineVertexChange(insert: true);

    private void DeleteDxfVertexClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyLineVertexChange(insert: false);

    private void ApplyLineVertexChange(bool insert)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте 2D-эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var vertexNumber) ||
                ringNumber < 1 || vertexNumber < 1)
                throw new FormatException("Номер контура и вершины должен быть положительным.");

            var verified = PreviewDxfExporter.ValidateDocument(_dxfPreview, toleranceMm);
            PreviewVertexEditResult edited;
            if (insert)
            {
                static bool ParseCoordinate(string? raw, out double value) =>
                    (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                     double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                    double.IsFinite(value) && Math.Abs(value) <= 1_000_000;
                if (!ParseCoordinate(EditVertexXBox.Text, out var x) ||
                    !ParseCoordinate(EditVertexYBox.Text, out var y))
                    throw new FormatException("Неверные координаты новой вершины.");
                edited = PreviewVertexEditor.InsertVertex(
                    verified, ringNumber - 1, vertexNumber - 1, new PreviewPoint(x,y), toleranceMm);
            }
            else
            {
                edited = PreviewVertexEditor.DeleteVertex(
                    verified, ringNumber - 1, vertexNumber - 1, toleranceMm);
            }
            // Round trip before any UI state mutation. An error leaves the
            // original imported drawing untouched.
            var contents = PreviewDxfExporter.Export(edited.Rings, toleranceMm);
            var candidate = DxfReader.Parse(contents);
            var reopened = PreviewDxfRoundTripVerifier.Verify(edited.Rings, contents, toleranceMm);
            var topology = ValidatePreviewDrawing(reopened, toleranceMm);
            if (!topology.IsValid || reopened.Count != edited.Rings.Count ||
                Math.Abs(topology.NetAreaMm2 - edited.NetAreaMm2) >
                    Math.Max(1e-6, Math.Abs(edited.NetAreaMm2) * 1e-9))
                throw new FormatException("Изменённый контур не прошёл DXF-проверку.");
            CommitDxfPreview(candidate);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: редактирование прямого контура, только предпросмотр";
            StatusText.Text = $"Контур №{ringNumber}: вершина №{vertexNumber} " +
                (insert ? "вставлена" : "удалена") +
                $"; чистая площадь {topology.NetAreaMm2:0.###} мм². " +
                "Сохраните отдельно в DXF; CAM и резка запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text = "Изменение вершины отклонено: " + ex.Message;
        }
    }

    private void SetDxfEdgeLengthClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте 2D-эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var edgeNumber) ||
                ringNumber < 1 || edgeNumber < 1)
                throw new FormatException("Номер контура и ребра должен быть положительным.");
            if (!(double.TryParse(EditEdgeLengthBox.Text, NumberStyles.Float,
                     CultureInfo.CurrentCulture, out var lengthMm) ||
                  double.TryParse(EditEdgeLengthBox.Text, NumberStyles.Float,
                     CultureInfo.InvariantCulture, out lengthMm)) ||
                !double.IsFinite(lengthMm))
                throw new FormatException("Неверный размер ребра.");

            var originalRings = PreviewDxfExporter.ValidateDocument(_dxfPreview, toleranceMm);
            var updated = PreviewVertexEditor.SetEdgeLength(
                originalRings, ringNumber - 1, edgeNumber - 1, lengthMm, toleranceMm);
            var serialized = PreviewDxfExporter.Export(updated.Rings, toleranceMm);
            var candidate = DxfReader.Parse(serialized);
            var reopened = PreviewDxfRoundTripVerifier.Verify(updated.Rings, serialized, toleranceMm);
            var verifiedTopology = ValidatePreviewDrawing(reopened, toleranceMm);
            if (!verifiedTopology.IsValid || reopened.Count != updated.Rings.Count ||
                Math.Abs(verifiedTopology.NetAreaMm2 - updated.NetAreaMm2) >
                    Math.Max(1e-6, Math.Abs(updated.NetAreaMm2) * 1e-9))
                throw new FormatException("Размерный DXF не прошёл повторную проверку.");
            if (reopened[ringNumber - 1].Segments[edgeNumber - 1] is not PreviewLine edge)
                throw new FormatException("Редактируемое ребро больше не прямолинейное.");
            var dx = edge.End.X - edge.Start.X;
            var dy = edge.End.Y - edge.Start.Y;
            var actualLength = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(actualLength - lengthMm) >
                Math.Max(1e-6, Math.Abs(lengthMm) * 1e-9))
                throw new FormatException("Повторно открытый DXF изменил заданный размер.");

            CommitDxfPreview(candidate);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: размерное редактирование, только предпросмотр";
            StatusText.Text = $"Контур №{ringNumber}, ребро №{edgeNumber}: " +
                $"длина {actualLength:0.###} мм, чистая площадь " +
                $"{verifiedTopology.NetAreaMm2:0.###} мм². " +
                "Геометрия проверена; CAM и резка запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text = "Изменение размера отклонено: " + ex.Message;
        }
    }

    private void EditSingleArcRadiusClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Укажите допустимый геометрический допуск.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var segmentNumber) ||
                ringNumber<1 || segmentNumber<1)
                throw new FormatException("Номера контура и ARC-сегмента должны быть положительными.");
            if (!(double.TryParse(EditArcRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var radiusMm) ||
                  double.TryParse(EditArcRadiusBox.Text,NumberStyles.Float,
                    CultureInfo.InvariantCulture,out radiusMm)) ||
                !double.IsFinite(radiusMm))
                throw new FormatException("Неверный радиус дуги.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var changed=PreviewArcRadiusEditor.SetRadius(
                before,ringNumber-1,segmentNumber-1,radiusMm,toleranceMm);
            var serialized=PreviewDxfExporter.Export(changed.Rings,toleranceMm);
            var checkedRings=PreviewDxfRoundTripVerifier.Verify(
                changed.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(checkedRings,toleranceMm);
            if(!topology.IsValid)
                throw new FormatException("Топология изменённой дуги не прошла проверку.");
            var candidate=DxfReader.Parse(serialized);
            CommitDxfPreview(candidate);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: точная правка радиуса ARC, только предпросмотр";
            StatusText.Text=$"Контур №{ringNumber}, ARC №{segmentNumber}, R={radiusMm:0.###} мм; "+
                $"площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны. CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Правка радиуса ARC отклонена: "+ex.Message;
        }
    }

    private void SetDxfEdgeAngleClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте 2D-эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var edgeNumber) ||
                ringNumber < 1 || edgeNumber < 1)
                throw new FormatException("Номер контура и ребра должен быть положительным.");
            if (!(double.TryParse(EditEdgeAngleBox.Text, NumberStyles.Float,
                      CultureInfo.CurrentCulture, out var degrees) ||
                  double.TryParse(EditEdgeAngleBox.Text, NumberStyles.Float,
                      CultureInfo.InvariantCulture, out degrees)) ||
                !double.IsFinite(degrees))
                throw new FormatException("Некорректный угол ребра.");

            var before = PreviewDxfExporter.ValidateDocument(_dxfPreview, toleranceMm);
            var changed = PreviewVertexEditor.SetEdgeAngle(
                before, ringNumber - 1, edgeNumber - 1, degrees, toleranceMm);
            var serialized = PreviewDxfExporter.Export(changed.Rings, toleranceMm);
            var candidate = DxfReader.Parse(serialized);
            var reopened = PreviewDxfRoundTripVerifier.Verify(changed.Rings, serialized, toleranceMm);
            var topology = ValidatePreviewDrawing(reopened, toleranceMm);
            if (!topology.IsValid || reopened.Count != changed.Rings.Count ||
                Math.Abs(topology.NetAreaMm2 - changed.NetAreaMm2) >
                    Math.Max(1e-6, Math.Abs(changed.NetAreaMm2) * 1e-9))
                throw new FormatException("Угловой DXF не прошёл повторную проверку.");
            if (reopened[ringNumber - 1].Segments[edgeNumber - 1] is not PreviewLine edge)
                throw new FormatException("Редактируемое ребро больше не прямолинейное.");
            var actual = Math.Atan2(edge.End.Y - edge.Start.Y,
                                    edge.End.X - edge.Start.X);
            var expected = degrees * Math.PI / 180;
            var angularError = Math.Atan2(
                Math.Sin(actual - expected), Math.Cos(actual - expected));
            if (Math.Abs(angularError) > 1e-8)
                throw new FormatException("Повторно открытый DXF изменил заданный угол.");

            CommitDxfPreview(candidate);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: угловое редактирование, только предпросмотр";
            StatusText.Text = $"Контур №{ringNumber}, ребро №{edgeNumber}: " +
                $"угол {degrees:0.###}°, чистая площадь " +
                $"{topology.NetAreaMm2:0.###} мм². " +
                "Геометрия проверена; CAM и резка запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text = "Изменение угла отклонено: " + ex.Message;
        }
    }

    private void PinEdgeLengthClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) => ChangeLengthRule(add:true);

    private void UnpinEdgeLengthClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) => ChangeLengthRule(add:false);

    private void ChangeLengthRule(bool add)
    {
        try
        {
            if(_dxfPreview is null||_dxfHistory is null)
                throw new FormatException("Сначала откройте DXF.");
            if(!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if(!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber)||
               !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var edgeNumber)||
               ringNumber<1||edgeNumber<1)
                throw new FormatException("Номера контура и ребра должны быть положительными.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var rules=add
                ? PreviewPersistentLengthConstraints.Add(
                    before,_dxfHistory.CurrentLengthRules,
                    ringNumber-1,edgeNumber-1,toleranceMm)
                : PreviewPersistentLengthConstraints.Remove(
                    _dxfHistory.CurrentLengthRules,ringNumber-1,edgeNumber-1);
            PreviewPersistentLengthConstraints.Verify(before,rules,toleranceMm);
            _dxfHistory.Push(_dxfPreview,_dxfHistory.CurrentAxisRules,rules);
            _dxfPreview=_dxfHistory.Current;
            StatusText.Text=add
                ? $"Длина ребра №{edgeNumber} контура №{ringNumber} закреплена. Undo/Redo доступны."
                : $"Ограничение длины ребра №{edgeNumber} снято. Undo/Redo доступны.";
            StatusText.Text+=" CAM и резка запрещены.";
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Ограничение длины отклонено: "+ex.Message;
        }
    }

    private void PinAxisConstraintClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) => ChangeAxisRule(add:true);

    private void UnpinAxisConstraintClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) => ChangeAxisRule(add:false);

    private void ChangeAxisRule(bool add)
    {
        try
        {
            if(_dxfPreview is null || _dxfHistory is null)
                throw new FormatException("Сначала откройте DXF.");
            if(!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if(!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
               !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var edgeNumber) ||
               ringNumber<1 || edgeNumber<1)
                throw new FormatException("Номера контура и ребра должны быть положительными.");
            var horizontal=AxisConstraintDirectionBox.SelectedIndex==0;
            var rule=new PreviewAxisRule(ringNumber-1,edgeNumber-1,horizontal);
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var rules=add
                ? PreviewPersistentAxisConstraints.Add(
                    before,_dxfHistory.CurrentAxisRules,rule,toleranceMm)
                : PreviewPersistentAxisConstraints.Remove(
                    _dxfHistory.CurrentAxisRules,rule);
            PreviewPersistentAxisConstraints.Verify(before,rules,toleranceMm);
            // A constraint-only edit is still one atomic Undo/Redo snapshot.
            _dxfHistory.Push(_dxfPreview,rules);
            _dxfPreview=_dxfHistory.Current;
            StatusText.Text=add
                ? $"Ограничение {(horizontal?"горизонтали":"вертикали")} закреплено: контур №{ringNumber}, ребро №{edgeNumber}. Undo/Redo доступны."
                : $"Ограничение снято: контур №{ringNumber}, ребро №{edgeNumber}. Undo/Redo доступны.";
            StatusText.Text+=" CAM и резка запрещены.";
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Изменение сохраняемого ограничения отклонено: "+ex.Message;
        }
    }

    private void ConstrainEdgeHorizontalClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyEdgeAxisConstraint(horizontal:true);

    private void ConstrainEdgeVerticalClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplyEdgeAxisConstraint(horizontal:false);

    private void ApplyEdgeAxisConstraint(bool horizontal)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) ||
                !int.TryParse(EditVertexIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var edgeNumber) ||
                ringNumber<1 || edgeNumber<1)
                throw new FormatException("Номера контура и ребра должны быть положительными.");
            var before=PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var edited=PreviewAxisConstraintEditor.AlignEdge(
                before,ringNumber-1,edgeNumber-1,horizontal,toleranceMm);
            var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
            var imported=DxfReader.Parse(serialized);
            if (imported.Warnings.Count>0)
                throw new FormatException("Результат DXF содержит неподдерживаемые данные.");
            var reopened=PreviewDxfRoundTripVerifier.Verify(
                edited.Rings,serialized,toleranceMm);
            var topology=ValidatePreviewDrawing(reopened,toleranceMm);
            if(!topology.IsValid || reopened.Count!=edited.Rings.Count ||
                Math.Abs(topology.NetAreaMm2-edited.NetAreaMm2)>
                    Math.Max(1e-6,Math.Abs(edited.NetAreaMm2)*1e-9))
                throw new FormatException("Ограничение не прошло повторную проверку DXF.");
            if(reopened[ringNumber-1].Segments[edgeNumber-1] is not PreviewLine edge ||
               (horizontal ? Math.Abs(edge.Start.Y-edge.End.Y)>1e-7
                           : Math.Abs(edge.Start.X-edge.End.X)>1e-7))
                throw new FormatException("Геометрическое ограничение потеряно при экспорте.");
            CommitDxfPreview(imported);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: "+(horizontal?"горизонтальное":"вертикальное")+
                " выравнивание ребра";
            StatusText.Text=$"Контур №{ringNumber}, ребро №{edgeNumber}: "+
                (horizontal?"горизонтально":"вертикально")+
                $"; площадь {topology.NetAreaMm2:0.###} мм². "+
                "Undo/Redo доступны. CAM и резка запрещены.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Выравнивание ребра отклонено: "+ex.Message;
        }
    }

    private void AddCircleHoleClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF или создайте эскиз.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            static bool Coordinate(string? raw, out double value) =>
                (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                 double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                double.IsFinite(value);
            if (!Coordinate(HoleCenterXBox.Text, out var cx) ||
                !Coordinate(HoleCenterYBox.Text, out var cy) ||
                !Coordinate(HoleDiameterBox.Text, out var diameter))
                throw new FormatException("Неверные координаты или диаметр отверстия.");
            var updated = PreviewCircleHoleEditor.AddHole(
                _dxfPreview, new PreviewPoint(cx,cy), diameter, toleranceMm);
            // The verified result remains a document with an explicit CIRCLE,
            // allowing subsequent numeric edits and undo/redo.
            CommitDxfPreview(updated);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: добавление круглого отверстия, только предпросмотр";
            StatusText.Text = $"Новое отверстие: центр ({cx:0.###}; {cy:0.###}) мм, " +
                $"Ø{diameter:0.###} мм. Отмена доступна. " +
                "CAM и управление станком запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text = "Добавление отверстия отклонено: " + ex.Message;
        }
    }

    private void EditArcCircleDiameterClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(EditRingIndexBox.Text,NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var ringNumber) || ringNumber<1)
                throw new FormatException("Номер контура должен быть положительным.");
            if (!(double.TryParse(HoleDiameterBox.Text,NumberStyles.Float,
                      CultureInfo.CurrentCulture,out var diameter) ||
                  double.TryParse(HoleDiameterBox.Text,NumberStyles.Float,
                      CultureInfo.InvariantCulture,out diameter)) ||
                !double.IsFinite(diameter))
                throw new FormatException("Неверный диаметр окружности.");

            var updated=PreviewArcCircleEditor.SetDiameter(
                _dxfPreview,ringNumber-1,diameter,toleranceMm);
            var rings=PreviewDxfExporter.ValidateDocument(updated,toleranceMm);
            var topology=ValidatePreviewDrawing(rings,toleranceMm);
            if(!topology.IsValid)
                throw new FormatException("Изменённый ARC-DXF не прошёл проверку.");
            CommitDxfPreview(updated);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="DXF: изменение диаметра окружности из ARC";
            StatusText.Text=$"Контур №{ringNumber}, диаметр Ø{diameter:0.###} мм; " +
                $"площадь {topology.NetAreaMm2:0.###} мм². " +
                "Undo/Redo доступны, CAM и резка запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Изменение ARC-окружности отклонено: "+ex.Message;
        }
    }

    private void EditCircleHoleClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте DXF.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Некорректный допуск DXF.");
            if (!int.TryParse(HoleCircleIndexBox.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var circleNumber) || circleNumber < 1)
                throw new FormatException("Номер отверстия CIRCLE должен быть положительным.");
            static bool Coordinate(string? raw, out double value) =>
                (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                 double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                double.IsFinite(value);
            if (!Coordinate(HoleCenterXBox.Text, out var cx) ||
                !Coordinate(HoleCenterYBox.Text, out var cy) ||
                !Coordinate(HoleDiameterBox.Text, out var diameter))
                throw new FormatException("Неверные координаты или диаметр отверстия.");
            var updated = PreviewCircleHoleEditor.SetHole(
                _dxfPreview, circleNumber - 1, new PreviewPoint(cx,cy),
                diameter, toleranceMm);
            var paths = PreviewDxfExporter.ValidateDocument(updated, toleranceMm);
            var exported = PreviewDxfExporter.Export(paths, toleranceMm);
            var reopened = PreviewDxfRoundTripVerifier.Verify(paths, exported, toleranceMm);
            var topology = ValidatePreviewDrawing(reopened, toleranceMm);
            if (!topology.IsValid)
                throw new FormatException("DXF с изменённым отверстием не проверен.");

            // Preserve the actual CIRCLE entity for subsequent edits. Mutate the
            // preview only after all topology and serialization checks succeed.
            CommitDxfPreview(updated);
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text = "DXF: редактирование круглого отверстия, только предпросмотр";
            StatusText.Text = $"Отверстие CIRCLE №{circleNumber}: " +
                $"центр ({cx:0.###}; {cy:0.###}), Ø{diameter:0.###} мм; " +
                $"чистая площадь {topology.NetAreaMm2:0.###} мм². " +
                "CAM и резка запрещены.";
            RenderSheet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            StatusText.Text = "Изменение отверстия отклонено: " + ex.Message;
        }
    }

    private async void ExportEditedDxfClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Нет открытой DXF-геометрии.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text, out var toleranceMm))
                throw new FormatException("Неверный геометрический допуск.");
            static bool ParseOffset(string? raw, out double result) =>
                (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                 double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
                double.IsFinite(result) && Math.Abs(result) <= 1_000_000;
            if (!ParseOffset(ExportMoveXBox.Text, out var dx) ||
                !ParseOffset(ExportMoveYBox.Text, out var dy) ||
                !ParseOffset(ExportRotationBox.Text, out var degrees) ||
                Math.Abs(degrees) > 360_000)
                throw new FormatException("Неверные параметры смещения или поворота.");
            // All document warnings and geometric shapes are verified centrally.
            // Preview-only bulge notices do not prevent safe drawing export.
            var verifiedPaths = PreviewDxfExporter.ValidateDocument(_dxfPreview,toleranceMm);
            var original = ValidatePreviewDrawing(verifiedPaths,toleranceMm);
            var transformed = verifiedPaths.Select(path =>
                PreviewContourTransform.Move(
                    PreviewContourTransform.Rotate(path,new PreviewPoint(0,0),degrees),
                    dx,dy)).ToArray();
            var contents = PreviewDxfExporter.Export(transformed,toleranceMm);
            // Independent import/assembly/area checks before the save dialog.
            var reopened = DxfReader.Parse(contents);
            var reopenedAssembly = MixedContourAssembler.Assemble(reopened.Shapes,toleranceMm);
            var reopenedTopology = reopenedAssembly.Complete
                ? ValidatePreviewDrawing(reopenedAssembly.ClosedPaths,toleranceMm)
                : new PreviewTopologyResult(false,[],0,["DXF does not reassemble."]);
            if (reopened.Warnings.Count>0 ||
                !reopenedTopology.IsValid ||
                reopenedAssembly.ClosedPaths.Count != transformed.Length ||
                Math.Abs(reopenedTopology.NetAreaMm2-original.NetAreaMm2) >
                    Math.Max(0.000001,original.NetAreaMm2*1e-9))
                throw new FormatException("Повторная геометрическая проверка DXF не прошла.");
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить аналитические DXF-контуры",
                SuggestedFileName = "AI-CutPro-edited.dxf",
                FileTypeChoices = [new FilePickerFileType("DXF") { Patterns = ["*.dxf"] }]
            });
            if (target is null) return;
            await using var output = await target.OpenWriteAsync();
            using var writer = new StreamWriter(output,new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(contents);
            await writer.FlushAsync();
            StatusText.Text = $"DXF сохранён: {transformed.Length} контур(ов), " +
                $"площадь {reopenedTopology.NetAreaMm2:0.###} мм². " +
                "Только геометрия, без CAM и допуска к резке.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            StatusText.Text = "Сохранение DXF отклонено: " + ex.Message;
        }
    }

    private async void ExportCurrentDxf3DClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_dxfPreview is null)
                throw new FormatException("Сначала откройте или создайте DXF.");
            if (!ParsePositiveDimension(Current3DThicknessBox.Text,out var thicknessMm))
                throw new FormatException("Неверная толщина 3D-модели, мм.");
            if (!TryParseGeometryTolerance(DxfToleranceBox.Text,out var toleranceMm))
                throw new FormatException("Неверный геометрический допуск DXF.");
            var model = DxfAssembly3DExporter.Export(
                _dxfPreview,thicknessMm,toleranceMm);
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "3D из текущего отредактированного DXF",
                SuggestedFileName = "AI-CutPro-current-DXF-3D.scad",
                DefaultExtension = "scad",
                FileTypeChoices =
                [
                    new FilePickerFileType("OpenSCAD CAD model")
                    {
                        Patterns = ["*.scad"],
                        MimeTypes = ["text/plain"]
                    }
                ]
            });
            if (target is null)
            {
                StatusText.Text = "Сохранение текущей 3D-модели отменено.";
                return;
            }
            await using var stream = await target.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(model.OpenScad);
            await writer.FlushAsync();
            StatusText.Text = $"Текущий DXF экспортирован в OpenSCAD: " +
                $"толщина {model.ThicknessMm:0.###} мм, " +
                $"материальных областей {model.MaterialRegionCount}, отверстий {model.HoleCount}, " +
                $"объём {model.VolumeMm3:0.###} мм³. " +
                "Состояние учитывает Undo/Redo. CAM и резка запрещены.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            StatusText.Text = "3D-экспорт текущего DXF отклонён: " + ex.Message;
        }
    }

    private async void ExportPlate3DClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            // Always regenerate from the text parameters. Later DXF edits do
            // not implicitly mutate the 3D model or claim a synced B-rep.
            var plate = TextPlateSketch.Parse(TextSketchBox.Text ?? "");
            var scad = PlateOpenScadExporter.Export(plate);
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить параметрическую 3D-модель OpenSCAD",
                SuggestedFileName = "AI-CutPro-plate-3d.scad",
                DefaultExtension = "scad",
                FileTypeChoices =
                [
                    new FilePickerFileType("OpenSCAD CAD model")
                    {
                        Patterns = ["*.scad"],
                        MimeTypes = ["text/plain"]
                    }
                ]
            });
            if (target is null)
            {
                StatusText.Text = "Сохранение 3D-модели отменено.";
                return;
            }
            await using var stream = await target.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(scad);
            await writer.FlushAsync();
            StatusText.Text = $"3D-модель сохранена: {plate.WidthMm:0.###} × " +
                $"{plate.HeightMm:0.###} × {plate.ThicknessMm:0.###} мм, " +
                $"отверстий {plate.Holes.Count}, объём {plate.VolumeMm3:0.###} мм³. " +
                "OpenSCAD F6 для построения тела; это не программа станка.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            StatusText.Text = "Не удалось экспортировать 3D-модель: " + ex.Message;
        }
    }

    private void BuildTextSketchClicked(object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var model=TextPlateSketch.Parse(TextSketchBox.Text ?? "");
            // This is a geometry-only preview and is never translated into
            // cutting commands or a fabricated 3D mesh.
            var topology=MixedContourAssembler.Assemble(model.Drawing2D.Shapes);
            if(!topology.Complete)
                throw new FormatException("Contours cannot be verified: " +
                    string.Join("; ",topology.Warnings));
            var measured=ValidatePreviewDrawing(topology.ClosedPaths);
            if(!measured.IsValid ||
               Math.Abs(measured.NetAreaMm2-model.NetAreaMm2)>
                   Math.Max(1e-6,model.NetAreaMm2*1e-9))
                throw new FormatException("Analytical area mismatch in text sketch.");
            _dxfHistory = new DxfPreviewHistory(model.Drawing2D);
            _dxfPreview = _dxfHistory.Current;
            ResetView();
            UpdatePartCard();
            UpdateHoleSummary();
            PanelTitle.Text="Текстовый 2D-эскиз и параметрический 3D-объём";
            StatusText.Text=$"Пластина: {model.WidthMm:0.###} × {model.HeightMm:0.###} × " +
                $"{model.ThicknessMm:0.###} мм; отверстий: {model.Holes.Count}; " +
                $"чистая площадь: {model.NetAreaMm2:0.###} мм²; " +
                $"параметрический объём: {model.VolumeMm3:0.###} мм³. " +
                "Это не полноценный 3D-CAD и не допуск к резке.";
            RenderSheet();
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException)
        {
            StatusText.Text="Эскиз отклонён: "+ex.Message;
        }
    }

    private static bool TryParseGeometryTolerance(string? text, out double toleranceMm)
    {
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out toleranceMm) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out toleranceMm)) &&
               double.IsFinite(toleranceMm) && toleranceMm >= 0.0001 && toleranceMm <= 1;
    }

    private static bool ParsePositiveDimension(string? text, out double value)
    {
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
               double.IsFinite(value) && value > 0 && value <= 100000;
    }

    private void ApplyWorkspaceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ParsePositiveDimension(TableWidthBox.Text, out var tableWidth) ||
            !ParsePositiveDimension(TableHeightBox.Text, out var tableHeight) ||
            !ParsePositiveDimension(SheetWidthBox.Text, out var sheetWidth) ||
            !ParsePositiveDimension(SheetHeightBox.Text, out var sheetHeight))
        {
            StatusText.Text = "Ошибка: укажите положительные размеры в миллиметрах (не более 100000).";
            return;
        }
        var requested = new WorkspaceGeometry(new Sheet(tableWidth, tableHeight),
                                              new Sheet(sheetWidth, sheetHeight));
        var errors = requested.Validate();
        if (errors.Count > 0)
        {
            StatusText.Text = "Размеры не приняты: лист должен помещаться на столе станка.";
            return;
        }
        _workspace = requested;
        _virtualMachine = VirtualMachine.Create(requested);
        HeadXBox.Text = HeadYBox.Text = HeadZBox.Text = "0";
        _step = 0;
        ResetView();
        _result = ShelfNester.Arrange(requested.MaterialSheet, SampleJob.Parts);
        PanelTitle.Text = $"Раскрой листа {sheetWidth:0.###} × {sheetHeight:0.###} мм";
        StatusText.Text = "Размеры стола и листа обновлены независимо. Это виртуальные параметры.";
        UpdateSummary();
    }

    private void MoveVirtualHeadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!double.TryParse(HeadXBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var x) &&
            !double.TryParse(HeadXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
            !double.TryParse(HeadYBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var y) &&
            !double.TryParse(HeadYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
            !double.TryParse(HeadZBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var z) &&
            !double.TryParse(HeadZBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
        {
            StatusText.Text = "Неверные координаты X/Y/Z.";
            return;
        }
        try
        {
            _virtualMachine = _virtualMachine.MoveVirtual(new MachinePoint(x, y, z));
            UpdateMachineCard();
            RenderSheet();
            StatusText.Text = "Только виртуальное перемещение: физический станок не подключён.";
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText.Text = "Координаты выходят за пределы стола либо Z вне диапазона 0–1000 мм.";
        }
    }

    private void SetWorkZeroClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _virtualMachine = _virtualMachine.SetWorkZeroAtHead();
        UpdateMachineCard();
        StatusText.Text = "Рабочий ноль установлен в текущей ВИРТУАЛЬНОЙ точке. Реальные координаты станка не изменены.";
    }

    private void CanvasSizeChanged(object? sender, SizeChangedEventArgs e) => RenderSheet();
    private void NewLayoutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _step = 0;
        _dxfPreview = null;
        _dxfHistory = null;
        ResetView();
        PanelTitle.Text = $"Раскрой листа {_workspace.MaterialSheet.WidthMm:0.###} × {_workspace.MaterialSheet.HeightMm:0.###} мм";
        _result = ShelfNester.Arrange(_workspace.MaterialSheet, SampleJob.Parts);
        StatusText.Text = "Раскладка пересчитана";
        UpdateSummary();
    }
    private void SimulateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_dxfPreview is not null) { StatusText.Text = "DXF доступен только для просмотра; сначала создайте новое демонстрационное задание."; return; }
        _step = (_step + 1) % (_result.Placements.Count + 1);
        StatusText.Text = $"Виртуальная симуляция: {_step} / {_result.Placements.Count} деталей (не траектория резки)";
        RenderSheet();
    }
    private void ManualClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => StatusText.Text = "Ручное управление X/Y/Z: следующий этап, движение станка невозможно";
    private void SaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Save simple job metadata only. Do not claim DXF or machine programs.
        var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-CutPro-demo.txt");
        File.WriteAllText(file, $"Лист: {_result.Sheet.WidthMm} × {_result.Sheet.HeightMm} мм{Environment.NewLine}Размещено деталей: {_result.Placements.Count}{Environment.NewLine}Использование: {_result.UtilizationPercent:F2}%");
        StatusText.Text = $"Демонстрационное задание сохранено: {file}";
    }
    private void TabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var tab = (sender as Button)?.Tag?.ToString() ?? "Раскрой";
        PanelTitle.Text = tab;
        StatusText.Text = tab == "Раскрой" ? "Рабочий прототип раскроя" : $"Раздел «{tab}»: интерфейс подготовлен, функции ещё в разработке";
    }
}
