using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GeminiDesk;

internal static class FileAssociationService
{
    private const string FileExtension = ".bunnykeys";
    private const string ProgId = "BunnyDesk.KeyPreset";
    private const uint AssociationChanged = 0x08000000;

    public static void RegisterKeyPresetIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "bunny-keys.ico");
            if (!File.Exists(iconPath))
            {
                return;
            }

            var changed = false;
            using var classesKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using var extensionKey = classesKey.CreateSubKey(FileExtension);
            changed |= SetStringValue(extensionKey, string.Empty, ProgId);

            using var progIdKey = classesKey.CreateSubKey(ProgId);
            changed |= SetStringValue(progIdKey, string.Empty, "Bunny Desk 키 프리셋");

            using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
            changed |= SetStringValue(iconKey, string.Empty, $"\"{iconPath}\",0");

            if (changed)
            {
                SHChangeNotify(AssociationChanged, 0, nint.Zero, nint.Zero);
            }
        }
        catch
        {
            // 파일 아이콘 등록 실패가 앱 실행을 막으면 안 됩니다.
        }
    }

    private static bool SetStringValue(RegistryKey key, string name, string value)
    {
        if (string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal))
        {
            return false;
        }

        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        nint item1,
        nint item2);
}
