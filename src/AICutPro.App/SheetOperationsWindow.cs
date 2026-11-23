using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using AICutPro.Core;

namespace AICutPro.App;

/// <summary>
/// A small, independently testable sheet-operation editor/preview.
/// Its output is NOT CAD authorisation, CAM, G-code or machine motion.
/// </summary>
public sealed class SheetOperationsWindow : Window
{
    private readonly ComboBox _kind;
    private readonly ComboBox _sourceLine;
    private readonly IReadOnlyList<DxfLine> _dxfLines;
    private readonly Sheet _sheet;
    private readonly TextBox _startX, _startY, _endX, _endY;
    private readonly TextBox _startInset, _endInset, _cut, _skip;
    private readonly TextBlock _status;
    private readonly Canvas _drawing;

    public SheetOperationsWindow(DxfImportResult? document = null, Sheet? sheet = null, int? selectedLineIndex = null)
    {
        _sheet = sheet ?? new Sheet(200, 100);
        _dxfLines = document?.Shapes.OfType<DxfLine>().ToArray() ?? [];
        Title = "AI CutPro | Технологические линии (предпросмотр)";
        Width = 870;
        Height = 640;
        MinWidth = 650;
        MinHeight = 500;

        _kind = new ComboBox
        {
            ItemsSource = new[]
            {
                "Непрерывный открытый рез",
                "Прерывистый рез (Cut / Skip)",
                "Линия будущего сгиба",
                "Линия разметки"
            },
            SelectedIndex = 0
        };
        _sourceLine = new ComboBox
        {
            ItemsSource = new[] { "Пример: X 0–200, Y 50" }
                .Concat(_dxfLines.Select((line, index) =>
                    $"DXF LINE №{index + 1} | слой {line.Layer} | " +
                    $"({line.Start.X:0.###}; {line.Start.Y:0.###}) → " +
                    $"({line.End.X:0.###}; {line.End.Y:0.###})"))
                .ToArray(),
            SelectedIndex = 0
        };
        _sourceLine.SelectionChanged += (_, _) => ApplySelectedLine();
        _startX = Field("0");
        _startY = Field("50");
        _endX = Field("200");
        _endY = Field("50");
        _startInset = Field("5");
        _endInset = Field("5");
        _cut = Field("3");
        _skip = Field("2");
        _status = new TextBlock
        {
            Text = "Выберите назначение и нажмите «Показать предпросмотр».",
            TextWrapping = TextWrapping.Wrap
        };
        _drawing = new Canvas
        {
            Width = 520,
            Height = 270,
            Background = new SolidColorBrush(Color.Parse("#E0E9F0"))
        };

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };
        Add(fields, 0, 0, "Начало X, мм", _startX);
        Add(fields, 0, 1, "Начало Y, мм", _startY);
        Add(fields, 0, 2, "Конец X, мм", _endX);
        Add(fields, 0, 3, "Конец Y, мм", _endY);
        Add(fields, 2, 0, "Отступ от начала, мм", _startInset);
        Add(fields, 2, 1, "Отступ от конца, мм", _endInset);
        Add(fields, 2, 2, "Длина реза, мм", _cut);
        Add(fields, 2, 3, "Длина пропуска, мм", _skip);

        var preview = new Button { Content = "Показать предпросмотр" };
        preview.Click += (_, _) => RenderPreview();

        var root = new StackPanel
        {
            Spacing = 11,
            Margin = new Thickness(18)
        };
        root.Children.Add(new TextBlock
        {
            Text = "Технологические операции с открытой линией",
            FontSize = 20,
            FontWeight = FontWeight.Bold
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Рабочий лист {_sheet.WidthMm:0.###} × {_sheet.HeightMm:0.###} мм. " +
                   "Выбор DXF LINE копирует координаты в отдельную модель предпросмотра, оригинал не изменяется. Пунктир DXF не является командой резки.",
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock { Text = "Источник линии", FontWeight = FontWeight.SemiBold });
        root.Children.Add(_sourceLine);
        var selectByNumber = new Button { Content = "Применить DXF LINE №1" };
        selectByNumber.Click += (_, _) =>
        {
            if (_dxfLines.Count == 0)
            {
                _status.Text = "В загруженном DXF отсутствуют отдельные объекты LINE.";
                return;
            }
            _sourceLine.SelectedIndex = 1;
            ApplySelectedLine();
        };
        root.Children.Add(selectByNumber);
        root.Children.Add(_kind);
        root.Children.Add(fields);
        root.Children.Add(preview);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer
        {
            Content = _drawing,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        root.Children.Add(new TextBlock
        {
            Text = "Только CAD-предпросмотр. Нет управления станком, компенсации ширины реза, проверенных сгибов или готовой CAM-траектории.",
            TextWrapping = TextWrapping.Wrap
        });
        Content = new ScrollViewer { Content = root };
        if (selectedLineIndex is int index && index >= 0 && index < _dxfLines.Count)
            _sourceLine.SelectedIndex = index + 1;
        else
            RenderPreview();
    }

    private void ApplySelectedLine()
    {
        var selected = _sourceLine.SelectedIndex;
        if (selected <= 0 || selected > _dxfLines.Count)
            return;
        var line = _dxfLines[selected - 1];
        _startX.Text = line.Start.X.ToString("R", CultureInfo.InvariantCulture);
        _startY.Text = line.Start.Y.ToString("R", CultureInfo.InvariantCulture);
        _endX.Text = line.End.X.ToString("R", CultureInfo.InvariantCulture);
        _endY.Text = line.End.Y.ToString("R", CultureInfo.InvariantCulture);
        RenderPreview();
        _status.Text = $"Источник DXF LINE №{selected}, слой {line.Layer}: " +
            $"X {line.Start.X:0.###}, Y {line.Start.Y:0.###} → " +
            $"X {line.End.X:0.###}, Y {line.End.Y:0.###}. " + _status.Text;
    }

    private static TextBox Field(string text) => new() { Text = text, Width = 112 };

    private static void Add(Grid parent, int row, int column, string label, Control control)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 8, 5) };
        stack.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(control);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        parent.Children.Add(stack);
    }

    private static bool Number(TextBox box, out double value)
    {
        var text = box.Text?.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void RenderPreview()
    {
        if (!(Number(_startX, out var sx) && Number(_startY, out var sy)
            && Number(_endX, out var ex) && Number(_endY, out var ey)
            && Number(_startInset, out var a) && Number(_endInset, out var b)
            && Number(_cut, out var cut) && Number(_skip, out var skip)))
        {
            _status.Text = "Введите числовые параметры в миллиметрах.";
            _drawing.Children.Clear();
            return;
        }
        var kind = _kind.SelectedIndex switch
        {
            0 => SheetOperationKind.OpenThroughCut,
            1 => SheetOperationKind.PerforatedCut,
            2 => SheetOperationKind.BendReference,
            3 => SheetOperationKind.Marking,
            _ => SheetOperationKind.Unassigned
        };
        if (kind != SheetOperationKind.PerforatedCut)
            cut = skip = 0;
        var result = SheetOperationPlanner.InspectOpenLine(
            new(kind, new(sx, sy), new(ex, ey), a, b, cut, skip),
            _sheet);
        _drawing.Children.Clear();
        var sheet = new Rectangle
        {
            Width = 480,
            Height = 240,
            Stroke = Brushes.SlateGray,
            StrokeThickness = 2,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(sheet, 20);
        Canvas.SetTop(sheet, 15);
        _drawing.Children.Add(sheet);
        if (!result.IsValid)
        {
            _status.Text = "Предпросмотр отклонён: " + string.Join("; ", result.Issues);
            return;
        }
        var drawScale = Math.Min(480 / _sheet.WidthMm, 240 / _sheet.HeightMm);
        foreach (var segment in result.Segments)
        {
            var line = new Line
            {
                StartPoint = new Point(20 + segment.Start.X * drawScale,
                                       255 - segment.Start.Y * drawScale),
                EndPoint = new Point(20 + segment.End.X * drawScale,
                                     255 - segment.End.Y * drawScale),
                Stroke = segment.IsCut ? Brushes.IndianRed : Brushes.SteelBlue,
                StrokeThickness = segment.IsCut ? 3 : 2
            };
            _drawing.Children.Add(line);
        }
        var cutCount = result.Segments.Count(x => x.IsCut);
        var skipped = result.Segments.Count - cutCount;
        _status.Text = $"Предпросмотр: {result.Segments.Count} сегментов; " +
                       $"резать: {cutCount}; пропуск/разметка: {skipped}. " +
                       "Только геометрический расчёт, резка запрещена.";
    }
}
