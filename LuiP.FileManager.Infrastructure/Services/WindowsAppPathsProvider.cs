using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class WindowsAppPathsProvider : IAppPathsProvider
{
    private const string ProductFolderName = "Lui-P File Manager";

    public string GetAppDataRoot()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonAppData, ProductFolderName);
    }

    public string GetDatabasePath()
    {
        return Path.Combine(GetAppDataRoot(), "data", "luip_file_manager.db");
    }

    public string GetRecoveryRoot()
    {
        return Path.Combine(GetAppDataRoot(), "recovery");
    }

    public string GetDiagnosticsLogRoot()
    {
        return Path.Combine(GetAppDataRoot(), "logs");
    }
}
