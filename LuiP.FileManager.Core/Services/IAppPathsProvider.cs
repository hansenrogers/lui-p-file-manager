namespace LuiP.FileManager.Core.Services;

public interface IAppPathsProvider
{
    string GetAppDataRoot();

    string GetDatabasePath();

    string GetRecoveryRoot();

    string GetDiagnosticsLogRoot();
}
