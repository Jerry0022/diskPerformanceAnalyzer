using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class IntegrationViewModelTests
{
    [Fact]
    public void FreshViewModel_IsIdleAndNotInstalled()
    {
        var vm = new IntegrationViewModel();

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsInstalled);
        Assert.False(vm.CanToggleShortcut);
        Assert.False(vm.CanToggleTask);
        Assert.True(vm.ShowInstallButton);
        Assert.Equal("Install", vm.InstallButtonText);
        Assert.Contains("Not installed", vm.InstallStateText);
        Assert.Equal(OperatingSystem.IsWindows(), vm.IsSupported);
    }

    [Fact]
    public void InstallStateText_DistinguishesInstalledCopyFromOtherCopy()
    {
        var vm = new IntegrationViewModel { IsInstalled = true };
        Assert.Equal("Reinstall", vm.InstallButtonText);
        Assert.Contains("another copy", vm.InstallStateText);

        vm.IsRunningInstalledCopy = true;
        Assert.False(vm.CanInstall);
        Assert.False(vm.ShowInstallButton);
        Assert.Equal("Running the installed copy.", vm.InstallStateText);
    }

    [Fact]
    public void ProfileWarning_BlocksEveryChange()
    {
        var vm = new IntegrationViewModel { IsInstalled = true, ProfileWarning = "signed in as A, running as B" };

        Assert.True(vm.HasProfileWarning);
        Assert.False(vm.CanInstall);
        Assert.False(vm.CanUninstall);
        Assert.False(vm.CanToggleShortcut);
        Assert.False(vm.CanToggleTask);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshAsync_OnUnsupportedPlatform_IsNoOp()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Touches schtasks + the user's profile on Windows; covered by the manual test plan.
        }

        var vm = new IntegrationViewModel();
        await vm.RefreshAsync();

        Assert.False(vm.IsInstalled);
        Assert.False(vm.StartWithWindows);
        Assert.False(vm.StartMenuShortcut);
        Assert.Null(vm.ErrorText);
    }
}
