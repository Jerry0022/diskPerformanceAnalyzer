using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DiskPerformanceAnalyzer.ViewModels;
using DiskPerformanceAnalyzer.Views;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace DiskPerformanceAnalyzer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LiveCharts.Configure(config => config.AddDarkTheme());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => viewModel.Dispose();
            viewModel.Start();

            if (OperatingSystem.IsWindows() && Program.Instance is { } instance)
            {
                instance.ActivateRequested += () => Dispatcher.UIThread.Post(() =>
                {
                    if (desktop.MainWindow is { } window)
                    {
                        if (window.WindowState == WindowState.Minimized)
                        {
                            window.WindowState = WindowState.Normal;
                        }

                        window.Activate();
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
