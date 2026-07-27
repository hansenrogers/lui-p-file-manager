using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class RecoveryMaintenanceService
{
    private readonly IAppPathsProvider _pathsProvider;

    public RecoveryMaintenanceService(IAppPathsProvider pathsProvider)
    {
        _pathsProvider = pathsProvider;
    }

    public void CleanupExpiredRecoveryData(int retentionDays)
    {
        var recoveryRoot = _pathsProvider.GetRecoveryRoot();
        if (!Directory.Exists(recoveryRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Math.Abs(retentionDays));
        foreach (var directory in Directory.EnumerateDirectories(recoveryRoot))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var lastWrite = info.LastWriteTimeUtc;
                if (lastWrite < cutoff)
                {
                    info.Delete(recursive: true);
                }
            }
            catch
            {
                // Cleanup is best-effort and should never block startup.
            }
        }
    }
}
