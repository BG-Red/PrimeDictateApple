using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed class HeadlessHotkeyListener : IDisposable
{
    private readonly IGlobalHook hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
    private readonly Func<Task> onToggleAsync;
    private readonly Func<Task> onStopAsync;

    public HeadlessHotkeyListener(Func<Task> onToggleAsync, Func<Task> onStopAsync)
    {
        this.onToggleAsync = onToggleAsync;
        this.onStopAsync = onStopAsync;
        this.hook.KeyPressed += this.OnKeyPressed;
    }

    public Task RunAsync() => this.hook.RunAsync();

    public void Dispose()
    {
        this.hook.KeyPressed -= this.OnKeyPressed;
        this.hook.Dispose();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs args)
    {
        var mask = args.RawEvent.Mask;
        Func<Task>? action = null;

        if (args.Data.KeyCode == KeyCode.VcEnter && mask.HasCtrl() && mask.HasShift())
        {
            action = this.onStopAsync;
        }
        else if (args.Data.KeyCode == KeyCode.VcSpace && mask.HasCtrl() && mask.HasShift())
        {
            action = this.onToggleAsync;
        }

        if (action is null)
        {
            return;
        }

        args.SuppressEvent = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Hotkey action failed: {ex.Message}");
            }
        });
    }
}

internal sealed class SharpHookTextInjector
{
    private readonly EventSimulator eventSimulator = new();

    public void InjectText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var result = this.eventSimulator.SimulateTextEntry(text);
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {result}.");
        }
    }
}
