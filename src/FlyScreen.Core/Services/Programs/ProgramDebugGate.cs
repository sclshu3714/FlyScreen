using System.Collections.ObjectModel;

namespace FlyScreen.Services.Programs;

public enum ProgramDebugPhase
{
    Idle,
    Running,
    Paused
}

enum ProgramDebugStepKind
{
    None,
    Over,
    Into
}

/// <summary>协作式调试闸门：断点 / 单步 / 暂停 Poll / 继续 / 停止。</summary>
public class ProgramDebugGate
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumeTcs;
    private bool _pauseRequested;
    private bool _stopped;
    private int? _pausedLine;
    private ProgramDebugStepKind _stepKind;
    private HashSet<int> _breakpoints = new();
    private readonly Dictionary<string, string> _locals = new(StringComparer.Ordinal);

    public ProgramDebugPhase Phase { get; private set; } = ProgramDebugPhase.Idle;
    public int? PausedLine
    {
        get { lock (_gate) return _pausedLine; }
    }

    public IReadOnlyDictionary<string, string> Locals
    {
        get
        {
            lock (_gate)
                return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_locals));
        }
    }

    public event Action? StateChanged;

    public static ProgramDebugGate? Current { get; set; }

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        lock (_gate)
            _breakpoints = new HashSet<int>(lines.Where(l => l >= 1));
    }

    public void BeginRun()
    {
        lock (_gate)
        {
            _stopped = false;
            _pauseRequested = false;
            _stepKind = ProgramDebugStepKind.None;
            _pausedLine = null;
            _locals.Clear();
            _resumeTcs = null;
            Phase = ProgramDebugPhase.Running;
        }
        Raise();
    }

    public void EndRun()
    {
        lock (_gate)
        {
            _stopped = true;
            _pauseRequested = false;
            _stepKind = ProgramDebugStepKind.None;
            _pausedLine = null;
            _locals.Clear();
            Phase = ProgramDebugPhase.Idle;
            _resumeTcs?.TrySetResult();
            _resumeTcs = null;
        }
        Raise();
    }

    /// <summary>运行中点「暂停」：下一处 Line/Poll 停下。</summary>
    public void RequestPause()
    {
        lock (_gate)
        {
            if (Phase != ProgramDebugPhase.Running) return;
            _pauseRequested = true;
            _stepKind = ProgramDebugStepKind.None;
        }
    }

    /// <summary>暂停中点「继续」：跑到下一断点。</summary>
    public void Continue()
    {
        Resume(ProgramDebugStepKind.None);
    }

    /// <summary>F10 逐过程：下一语句停下（脚本级无调用栈时与步入相同）。</summary>
    public void StepOver()
    {
        Resume(ProgramDebugStepKind.Over);
    }

    /// <summary>F11 逐语句：下一语句停下。</summary>
    public void StepInto()
    {
        Resume(ProgramDebugStepKind.Into);
    }

    private void Resume(ProgramDebugStepKind step)
    {
        TaskCompletionSource? tcs;
        lock (_gate)
        {
            if (Phase != ProgramDebugPhase.Paused) return;
            _pauseRequested = false;
            _stepKind = step;
            _pausedLine = null;
            _locals.Clear();
            Phase = ProgramDebugPhase.Running;
            tcs = _resumeTcs;
            _resumeTcs = null;
        }
        tcs?.TrySetResult();
        Raise();
    }

    public Task HitAsync(int line)
        => HitAsync(line, Array.Empty<(string, object?)>());

    /// <summary>断点行（强制暂停）。</summary>
    public virtual Task HitAsync(int line, params (string Name, object? Value)[] locals)
        => PauseAtAsync(line, locals, force: true);

    /// <summary>每条可执行语句前：断点 / 单步 / 暂停请求时停下。</summary>
    public virtual async Task LineAsync(int line, params (string Name, object? Value)[] locals)
    {
        ThrowIfStopped();
        bool force;
        lock (_gate)
        {
            force = _breakpoints.Contains(line)
                    || _stepKind != ProgramDebugStepKind.None
                    || _pauseRequested;
        }
        if (!force) return;
        await PauseAtAsync(line, locals, force: true).ConfigureAwait(false);
    }

    /// <summary>循环/延时检查点：仅当 RequestPause 时停下。</summary>
    public virtual async Task PollAsync()
    {
        ThrowIfStopped();
        bool need;
        lock (_gate) need = _pauseRequested && Phase == ProgramDebugPhase.Running;
        if (!need) return;
        await PauseAtAsync(0, Array.Empty<(string, object?)>(), force: true).ConfigureAwait(false);
    }

    private async Task PauseAtAsync(int line, (string Name, object? Value)[] locals, bool force)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException();
            if (!force && !_pauseRequested && _stepKind == ProgramDebugStepKind.None
                && Phase != ProgramDebugPhase.Paused)
                return;

            _pauseRequested = false;
            _stepKind = ProgramDebugStepKind.None;
            _pausedLine = line > 0 ? line : _pausedLine;
            _locals.Clear();
            foreach (var (name, value) in locals)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                _locals[name] = FormatValue(value);
            }
            Phase = ProgramDebugPhase.Paused;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeTcs = tcs;
        }
        Raise();
        await tcs.Task.ConfigureAwait(false);
        ThrowIfStopped();
    }

    private void ThrowIfStopped()
    {
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException();
        }
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return "\"" + s + "\"";
        if (value is System.Collections.IEnumerable en and not string)
        {
            try
            {
                var parts = new List<string>();
                foreach (var item in en)
                {
                    parts.Add(item?.ToString() ?? "null");
                    if (parts.Count >= 8)
                    {
                        parts.Add("…");
                        break;
                    }
                }
                return "[" + string.Join(", ", parts) + "]";
            }
            catch { /* fallthrough */ }
        }
        return value.ToString() ?? value.GetType().Name;
    }

    private void Raise()
    {
        try { StateChanged?.Invoke(); } catch { /* ignore */ }
    }
}
