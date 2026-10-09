using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EasyOpenCore.App.ViewModels;
using EasyOpenCore.Core;
using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Localization;
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
