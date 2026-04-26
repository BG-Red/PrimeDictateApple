using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SharpHook;

namespace PrimeDictate;

public partial class App : System.Windows.Application
{
    private readonly Icon appIcon = AppIconProvider.LoadWindowIcon();
    private readonly Icon trayReadyIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Ready);
    private readonly Icon trayRecordingIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Recording);
    private readonly Icon trayErrorIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Error);
    private readonly DispatcherTimer errorStateTimer;
    private Forms.NotifyIcon? notifyIcon;
    private DictationController? dictationController;
    private GlobalHotkeyListener? hotkeyListener;
    private SettingsStore? settingsStore;
    private AppSettings? settings;
    private Task? hookTask;
    private SettingsWindow? settingsWindow;
    private MainWindow? workspaceWindow;
    private readonly DictationWorkspaceViewModel workspaceViewModel = new();
    private bool isRecording;
    private DateTime errorStateUntilUtc = DateTime.MinValue;

    public App()
    {
        this.errorStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        this.errorStateTimer.Tick += this.OnErrorStateTimerTick;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        this.settingsStore = new SettingsStore();
        this.settings = this.settingsStore.LoadOrDefault();
        this.ApplyModelPathOverride(this.settings);

        var configured = this.settings.DictationHotkey.IsValid(out _)
            ? this.settings.DictationHotkey
            : HotkeyGesture.Default;
        this.settings.DictationHotkey = configured;

        this.dictationController = new DictationController(this.settings.ExclusiveMicAccessWhileDictating);
        this.dictationController.RecordingStateChanged += this.OnRecordingStateChanged;
        this.dictationController.ThreadStarted += this.OnThreadStarted;
        this.dictationController.ThreadCompleted += this.OnThreadCompleted;
        this.dictationController.ThreadTranscriptUpdated += this.OnThreadTranscriptUpdated;
        this.hotkeyListener = new GlobalHotkeyListener(this.dictationController.ToggleRecordingAsync, configured);
        this.hookTask = this.hotkeyListener.RunAsync();
        AppLog.EntryWritten += this.OnAppLogEntryWritten;

        this.notifyIcon = this.CreateNotifyIcon();
        this.UpdateTrayState();

        if (!this.settings.FirstRunCompleted)
        {
            this.ShowSettingsWindow(isFirstRun: true);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (this.hotkeyListener is not null)
        {
            this.hotkeyListener.Dispose();
        }

        if (this.hookTask is not null)
        {
            await StopHookAsync(this.hookTask).ConfigureAwait(false);
        }

        if (this.dictationController is not null)
        {
            this.dictationController.RecordingStateChanged -= this.OnRecordingStateChanged;
            this.dictationController.ThreadStarted -= this.OnThreadStarted;
            this.dictationController.ThreadCompleted -= this.OnThreadCompleted;
            this.dictationController.ThreadTranscriptUpdated -= this.OnThreadTranscriptUpdated;
            await this.dictationController.DisposeAsync().ConfigureAwait(false);
        }

        AppLog.EntryWritten -= this.OnAppLogEntryWritten;

        if (this.notifyIcon is not null)
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
        }

        this.errorStateTimer.Stop();
        this.errorStateTimer.Tick -= this.OnErrorStateTimerTick;
        this.trayReadyIcon.Dispose();
        this.trayRecordingIcon.Dispose();
        this.trayErrorIcon.Dispose();
        this.appIcon.Dispose();

        base.OnExit(e);
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Workspace", null, (_, _) => this.ShowWorkspaceWindow());
        menu.Items.Add("Settings", null, (_, _) => this.ShowSettingsWindow(isFirstRun: false));
        menu.Items.Add("Exit", null, (_, _) => this.Shutdown());

        var icon = new Forms.NotifyIcon
        {
            Icon = this.trayReadyIcon,
            Text = "PrimeDictate - Idle",
            Visible = true,
            ContextMenuStrip = menu
        };

        icon.Click += (_, _) =>
        {
            if (this.settings?.TrayClickBehavior == TrayClickBehavior.SingleClickOpensSettings)
            {
                this.ShowWorkspaceWindow();
            }
        };
        icon.DoubleClick += (_, _) =>
        {
            if (this.settings?.TrayClickBehavior == TrayClickBehavior.DoubleClickOpensSettings)
            {
                this.ShowWorkspaceWindow();
            }
        };

        return icon;
    }

    private void ShowSettingsWindow(bool isFirstRun)
    {
        if (this.settings is null)
        {
            return;
        }

        if (this.settingsWindow is { IsLoaded: true })
        {
            this.settingsWindow.Activate();
            return;
        }

        this.settingsWindow = new SettingsWindow(this.settings, isFirstRun);
        this.settingsWindow.Icon = this.CreateWindowIcon();
        this.settingsWindow.SettingsSaved += this.OnSettingsSaved;
        this.settingsWindow.Closed += (_, _) => this.settingsWindow = null;
        this.settingsWindow.Show();
        this.settingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        if (this.settingsStore is null || this.hotkeyListener is null)
        {
            return;
        }

        this.settings = newSettings;
        this.settingsStore.Save(newSettings);
        this.hotkeyListener.UpdateHotkey(newSettings.DictationHotkey);
        this.dictationController?.UpdateCaptureOptions(newSettings.ExclusiveMicAccessWhileDictating);
        this.ApplyModelPathOverride(newSettings);
    }

    private void OnRecordingStateChanged(bool isRecording)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.isRecording = isRecording;
            this.UpdateTrayState();
        });
    }

    private void UpdateTrayState()
    {
        if (this.notifyIcon is not null)
        {
            var trayState = this.GetTrayState();
            this.notifyIcon.Icon = trayState switch
            {
                TrayVisualState.Recording => this.trayRecordingIcon,
                TrayVisualState.Error => this.trayErrorIcon,
                _ => this.trayReadyIcon
            };

            this.notifyIcon.Text = trayState switch
            {
                TrayVisualState.Recording => this.GetRecordingTooltipText(),
                TrayVisualState.Error => "PrimeDictate - Error",
                _ => "PrimeDictate - Ready"
            };
        }
    }

    private string GetRecordingTooltipText()
    {
        var mode = this.dictationController?.ActiveMicAccessModeLabel ?? "Unknown";
        return mode switch
        {
            "Exclusive" => "PrimeDictate - Listening [Exclusive]",
            "Shared" => "PrimeDictate - Listening [Shared]",
            _ => "PrimeDictate - Listening"
        };
    }

    private void ShowWorkspaceWindow()
    {
        if (this.workspaceWindow is { IsLoaded: true })
        {
            this.workspaceWindow.Activate();
            return;
        }

        this.workspaceWindow = new MainWindow(this.workspaceViewModel);
        this.workspaceWindow.Icon = this.CreateWindowIcon();
        this.workspaceWindow.Closed += (_, _) => this.workspaceWindow = null;
        this.workspaceWindow.Show();
        this.workspaceWindow.Activate();
    }

    private void OnAppLogEntryWritten(AppLogEntry entry)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.workspaceViewModel.AppendEntry(entry);
            if (entry.Level == AppLogLevel.Error)
            {
                this.errorStateUntilUtc = DateTime.UtcNow.AddSeconds(10);
                if (!this.errorStateTimer.IsEnabled)
                {
                    this.errorStateTimer.Start();
                }
            }

            this.UpdateTrayState();
        });
    }

    private void OnThreadStarted(Guid threadId)
    {
        this.Dispatcher.Invoke(() => this.workspaceViewModel.StartThread(threadId));
    }

    private void OnThreadCompleted(Guid threadId)
    {
        this.Dispatcher.Invoke(() => this.workspaceViewModel.MarkThreadCompleted(threadId));
    }

    private void OnThreadTranscriptUpdated(Guid threadId, string transcript)
    {
        this.Dispatcher.Invoke(() =>
        {
            if (this.workspaceViewModel.GetThread(threadId) is { } thread)
            {
                thread.LatestTranscript = transcript;
            }
        });
    }

    private void ApplyModelPathOverride(AppSettings configuredSettings)
    {
        if (string.IsNullOrWhiteSpace(configuredSettings.ModelPath))
        {
            Environment.SetEnvironmentVariable("PRIME_DICTATE_MODEL", null);
            return;
        }

        Environment.SetEnvironmentVariable("PRIME_DICTATE_MODEL", configuredSettings.ModelPath);
    }

    private BitmapSource CreateWindowIcon() =>
        Imaging.CreateBitmapSourceFromHIcon(
            this.appIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

    private TrayVisualState GetTrayState()
    {
        if (this.isRecording)
        {
            return TrayVisualState.Recording;
        }

        if (DateTime.UtcNow <= this.errorStateUntilUtc)
        {
            return TrayVisualState.Error;
        }

        return TrayVisualState.Ready;
    }

    private void OnErrorStateTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow > this.errorStateUntilUtc)
        {
            this.errorStateTimer.Stop();
            this.UpdateTrayState();
        }
    }

    private static async Task StopHookAsync(Task hookTask)
    {
        try
        {
            await hookTask.ConfigureAwait(false);
        }
        catch (HookException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
