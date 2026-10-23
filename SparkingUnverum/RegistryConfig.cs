using Microsoft.Win32;
using System;

namespace SparkingUnverum
{
    public static class RegistryConfig
    {
        // GameBanana 1-click install buttons open unverum: links, the scheme is shared with the classic Unverum
        // so whichever manager registered it last receives the links
        // .majin files (lists of mods shared by friends) open with Majin Unverum
        public static bool InstallListHandler()
        {
            var appPath = Environment.ProcessPath;
            try
            {
                using (var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ModListShare.Extension))
                    extension.SetValue("", ListType);
                using var type = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ListType);
                type.SetValue("", Loc.T("Share.FileType"));
                using (var icon = type.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{appPath}\",0");
                using var command = type.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{appPath}\" {App.ImportArgument} \"%1\"");
                return true;
            }
            catch
            {
                return false;
            }
        }
        public const string ListType = "MajinUnverum.List";

        public static bool InstallGBHandler()
        {
            var appPath = Environment.ProcessPath;
            try
            {
                using var reg = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Unverum");
                reg.SetValue("", "URL:unverum");
                reg.SetValue("URL Protocol", "");
                using (var icon = reg.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{appPath}\",0");
                using var command = reg.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{appPath}\" -download \"%1\"");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
