using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard;

public partial class App : Application
{
    private const string ActivationCommand = "ACTIVATE";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _pipeCts;
    private string _pipeName = string.Empty;

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
            RunLog.WriteException("DispatcherUnhandledException", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                RunLog.WriteException("UnhandledException", exception);
            else
                RunLog.Write("UnhandledException: " + e.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RunLog.WriteException("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("无法获取当前用户 SID。");
            var sessionId = Process.GetCurrentProcess().SessionId;
            var safeSid = sid.Replace('-', '_');
            var identityKey = $"{safeSid}_{sessionId}";
            var mutexName = $"Local\\DiskSpaceGuard_{identityKey}";
            _pipeName = $"DiskSpaceGuard.Activation.{identityKey}";

            _mutex = new Mutex(initiallyOwned: false, name: mutexName);
            try { _ownsMutex = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }
        catch (Exception ex)
        {
            RunLog.WriteException("SingleInstance", ex);
            MessageBox.Show($"无法初始化单实例保护：{ex.Message}", "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!_ownsMutex)
        {
            TrySignalExistingInstance();
            Shutdown(0);
            return;
        }

        var settings = SettingsStore.Load();
        Loc.Instance.SetLanguage(settings.Language == "en-US" ? UiLanguage.EnUs : UiLanguage.ZhCn);
        ThemeService.Apply(settings.FollowSystemTheme
            ? ThemeService.SystemTheme()
            : AppTheme.Light);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 系统注销/关机时不能被“最小化到托盘”拦截。
        SessionEnding += (_, _) => window.PrepareForSystemShutdown();

        _pipeCts = new CancellationTokenSource();
        _ = RunPipeServerAsync(_pipeCts.Token);
    }

    private void TrySignalExistingInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                client.Connect(timeout: 500);
                var payload = Encoding.ASCII.GetBytes(ActivationCommand + "\n");
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return;
            }
            catch (TimeoutException) { Thread.Sleep(180); }
            catch (IOException) { Thread.Sleep(180); }
            catch (UnauthorizedAccessException) { return; }
        }
        // Do not launch a second UI if the first process owns the mutex but its pipe is unavailable.
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using (server)
                {
                    var command = await ReadBoundedLineAsync(server, 32, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(command, ActivationCommand, StringComparison.Ordinal))
                    {
                        await Dispatcher.InvokeAsync(ActivateMainWindow);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Activation pipe error: {ex}");
                server?.Dispose();
                try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task<string> ReadBoundedLineAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        var bytes = new List<byte>(maxBytes);
        var one = new byte[1];
        while (bytes.Count < maxBytes)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not Window window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
