using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.IO.Pipes;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private const string SingleInstanceMutexName = "LuiP.FileManager.App.SingleInstance";
	private const string SingleInstancePipeName = "LuiP.FileManager.App.Activation";
	private static readonly IAppPathsProvider _pathsProvider = new WindowsAppPathsProvider();
	private static Mutex? _singleInstanceMutex;
	private bool _mainWindowShown;
	private CancellationTokenSource? _singleInstanceCts;
	private Task? _singleInstanceListenerTask;

	protected override void OnStartup(StartupEventArgs e)
	{
		if (!TryAcquireSingleInstance())
		{
			SignalPrimaryInstanceToActivate();
			Shutdown(0);
			return;
		}

		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
		DispatcherUnhandledException += App_OnDispatcherUnhandledException;

		EnsureInfrastructureInitialized();
		StartSingleInstanceListener();

		base.OnStartup(e);
		MainWindow? mainWindow = null;

		try
		{
			mainWindow = new MainWindow();
			MainWindow = mainWindow;
			mainWindow.Show();
			_mainWindowShown = true;
		}
		catch (Exception ex)
		{
			LogUnhandledException(ex, "Application.OnStartup");
			MessageBox.Show(
				$"Startup failed:\n{ex.Message}\n\nDetails were written to diagnostics logs.",
				"Lui-P File Manager Error",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			Shutdown(-1);
			return;
		}

		if (TryGetShellCopyRequest(e.Args, out var shellRequest))
		{
			_ = mainWindow.HandleShellCopyAsync(shellRequest.Paths, shellRequest.DestinationId);
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		try
		{
			_singleInstanceCts?.Cancel();
			_singleInstanceListenerTask?.Wait(TimeSpan.FromMilliseconds(500));
		}
		catch
		{
			// Best effort shutdown cleanup.
		}
		finally
		{
			_singleInstanceCts?.Dispose();
			_singleInstanceCts = null;
			_singleInstanceMutex?.ReleaseMutex();
			_singleInstanceMutex?.Dispose();
			_singleInstanceMutex = null;
		}

		base.OnExit(e);
	}

	private static bool TryAcquireSingleInstance()
	{
		var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
		if (!createdNew)
		{
			mutex.Dispose();
			return false;
		}

		_singleInstanceMutex = mutex;
		return true;
	}

	private static void SignalPrimaryInstanceToActivate()
	{
		try
		{
			using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
			client.Connect(1200);
			using var writer = new StreamWriter(client) { AutoFlush = true };
			writer.WriteLine("activate");
		}
		catch
		{
			// Primary instance may be starting up; no further action needed.
		}
	}

	private void StartSingleInstanceListener()
	{
		_singleInstanceCts = new CancellationTokenSource();
		var token = _singleInstanceCts.Token;

		_singleInstanceListenerTask = Task.Run(async () =>
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					await using var server = new NamedPipeServerStream(
						SingleInstancePipeName,
						PipeDirection.In,
						1,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous);

					await server.WaitForConnectionAsync(token).ConfigureAwait(false);
					using var reader = new StreamReader(server);
					_ = await reader.ReadLineAsync().ConfigureAwait(false);

					await Dispatcher.InvokeAsync(() =>
					{
						if (MainWindow is MainWindow mainWindow)
						{
							mainWindow.RestoreFromExternalActivation();
						}
					}, DispatcherPriority.Normal);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch
				{
					// Keep listener resilient to transient pipe errors.
				}
			}
		}, token);
	}

	private static void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			LogUnhandledException(ex, "AppDomain.UnhandledException");
		}
	}

	private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		LogUnhandledException(e.Exception, "Application.DispatcherUnhandledException");
		MessageBox.Show(
			$"Unexpected error:\n{e.Exception.Message}\n\nDetails were written to diagnostics logs.",
			"Lui-P File Manager Error",
			MessageBoxButton.OK,
			MessageBoxImage.Error);

		if (!_mainWindowShown || MainWindow is null || !MainWindow.IsVisible)
		{
			e.Handled = true;
			Shutdown(-1);
			return;
		}

		e.Handled = true;
	}

	private static void LogUnhandledException(Exception ex, string source)
	{
		if (TryWriteCrashLog(_pathsProvider.GetDiagnosticsLogRoot(), ex, source))
		{
			return;
		}

		var fallbackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuiPFileManager", "logs");
		_ = TryWriteCrashLog(fallbackRoot, ex, source);
	}

	private static bool TryWriteCrashLog(string logRoot, Exception ex, string source)
	{
		try
		{
			Directory.CreateDirectory(logRoot);
			var logPath = Path.Combine(logRoot, "crash.log");
			var entry =
				$"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
				$"{ex}{Environment.NewLine}{Environment.NewLine}";
			File.AppendAllText(logPath, entry);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureInfrastructureInitialized()
	{
		IAppPathsProvider pathsProvider = new WindowsAppPathsProvider();
		IDatabaseInitializer databaseInitializer = new SqliteDatabaseInitializer(pathsProvider);
		databaseInitializer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

		var recoveryMaintenance = new RecoveryMaintenanceService(pathsProvider);
		recoveryMaintenance.CleanupExpiredRecoveryData(retentionDays: 30);
	}

	private static async Task ShowInterruptedOperationsIfAnyAsync(Window owner)
	{
		try
		{
			var pathsProvider = new WindowsAppPathsProvider();
			IActivityLogRepository activityLogRepository = new SqliteActivityLogRepository(pathsProvider);
			var interrupted = await activityLogRepository.GetInterruptedAsync(CancellationToken.None).ConfigureAwait(true);
			if (interrupted.Count == 0)
			{
				return;
			}

			if (!owner.Dispatcher.CheckAccess())
			{
				await owner.Dispatcher.InvokeAsync(() => ShowInterruptedWindow(owner, interrupted), DispatcherPriority.Background);
				return;
			}

			ShowInterruptedWindow(owner, interrupted);
		}
		catch
		{
			// Startup should never fail because interrupted operations could not be read.
		}
	}

	private static void ShowInterruptedWindow(Window owner, IReadOnlyList<FileOperationRecord> interrupted)
	{
		var window = new InterruptedOperationsWindow(interrupted)
		{
			Owner = owner
		};

		window.ShowDialog();
	}

	private static bool TryGetShellCopyRequest(string[] args, out ShellCopyRequest request)
	{
		request = new ShellCopyRequest(new List<string>(), null);
		if (args is null || args.Length < 2)
		{
			return false;
		}

		var destinationId = (string?)null;
		var startIndex = 1;

		if (string.Equals(args[0], "--shell-copy-destination", StringComparison.OrdinalIgnoreCase))
		{
			if (args.Length < 3)
			{
				return false;
			}

			destinationId = args[1];
			startIndex = 2;
		}
		else if (!string.Equals(args[0], "--shell-copy", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var paths = new List<string>();

		foreach (var arg in args.Skip(startIndex))
		{
			if (string.IsNullOrWhiteSpace(arg))
			{
				continue;
			}

			paths.Add(arg);
		}

		if (paths.Count == 0)
		{
			return false;
		}

		request = new ShellCopyRequest(paths, destinationId);
		return true;
	}

	private sealed record ShellCopyRequest(IReadOnlyList<string> Paths, string? DestinationId);
}

