using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AICutPro.Core;

namespace AICutPro.App;

/// <summary>
/// Read-only, offline diagnostic report for the last DXF import attempt.
/// No automatic repair or cutting approval is exposed by this window.
/// </summary>
public sealed record DxfDiagnosticFinding(DxfDiagnosticHelp Help, string TechnicalMessage, string? Layer = null);

public sealed class DxfDiagnosticsWindow : Window
{
    private readonly TextBlock _details;

    public DxfDiagnosticsWindow(IReadOnlyList<DxfDiagnosticFinding> findings)
    {
        Title = "AI CutPro | Диагностика DXF";
        Width = 940;
        Height = 600;
        MinWidth = 700;
        MinHeight = 440;

        var title = new TextBlock
        {
            Text = $"Диагностика DXF: {findings.Count} замечаний",
            FontSize = 21,
            FontWeight = Avalonia.Media.FontWeight.Bold
        };
        var safety = new TextBlock
        {
            Text = "Диагностика и советы работают без ИИ и интернета. Исправления не применяются автоматически. Резка запрещена.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var list = new ListBox
        {
            ItemsSource = findings.Select(f =>
                new ListBoxItem
                {
                    Content = $"{f.Help.Code}: {f.Help.TitleRu}" + (f.Layer is null ? "" : $" (слой {f.Layer})"),
                    Tag = f
                }).ToList()
        };
        _details = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*")
        };
        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(new TextBlock
        {
            Text = "Найденные ошибки и предупреждения",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        left.Children.Add(list);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new ScrollViewer { Content = _details, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(18) };
        var heading = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(title);
        heading.Children.Add(safety);
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(grid);
        Content = panel;

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: DxfDiagnosticFinding finding })
                ShowDetails(finding);
        };
        if (findings.Count > 0)
            list.SelectedIndex = 0;
        else
            _details.Text = "Пока нет диагностических записей. Откройте DXF для проверки. " +
                            "Отсутствие сообщений само по себе не означает допуска к резке.";
    }

    private void ShowDetails(DxfDiagnosticFinding finding)
    {
        var help = finding.Help;
        _details.Text =
            $"Код: {help.Code}\n" +
            $"Проблема: {help.TitleRu}\n\n" +
            $"Что произошло: {help.ExplanationRu}\n\n" +
            $"Как исправить: {help.AdviceRu}\n\n" +
            $"Автоматическое исправление: {(help.RepairAvailable ? "доступно" : "пока недоступно")}\n\n" +
            $"Технические подробности:\n{finding.TechnicalMessage}\n\n" +
            "Сохраняйте исходный DXF. Непроверенная геометрия не допускается к резке.";
    }
}
