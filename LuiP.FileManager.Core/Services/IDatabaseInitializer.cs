namespace LuiP.FileManager.Core.Services;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
