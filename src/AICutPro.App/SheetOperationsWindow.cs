using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;
using AICutPro.Core;

namespace AICutPro.App;

/// <summary>
/// A small, independently testable sheet-operation editor/preview.
/// Its output is NOT CAD authorisation, CAM, G-code or machine motion.
/// </summary>
public sealed class SheetOperationsWindow : Window
{
    private readonly ComboBox _kind;
    private readonly ComboBox _processing;
    private readonly ComboBox _preset;
    private readonly ComboBox _chainDirection;
    private readonly ComboBox _chainFirstPhase;
    private readonly ComboBox _sourceLine;
    private readonly ComboBox _sourceCurve;
    private readonly IReadOnlyList<DxfShape> _curves;
    private readonly IReadOnlyList<DxfShape> _allDxfShapes;
    private readonly IReadOnlyList<DxfLine> _dxfLines;
    private readonly Sheet _sheet;
    private readonly SheetLineAssignmentRegister _assignments = new();
    private readonly SheetUShapeAssignmentRegister _uAssignments = new();
    private readonly SheetChainAssignmentRegister _chainAssignments = new();
    private readonly TextBox _startX, _startY, _endX, _endY;
    private readonly TextBox _startInset, _endInset, _cut, _skip, _uHeight;
    private readonly TextBlock _status;
    private readonly Canvas _drawing;

    public SheetOperationsWindow(DxfImportResult? document = null, Sheet? sheet = null, int? selectedLineIndex = null)
    {
        _sheet = sheet ?? new Sheet(200, 100);
        _allDxfShapes = document?.Shapes ?? [];
        _dxfLines = _allDxfShapes.OfType<DxfLine>().ToArray();
        _curves = document?.Shapes.Where(x => x is DxfArc
            or DxfPolyline { Closed: false }
            or DxfBulgedPolyline { Closed: false }).ToArray() ?? [];
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
        _preset = new ComboBox
        {
            ItemsSource = new[]
            {
                "Сплошной рез без отступов",
                "Сплошной рез с отступами",
                "Прерывистый рез Cut/Skip",
                "Сгиб: только ориентир",
                "Сгиб: сплошной сквозной рез",
                "Сгиб: прерывистые прорези"
            },
            SelectedIndex = 0
        };
        _chainDirection = new ComboBox
        {
            ItemsSource = new[]
            {
                "Направление цепочки: от меньшего X/Y",
                "Направление цепочки: обратное"
            },
            SelectedIndex = 0
        };
        _chainFirstPhase = new ComboBox
        {
            ItemsSource = new[]
            {
                "Первый участок Cut/Skip: рез",
                "Первый участок Cut/Skip: пропуск"
            },
            SelectedIndex = 0
        };
        _processing = new ComboBox
        {
            ItemsSource = new[]
            {
                "По назначению (совместимый режим)",
                "Без резки",
                "Поверхностный надрез (только схема)",
                "Сплошной сквозной рез (только схема)",
                "Прерывистый сквозной рез (только схема)"
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
        _sourceCurve = new ComboBox
        {
            ItemsSource = new[] { "Выберите открытый ARC/POLYLINE" }
                .Concat(_curves.Select((curve, index) =>
                    $"{index + 1}. {curve.GetType().Name} | слой {curve.Layer}"))
                .ToArray(),
            SelectedIndex = 0
        };
        _startX = Field("0");
        _startY = Field("50");
        _endX = Field("200");
        _endY = Field("50");
        _startInset = Field("5");
        _endInset = Field("5");
        _cut = Field("3");
        _skip = Field("2");
        _uHeight = Field("20");
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
        var applySource = new Button { Content = "Применить выбранную DXF LINE" };
        applySource.Click += (_, _) =>
        {
            if (_dxfLines.Count == 0)
            {
                _status.Text = "В загруженном DXF отсутствуют отдельные объекты LINE.";
                return;
            }
            if (_sourceLine.SelectedIndex <= 0)
                _sourceLine.SelectedIndex = 1; // also raises SelectionChanged
            else
                ApplySelectedLine();
        };
        root.Children.Add(applySource);
        root.Children.Add(new TextBlock { Text = "Параметрические шаблоны (только предпросмотр)" });
        root.Children.Add(_preset);
        var applyPreset = new Button { Content = "Применить шаблон к выбранной линии" };
        applyPreset.Click += (_, _) => ApplyPreset();
        root.Children.Add(applyPreset);
        root.Children.Add(new TextBlock { Text = "Функциональное назначение" });
        root.Children.Add(_kind);
        root.Children.Add(new TextBlock { Text = "Способ обработки (независимо от назначения)" });
        root.Children.Add(_processing);
        root.Children.Add(fields);
        root.Children.Add(preview);
        root.Children.Add(_chainDirection);
        root.Children.Add(_chainFirstPhase);
        var chainPreview = new Button
        {
            Content = "Предпросмотр связанной цепочки DXF LINE"
        };
        chainPreview.Click += (_, _) => RenderConnectedChain();
        root.Children.Add(chainPreview);
        root.Children.Add(new TextBlock
        {
            Text = "Открытые дуги ARC и POLYLINE/bulge (только изображение)",
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(_sourceCurve);
        var pickFirstCurve = new Button
        {
            Content = "Выбрать первую импортированную DXF-кривую"
        };
        pickFirstCurve.Click += (_, _) =>
        {
            if (_curves.Count == 0)
            {
                _status.Text = "В DXF нет открытых ARC или POLYLINE.";
                return;
            }
            _sourceCurve.SelectedIndex = 1;
            RenderOpenCurve();
        };
        root.Children.Add(pickFirstCurve);
        var curvePreview = new Button
        {
            Content = "Предпросмотр открытой DXF-дуги/полилинии"
        };
        curvePreview.Click += (_, _) => RenderOpenCurve();
        root.Children.Add(curvePreview);
        var mixedPreview = new Button
        {
            Content = "Предпросмотр смешанной цепочки LINE/ARC/POLYLINE"
        };
        mixedPreview.Click += (_, _) => RenderMixedOpenChain();
        root.Children.Add(mixedPreview);
        var assignChain = new Button { Content = "Назначить открытую цепочку (группа)" };
        assignChain.Click += (_, _) => AssignConnectedChain();
        root.Children.Add(assignChain);
        var clearChain = new Button { Content = "Снять назначение цепочки" };
        clearChain.Click += (_, _) => RemoveConnectedChain();
        root.Children.Add(clearChain);
        var uOptions = new StackPanel { Spacing = 4 };
        uOptions.Children.Add(new TextBlock
        {
            Text = "П-образная прорезь: выберите горизонтальную DXF LINE как основание, высота вверх, мм",
            TextWrapping = TextWrapping.Wrap
        });
        uOptions.Children.Add(_uHeight);
        var uPreview = new Button { Content = "Показать П-образную прорезь" };
        uPreview.Click += (_, _) => RenderUPreview();
        uOptions.Children.Add(uPreview);
        var uAssign = new Button { Content = "Назначить П-образную прорезь (группа)" };
        uAssign.Click += (_, _) => AssignUGroup();
        uOptions.Children.Add(uAssign);
        var uClear = new Button { Content = "Снять П-образную группу" };
        uClear.Click += (_, _) =>
        {
            if (_sourceLine.SelectedIndex <= 0 ||
                !_uAssignments.Remove(_sourceLine.SelectedIndex - 1))
            {
                _status.Text = "Нет П-образной группы для выбранной линии.";
                return;
            }
            _status.Text = $"П-образная группа удалена. Осталось групп: {_uAssignments.Count}.";
        };
        uOptions.Children.Add(uClear);
        root.Children.Add(uOptions);
        var assign = new Button { Content = "Назначить операцию выбранной DXF LINE (сеанс)" };
        assign.Click += (_, _) => AssignCurrentLine();
        root.Children.Add(assign);
        var clear = new Button { Content = "Снять назначение с DXF LINE" };
        clear.Click += (_, _) =>
        {
            if (_sourceLine.SelectedIndex <= 0)
            {
                _status.Text = "Сначала выберите импортированную DXF LINE.";
                return;
            }
            _assignments.Remove(_sourceLine.SelectedIndex - 1);
            _status.Text = "Назначение снято. DXF не изменён. " +
                $"Назначено линий: {_assignments.Count}.";
        };
        root.Children.Add(clear);
        var inspectIntent = new Button { Content = "Проверить назначения открытых DXF LINE" };
        inspectIntent.Click += (_, _) => InspectOpenIntent();
        root.Children.Add(inspectIntent);
        var saveAnnotations = new Button { Content = "Сохранить назначения отдельно (.json)" };
        saveAnnotations.Click += async (_, _) => await SaveAnnotationsAsync();
        root.Children.Add(saveAnnotations);
        var loadAnnotations = new Button { Content = "Загрузить назначения (.json)" };
        loadAnnotations.Click += async (_, _) => await LoadAnnotationsAsync();
        root.Children.Add(loadAnnotations);
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
        if (_assignments.TryGet(selected - 1, out var stored) && stored is not null)
        {
            _kind.SelectedIndex = stored.Kind switch
            {
                SheetOperationKind.OpenThroughCut => 0,
                SheetOperationKind.PerforatedCut => 1,
                SheetOperationKind.BendReference => 2,
                SheetOperationKind.Marking => 3,
                _ => -1
            };
            _processing.SelectedIndex = (int)stored.Processing;
            _startInset.Text = stored.StartInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _endInset.Text = stored.EndInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _cut.Text = stored.CutLengthMm.ToString("R", CultureInfo.InvariantCulture);
            _skip.Text = stored.SkipLengthMm.ToString("R", CultureInfo.InvariantCulture);
        }
        else
        {
            _kind.SelectedIndex = 0;
            _processing.SelectedIndex = 0;
            _startInset.Text = "5";
            _endInset.Text = "5";
            _cut.Text = "3";
            _skip.Text = "2";
        }
        RenderPreview();
        foreach (var entry in _chainAssignments.Snapshot())
        {
            var memberChain = DxfOpenLineChainBuilder.Build(_dxfLines, entry.Key);
            if (!memberChain.IsValid || !memberChain.SourceIndices.Contains(selected - 1))
                continue;
            var chainOp = entry.Value;
            _kind.SelectedIndex = chainOp.Kind switch
            {
                SheetOperationKind.OpenThroughCut => 0,
                SheetOperationKind.PerforatedCut => 1,
                SheetOperationKind.BendReference => 2,
                SheetOperationKind.Marking => 3,
                _ => -1
            };
            _processing.SelectedIndex = (int)chainOp.Processing;
            _chainDirection.SelectedIndex = chainOp.ReverseDirection ? 1 : 0;
            _chainFirstPhase.SelectedIndex = chainOp.StartWithCut ? 0 : 1;
            _startInset.Text = chainOp.StartInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _endInset.Text = chainOp.EndInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _cut.Text = chainOp.CutLengthMm.ToString("R", CultureInfo.InvariantCulture);
            _skip.Text = chainOp.SkipLengthMm.ToString("R", CultureInfo.InvariantCulture);
            break;
        }
        if (_uAssignments.TryGet(selected - 1, out var savedGroup) &&
            savedGroup is not null)
        {
            _uHeight.Text = savedGroup.HeightMm.ToString("R", CultureInfo.InvariantCulture);
            _processing.SelectedIndex = (int)savedGroup.BaseProcessing;
            _cut.Text = savedGroup.CutLengthMm.ToString("R", CultureInfo.InvariantCulture);
            _skip.Text = savedGroup.SkipLengthMm.ToString("R", CultureInfo.InvariantCulture);
        }
        _status.Text = $"Источник DXF LINE №{selected}, слой {line.Layer}: " +
            $"X {line.Start.X:0.###}, Y {line.Start.Y:0.###} → " +
            $"X {line.End.X:0.###}, Y {line.End.Y:0.###}. " + _status.Text;
    }

    private void InspectOpenIntent()
    {
        if (_dxfLines.Count == 0)
        {
            _status.Text = "Нет импортированных DXF LINE для диагностики.";
            return;
        }
        var findings = OpenGeometryIntentReview.Inspect(
            _dxfLines, _sheet, _assignments, _uAssignments, _chainAssignments);
        var accepted = findings.Count(x => x.Status == OpenLineIntentStatus.AssignedPreview);
        var unresolved = findings.Count(x => x.Status == OpenLineIntentStatus.NeedsReview);
        var invalid = findings.Count(x => x.Status == OpenLineIntentStatus.InvalidAssignment);
        _status.Text = $"Проверка GEO-3001: явно назначено: {accepted}; " +
            $"не назначено: {unresolved}; ошибочные назначения: {invalid}. " +
            "Открытая линия не является автоматически дефектом, но назначение " +
            "технологии не означает готовность CAM или допуск станка.";
    }

    private async Task SaveAnnotationsAsync()
    {
        try
        {
            if (_dxfLines.Count == 0)
            {
                _status.Text = "Сначала импортируйте DXF с объектами LINE.";
                return;
            }
            var json = SheetLineAssignmentSidecar.Export(
                _dxfLines, _sheet, _assignments, _uAssignments, _chainAssignments);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить технологические назначения (не DXF)",
                SuggestedFileName = "ai-cutpro-line-operations.json",
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new FilePickerFileType("JSON назначения") { Patterns = ["*.json"] }
                ]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            await writer.FlushAsync();
            _status.Text = $"Сохранено линий: {_assignments.Count}; П-образных групп: {_uAssignments.Count}; цепочек: {_chainAssignments.Count}. DXF не изменён.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or FormatException or NotSupportedException)
        {
            _status.Text = "Сохранение назначений не удалось: " + ex.Message;
        }
    }

    private async Task LoadAnnotationsAsync()
    {
        try
        {
            if (_dxfLines.Count == 0)
            {
                _status.Text = "Сначала откройте исходный DXF.";
                return;
            }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть назначения именно для текущего DXF",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON назначения") { Patterns = ["*.json"] }
                ]
            });
            if (files.Count != 1) return;
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > 8_000_000)
                throw new FormatException("Файл назначений слишком большой.");
            using var reader = new StreamReader(stream);
            var buffer = new char[8_000_001];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length));
                if (read == 0) break;
                length += read;
            }
            if (length > 8_000_000)
                throw new FormatException("Файл назначений слишком большой.");
            SheetLineAssignmentSidecar.Restore(
                new string(buffer, 0, length), _dxfLines, _sheet,
                _assignments, _uAssignments);
            if (_sourceLine.SelectedIndex > 0)
                ApplySelectedLine();
            _status.Text = $"Восстановлено назначений: {_assignments.Count}; П-образных групп: {_uAssignments.Count}; цепочек: {_chainAssignments.Count}. " +
                "Исходный DXF не изменён; управление станком запрещено.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or FormatException or NotSupportedException)
        {
            _status.Text = "Загрузка отклонена: " + ex.Message +
                ". Предыдущие назначения сохранены.";
        }
    }

    private void ApplyPreset()
    {
        if (_sourceLine.SelectedIndex <= 0 || _sourceLine.SelectedIndex > _dxfLines.Count)
        {
            _status.Text = "Сначала выберите импортированную DXF LINE.";
            return;
        }
        var source = _dxfLines[_sourceLine.SelectedIndex - 1];
        if (!Number(_startInset, out var a) || !Number(_endInset, out var b)
            || !Number(_cut, out var cut) || !Number(_skip, out var skip))
        {
            _status.Text = "Шаблон отклонён: нужны числовые параметры.";
            return;
        }
        try
        {
            var operation = SheetLinePresetLibrary.Create(
                (SheetLinePreset)_preset.SelectedIndex,
                source.Start, source.End, _sheet, a, b, cut, skip);
            _kind.SelectedIndex = operation.Kind switch
            {
                SheetOperationKind.OpenThroughCut => 0,
                SheetOperationKind.PerforatedCut => 1,
                SheetOperationKind.BendReference => 2,
                _ => -1
            };
            _processing.SelectedIndex = (int)operation.Processing;
            _startX.Text = operation.Start.X.ToString("R", CultureInfo.InvariantCulture);
            _startY.Text = operation.Start.Y.ToString("R", CultureInfo.InvariantCulture);
            _endX.Text = operation.End.X.ToString("R", CultureInfo.InvariantCulture);
            _endY.Text = operation.End.Y.ToString("R", CultureInfo.InvariantCulture);
            _startInset.Text = operation.StartInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _endInset.Text = operation.EndInsetMm.ToString("R", CultureInfo.InvariantCulture);
            _cut.Text = operation.CutLengthMm.ToString("R", CultureInfo.InvariantCulture);
            _skip.Text = operation.SkipLengthMm.ToString("R", CultureInfo.InvariantCulture);
            RenderPreview();
            _status.Text = "Шаблон подготовлен. Нажмите «Назначить операцию», чтобы сохранить его в сеансе. " +
                _status.Text;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            _status.Text = "Шаблон отклонён: " + ex.Message;
        }
    }

    private void AssignCurrentLine()
    {
        var index = _sourceLine.SelectedIndex - 1;
        if (index < 0 || index >= _dxfLines.Count)
        {
            _status.Text = "Сначала выберите импортированную DXF LINE.";
            return;
        }
        if (!(Number(_startX, out var sx) && Number(_startY, out var sy)
            && Number(_endX, out var ex) && Number(_endY, out var ey)
            && Number(_startInset, out var a) && Number(_endInset, out var b)
            && Number(_cut, out var cut) && Number(_skip, out var skip)))
        {
            _status.Text = "Назначение отклонено: нужны числовые параметры.";
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
        var processing = SelectedProcessing();
        if (processing != SheetLineProcessingMode.PerforatedThroughCut
            && !(processing == SheetLineProcessingMode.AutoLegacy
                 && kind == SheetOperationKind.PerforatedCut))
            cut = skip = 0;
        var imported = _dxfLines[index];
        var result = _assignments.Assign(index, imported.Start, imported.End,
            new(kind, new(sx, sy), new(ex, ey), a, b, cut, skip, processing), _sheet);
        _status.Text = result.IsValid
            ? $"Операция {kind} назначена DXF LINE №{index + 1}. " +
              $"Назначено линий: {_assignments.Count}. Только текущий сеанс; файл DXF не изменён, резка запрещена."
            : "Назначение отклонено: " + string.Join("; ", result.Issues);
    }

    private void AssignUGroup()
    {
        if (_sourceLine.SelectedIndex <= 0 ||
            _sourceLine.SelectedIndex > _dxfLines.Count ||
            !Number(_uHeight, out var height) ||
            !Number(_cut, out var cut) || !Number(_skip, out var skip))
        {
            _status.Text = "Для П-образной группы выберите DXF LINE и числовые параметры.";
            return;
        }
        var annotation = new SheetUShapeAnnotation(
            _sourceLine.SelectedIndex - 1, height, SelectedProcessing(),
            cut, skip);
        var result = _uAssignments.Assign(annotation, _dxfLines, _sheet);
        _status.Text = result.IsValid
            ? $"П-образная группа назначена. Групп: {_uAssignments.Count}. Сохраните JSON, чтобы восстановить её позже."
            : "П-образная группа отклонена: " + string.Join("; ", result.Issues);
    }

    private SheetChainAnnotation? CurrentChainAnnotation()
    {
        var index = _sourceLine.SelectedIndex - 1;
        if (index < 0 || index >= _dxfLines.Count ||
            !Number(_startInset, out var startInset) ||
            !Number(_endInset, out var endInset) ||
            !Number(_cut, out var cut) || !Number(_skip, out var skip))
            return null;
        var kind = _kind.SelectedIndex switch
        {
            0 => SheetOperationKind.OpenThroughCut,
            1 => SheetOperationKind.PerforatedCut,
            2 => SheetOperationKind.BendReference,
            3 => SheetOperationKind.Marking,
            _ => SheetOperationKind.Unassigned
        };
        var processing = SelectedProcessing();
        if (processing != SheetLineProcessingMode.PerforatedThroughCut)
            cut = skip = 0;
        return new SheetChainAnnotation(index, _chainDirection.SelectedIndex == 1,
            kind, processing, startInset, endInset, cut, skip,
            _chainFirstPhase.SelectedIndex != 1);
    }

    private void AssignConnectedChain()
    {
        var annotation = CurrentChainAnnotation();
        if (annotation is null)
        {
            _status.Text = "Выберите DXF LINE и корректные числовые параметры.";
            return;
        }
        var component = DxfOpenLineChainBuilder.Build(
            _dxfLines, annotation.SelectedLineIndex);
        if (component.IsValid && component.SourceIndices.Any(i =>
            _assignments.TryGet(i, out _) || _uAssignments.TryGet(i, out _)))
        {
            _status.Text = "Назначение цепочки отклонено: её линии уже имеют " +
                "отдельные технологические операции. Снимите конфликтующие назначения.";
            return;
        }
        var result = _chainAssignments.Assign(annotation, _dxfLines, _sheet);
        _status.Text = result.IsValid
            ? $"Цепочка назначена. Сохранено цепочек: {_chainAssignments.Count}. Для записи на диск сохраните JSON."
            : "Назначение цепочки отклонено: " + string.Join("; ", result.Issues);
    }

    private void RemoveConnectedChain()
    {
        var index = _sourceLine.SelectedIndex - 1;
        if (index < 0 || index >= _dxfLines.Count)
        {
            _status.Text = "Сначала выберите DXF LINE.";
            return;
        }
        // Any member of the same unambiguous component may locate its owner.
        var chain = DxfOpenLineChainBuilder.Build(_dxfLines, index);
        var keys = _chainAssignments.Snapshot().Keys.ToArray();
        var removed = false;
        if (chain.IsValid)
            foreach (var key in keys)
            {
                var existing = DxfOpenLineChainBuilder.Build(_dxfLines, key);
                if (existing.IsValid &&
                    existing.SourceIndices.Any(chain.SourceIndices.Contains))
                    removed |= _chainAssignments.Remove(key);
            }
        _status.Text = removed
            ? $"Назначение цепочки удалено. Осталось цепочек: {_chainAssignments.Count}."
            : "Для выбранной DXF LINE нет назначенной цепочки.";
    }

    private void RenderMixedOpenChain()
    {
        var index = _sourceCurve.SelectedIndex - 1;
        if (index < 0 || index >= _curves.Count)
        {
            _status.Text = "Выберите импортированную открытую DXF-кривую.";
            return;
        }
        var selected = -1;
        for (var i = 0; i < _allDxfShapes.Count; i++)
            if (ReferenceEquals(_allDxfShapes[i], _curves[index]))
            {
                selected = i;
                break;
            }
        if (selected < 0)
        {
            _status.Text = "Исходный DXF-объект не найден.";
            return;
        }
        var result = MixedOpenChainAssembler.Assemble(
            _allDxfShapes, selected, _sheet);
        if (!result.IsValid || result.AnalyticPath is null)
        {
            _status.Text = "Смешанная цепочка отклонена: " +
                string.Join("; ", result.Issues);
            return;
        }
        var drawingLines = new List<(Point2 Start, Point2 End)>();
        foreach (var segment in result.AnalyticPath.Segments)
        {
            if (segment is PreviewLine line)
                drawingLines.Add((
                    new Point2(line.Start.X, line.Start.Y),
                    new Point2(line.End.X, line.End.Y)));
            else if (segment is PreviewArc arc)
            {
                var startDeg = arc.SweepDegrees >= 0
                    ? arc.StartDegrees : arc.StartDegrees + arc.SweepDegrees;
                var endDeg = arc.SweepDegrees >= 0
                    ? arc.StartDegrees + arc.SweepDegrees : arc.StartDegrees;
                var points = DxfArcPreview.Sample(new DxfArc(
                    "DISPLAY", new Point2(arc.Center.X, arc.Center.Y),
                    arc.RadiusMm, startDeg, endDeg), 5);
                if (arc.SweepDegrees < 0)
                    points = points.Reverse().ToArray();
                for (var i = 1; i < points.Count; i++)
                    drawingLines.Add((points[i - 1], points[i]));
            }
        }
        if (drawingLines.Count > 10000)
        {
            _status.Text = "Смешанная цепочка слишком сложна для предпросмотра.";
            return;
        }
        _drawing.Children.Clear();
        var border = new Rectangle
        {
            Width = 480, Height = 240, Stroke = Brushes.SlateGray,
            StrokeThickness = 2, Fill = Brushes.Transparent
        };
        Canvas.SetLeft(border, 20);
        Canvas.SetTop(border, 15);
        _drawing.Children.Add(border);
        var scale = Math.Min(480 / _sheet.WidthMm, 240 / _sheet.HeightMm);
        foreach (var item in drawingLines)
            _drawing.Children.Add(new Line
            {
                StartPoint = new Point(20 + item.Start.X * scale,
                    255 - item.Start.Y * scale),
                EndPoint = new Point(20 + item.End.X * scale,
                    255 - item.End.Y * scale),
                Stroke = Brushes.SteelBlue,
                StrokeThickness = 2
            });
        _status.Text = $"Смешанная цепочка: {result.SourceIndices.Count} DXF-объектов; " +
            $"длина {result.LengthMm:0.###} мм. Аналитические дуги сохранены. " +
            "Только изображение: CAM, Cut/Skip и управление станком запрещены.";
    }

    private void RenderOpenCurve()
    {
        var index = _sourceCurve.SelectedIndex - 1;
        if (index < 0 || index >= _curves.Count)
        {
            _status.Text = "Выберите импортированный ARC или открытую POLYLINE.";
            return;
        }
        var preview = DxfOpenCurveInspector.Inspect(_curves[index], _sheet);
        _drawing.Children.Clear();
        if (!preview.IsValid)
        {
            _status.Text = "Предпросмотр кривой отклонён: " +
                string.Join("; ", preview.Issues);
            return;
        }
        var border = new Rectangle
        {
            Width = 480, Height = 240, Stroke = Brushes.SlateGray,
            StrokeThickness = 2, Fill = Brushes.Transparent
        };
        Canvas.SetLeft(border, 20);
        Canvas.SetTop(border, 15);
        _drawing.Children.Add(border);
        var scale = Math.Min(480 / _sheet.WidthMm, 240 / _sheet.HeightMm);
        for (var i = 1; i < preview.DisplayVertices.Count; i++)
        {
            var start = preview.DisplayVertices[i - 1];
            var end = preview.DisplayVertices[i];
            _drawing.Children.Add(new Line
            {
                StartPoint = new Point(20 + start.X * scale, 255 - start.Y * scale),
                EndPoint = new Point(20 + end.X * scale, 255 - end.Y * scale),
                Stroke = Brushes.SteelBlue,
                StrokeThickness = 2
            });
        }
        _status.Text = $"Предпросмотр кривой: {_curves[index].GetType().Name}; " +
            $"аналитическая длина {preview.SourceLengthMm:0.###} мм. " +
            "Точки нужны только для изображения: Cut/Skip, G-code и управление станком недоступны.";
    }

    private void RenderConnectedChain()
    {
        var selected = _sourceLine.SelectedIndex - 1;
        if (selected < 0 || selected >= _dxfLines.Count)
        {
            _status.Text = "Сначала выберите импортированную DXF LINE.";
            return;
        }
        var chain = DxfOpenLineChainBuilder.Build(
            _dxfLines, selected, _chainDirection.SelectedIndex == 1);
        if (!chain.IsValid)
        {
            _status.Text = "Цепочка не построена: " + string.Join("; ", chain.Issues);
            return;
        }
        if (!Number(_startInset, out var a) || !Number(_endInset, out var b) ||
            !Number(_cut, out var cut) || !Number(_skip, out var skip))
        {
            _status.Text = "Введите числовые параметры цепочки.";
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
        var treatment = SelectedProcessing();
        if (treatment != SheetLineProcessingMode.PerforatedThroughCut)
            cut = skip = 0;
        var result = SheetOpenChainPlanner.Inspect(
            chain.Vertices, _sheet, kind, treatment, a, b, cut, skip,
            startWithCut: _chainFirstPhase.SelectedIndex != 1);
        if (!result.IsValid)
        {
            _status.Text = "Цепочка отклонена: " + string.Join("; ", result.Issues);
            return;
        }
        _drawing.Children.Clear();
        var rectangle = new Rectangle
        {
            Width = 480, Height = 240, Stroke = Brushes.SlateGray,
            StrokeThickness = 2, Fill = Brushes.Transparent
        };
        Canvas.SetLeft(rectangle, 20);
        Canvas.SetTop(rectangle, 15);
        _drawing.Children.Add(rectangle);
        var scale = Math.Min(480 / _sheet.WidthMm, 240 / _sheet.HeightMm);
        foreach (var segment in result.Segments)
        {
            _drawing.Children.Add(new Line
            {
                StartPoint = new Point(20 + segment.Start.X * scale,
                    255 - segment.Start.Y * scale),
                EndPoint = new Point(20 + segment.End.X * scale,
                    255 - segment.End.Y * scale),
                Stroke = segment.IsCut ? Brushes.IndianRed : Brushes.SteelBlue,
                StrokeThickness = segment.IsCut ? 3 : 2
            });
        }
        _status.Text = $"Предпросмотр цепочки: {chain.SourceIndices.Count} DXF LINE, " +
            $"{result.Segments.Count} технологических сегментов. " +
            "Только предпросмотр, назначения цепочки ещё не сохраняются; CAM запрещён.";
    }

    private void RenderUPreview()
    {
        if (_sourceLine.SelectedIndex <= 0 || _sourceLine.SelectedIndex > _dxfLines.Count
            || !Number(_uHeight, out var height))
        {
            _status.Text = "Выберите DXF LINE-основание и числовую высоту П-образной прорези.";
            return;
        }
        var selected = _dxfLines[_sourceLine.SelectedIndex - 1];
        if (Math.Abs(selected.Start.Y - selected.End.Y) > 1e-8)
        {
            _status.Text = "П-образный шаблон пока поддерживает только горизонтальную DXF LINE.";
            return;
        }
        var treatment = SelectedProcessing();
        // For this specific multi-edge template the base must have an
        // explicit treatment, never infer a cutting mode from line style.
        if (treatment == SheetLineProcessingMode.AutoLegacy)
        {
            _status.Text = "Выберите способ обработки основания: без резки, надрез, сквозной или Cut/Skip.";
            return;
        }
        if (!Number(_cut, out var cut) || !Number(_skip, out var skip))
        {
            _status.Text = "Укажите числовые параметры Cut/Skip.";
            return;
        }
        var result = SheetUShapedPreset.Inspect(
            _sheet, Math.Min(selected.Start.X, selected.End.X),
            selected.Start.Y, Math.Abs(selected.End.X - selected.Start.X),
            height, treatment, cut, skip);
        if (!result.IsValid)
        {
            _status.Text = "П-образный предпросмотр отклонён: " +
                string.Join("; ", result.Issues);
            return;
        }
        _drawing.Children.Clear();
        var rectangle = new Rectangle
        {
            Width = 480, Height = 240, Stroke = Brushes.SlateGray,
            StrokeThickness = 2, Fill = Brushes.Transparent
        };
        Canvas.SetLeft(rectangle, 20);
        Canvas.SetTop(rectangle, 15);
        _drawing.Children.Add(rectangle);
        var scale = Math.Min(480 / _sheet.WidthMm, 240 / _sheet.HeightMm);
        foreach (var segment in result.Segments)
        {
            _drawing.Children.Add(new Line
            {
                StartPoint = new Point(20 + segment.Start.X * scale,
                    255 - segment.Start.Y * scale),
                EndPoint = new Point(20 + segment.End.X * scale,
                    255 - segment.End.Y * scale),
                Stroke = segment.IsCut ? Brushes.IndianRed : Brushes.SteelBlue,
                StrokeThickness = segment.IsCut ? 3 : 2
            });
        }
        _status.Text = "П-образный предпросмотр: 3 стороны реза, " +
            "четвёртая сторона по выбранному способу обработки. " +
            "Нет проверки прочности сгиба, нет CAM и управления станком.";
    }

    private SheetLineProcessingMode SelectedProcessing()
        => _processing.SelectedIndex switch
        {
            0 => SheetLineProcessingMode.AutoLegacy,
            1 => SheetLineProcessingMode.NoCut,
            2 => SheetLineProcessingMode.SurfaceScore,
            3 => SheetLineProcessingMode.ThroughCut,
            4 => SheetLineProcessingMode.PerforatedThroughCut,
            _ => (SheetLineProcessingMode)(-1)
        };

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
        var processing = SelectedProcessing();
        if (processing != SheetLineProcessingMode.PerforatedThroughCut
            && !(processing == SheetLineProcessingMode.AutoLegacy
                 && kind == SheetOperationKind.PerforatedCut))
            cut = skip = 0;
        var result = SheetOperationPlanner.InspectOpenLine(
            new(kind, new(sx, sy), new(ex, ey), a, b, cut, skip, processing),
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
