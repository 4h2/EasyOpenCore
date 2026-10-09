using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EasyOpenCore.App.ViewModels;
using EasyOpenCore.Core;
using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Usb;
using Microsoft.Win32;

namespace EasyOpenCore.App;

public sealed record LanguageOption(string Code, string Label)
{
    // Used by UI Automation / screen readers as the list item name.
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();

        var options = Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();
        LanguageList.ItemsSource = options;
        LanguageList.SelectedItem = options.FirstOrDefault(o => o.Code == Loc.Instance.Language);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar()
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private async void OnRescan(object sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageList.SelectedItem is not LanguageOption option || option.Code == Loc.Instance.Language)
            return;
        Loc.Instance.SetLanguage(option.Code);
        App.Settings.Language = option.Code;
        App.Settings.Save();
    }

    private async void OnBuild(object sender, RoutedEventArgs e) => await Vm.Build.BuildAsync();

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("build.folder_label") };
        if (dialog.ShowDialog(this) == true)
            Vm.Build.OutputFolder = Path.Combine(dialog.FolderName, "EasyOpenCore-EFI");
    }

    private void OnOpenEfi(object sender, RoutedEventArgs e)
    {
        if (Vm.Build.LastEfiFolder is { } folder && Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private void OnToggleDiscovery(object sender, RoutedEventArgs e) => Vm.Usb.ToggleDiscovery();

    private void OnResetUsbMap(object sender, RoutedEventArgs e)
    {
        if (Confirm(Loc.T("usb.confirm_reset")))
            Vm.Usb.Reset();
    }

    private void OnInstallUsbMap(object sender, RoutedEventArgs e)
    {
        if (Vm.Build.LastEfiFolder is not { } folder)
        {
            Vm.Usb.Message = Loc.T("usb.no_efi");
            return;
        }
        try
        {
            UsbMapInstaller.Install(folder, Vm.Usb.Map);
            Vm.Usb.Message = Loc.T("usb.installed", folder);
        }
        catch (IOException ex)
        {
            Vm.Usb.Message = ex.Message;
        }
    }

    private void OnRefreshDisks(object sender, RoutedEventArgs e) => Vm.Installer.RefreshDisks();

    private async void OnCreateUsb(object sender, RoutedEventArgs e) => await Vm.Installer.CreateAsync(Confirm);

    private void OnPostInstallBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("postinstall.folder") };
        if (dialog.ShowDialog(this) == true)
            Vm.PostInstall.Open(dialog.FolderName);
    }

    private void OnPostInstallOpen(object sender, RoutedEventArgs e) => Vm.PostInstall.Open(Vm.PostInstall.Folder);

    private void OnPostInstallUseLast(object sender, RoutedEventArgs e) => Vm.PostInstall.UseLastBuilt();

    private void OnPostInstallDiscard(object sender, RoutedEventArgs e) => Vm.PostInstall.Discard();

    private async void OnPostInstallApply(object sender, RoutedEventArgs e) => await Vm.PostInstall.ApplyAsync();

    private async void OnTroubleFix(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TroubleFixViewModel fix, Tag: TroubleViewModel card })
            card.Message = await Vm.PostInstall.ApplyFixAsync(fix.TweakId, fix.On);
    }

    private void OnCopyCommands(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TroubleViewModel card })
        {
            Clipboard.SetText(card.CommandsText);
            card.Message = Loc.T("trouble.copied");
        }
    }

    private void OnOpenUrl(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.StartsWith("https://", StringComparison.Ordinal))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "EasyOpenCore", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (Vm.Report is null)
            return;
        var dialog = new SaveFileDialog
        {
            FileName = $"hardware-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = $"{Loc.T("export.json_filter")}|*.json",
        };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllText(dialog.FileName, ReportSerializer.ToJson(Vm.Report));
    }

    private void OnExportAcpi(object sender, RoutedEventArgs e)
    {
        if (Vm.Report is null)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("export.folder_title") };
        if (dialog.ShowDialog(this) != true)
            return;
        int count = AcpiTableReader.Export(Vm.Report.AcpiTables, dialog.FolderName);
        MessageBox.Show(this, Loc.T("acpi.export_done", count, dialog.FolderName),
            "EasyOpenCore", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
