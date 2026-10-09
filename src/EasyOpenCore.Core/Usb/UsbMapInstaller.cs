using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Usb;

/// <summary>Adds (or refreshes) UTBMap.kext in an existing EFI folder and its config.plist.</summary>
public static class UsbMapInstaller
{
    /// <param name="efiDirectory">The folder containing EFI/ (or the EFI folder itself).</param>
    public static void Install(string efiDirectory, UsbMap map)
    {
        var oc = Directory.Exists(Path.Combine(efiDirectory, "EFI", "OC"))
            ? Path.Combine(efiDirectory, "EFI", "OC")
            : Path.Combine(efiDirectory, "OC");
        var configPath = Path.Combine(oc, "config.plist");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("config.plist not found", configPath);

        map.WriteKext(Path.Combine(oc, "Kexts"));

        var config = PlistSerializer.Load(configPath).AsDict;
        var add = config.Dict("Kernel").Array("Add").Items;
        add.RemoveAll(e => (e as PDict)?.GetString("BundlePath") == "UTBMap.kext");

        // Insert right after USBToolBox (UTBMap depends on it), reusing its entry layout.
        int host = add.FindIndex(e => (e as PDict)?.GetString("BundlePath") == "USBToolBox.kext");
        var template = host >= 0 ? (PDict)add[host].Clone() : new PDict();
        template["BundlePath"] = P.Str("UTBMap.kext");
        template["Comment"] = P.Str("UTBMap (EasyOpenCore USB map)");
        template["Enabled"] = P.Bool(true);
        template["ExecutablePath"] = P.Str("");
        template["PlistPath"] = P.Str("Contents/Info.plist");
        template["Arch"] = P.Str("x86_64");
        template["MaxKernel"] = P.Str("");
        template["MinKernel"] = P.Str("");
        add.Insert(host >= 0 ? host + 1 : add.Count, template);

        PlistSerializer.Save(config, configPath);
    }
}
