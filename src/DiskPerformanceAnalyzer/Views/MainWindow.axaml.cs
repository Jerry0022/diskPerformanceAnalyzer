using Avalonia.Controls;
using Avalonia.Input;
using DiskPerformanceAnalyzer.ViewModels;
using LiveChartsCore.Drawing;

namespace DiskPerformanceAnalyzer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Chart.PointerPressed += OnChartPointerPressed;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ClipboardWriter = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            }
        };
    }

    private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !e.GetCurrentPoint(Chart).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(Chart);
        var data = Chart.ScalePixelsToData(new LvcPointD(p.X, p.Y));
        if (double.IsNaN(data.X) || data.X <= 0 || data.X > DateTime.MaxValue.Ticks)
        {
            return;
        }

        var clicked = new DateTime((long)data.X, DateTimeKind.Local);
        vm.FreezeAt(new DateTimeOffset(clicked));
    }
}
