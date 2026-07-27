using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IShellIntegrationService
{
    bool IsInstalled();

    void InstallPerUser(string executablePath, IReadOnlyList<ConfiguredLocation> destinations);

    void UninstallPerUser();
}
