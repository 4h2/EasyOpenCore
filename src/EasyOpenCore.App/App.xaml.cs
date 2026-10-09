using System.Windows;
using EasyOpenCore.Core.Localization;

namespace EasyOpenCore.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        Loc.Instance.SetLanguage(Settings.Language);
        base.OnStartup(e);
    }
}
