using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace FlyScreen.Setup.Models;

public enum PublishJobStatus
{
    Waiting,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>右侧打包队列中的一项任务。</summary>
public sealed class PublishJobItem : INotifyPropertyChanged
{
    private double _percent;
    private string _message = "等待执行";
    private PublishJobStatus _status = PublishJobStatus.Waiting;
    private bool _isLogFocused;
    private CancellationTokenSource _cts = new();

    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required PublishTargetItem Target { get; init; }
    public required PublishOptions Options { get; init; }
    public CancellationTokenSource Cts => _cts;
    public ObservableCollection<SetupLogEntry> Logs { get; } = [];

    public double Percent
    {
        get => _percent;
        set
        {
            if (Math.Abs(_percent - value) < 0.01) return;
            _percent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            _message = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public PublishJobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanRetry));
        }
    }

    public bool IsLogFocused
    {
        get => _isLogFocused;
        set
        {
            if (_isLogFocused == value) return;
            _isLogFocused = value;
            OnPropertyChanged();
        }
    }

    public string StatusLine
    {
        get
        {
            var pct = (int)Math.Round(Percent);
            return Status switch
            {
                PublishJobStatus.Waiting => "等待执行",
                PublishJobStatus.Running => string.IsNullOrWhiteSpace(Message)
                    ? $"打包中  {pct}%"
                    : $"{Message}  {pct}%",
                PublishJobStatus.Completed => string.IsNullOrWhiteSpace(Message)
                    ? "已完成"
                    : Message,
                PublishJobStatus.Failed => string.IsNullOrWhiteSpace(Message)
                    ? "失败"
                    : $"失败：{Message}",
                PublishJobStatus.Cancelled => "已取消",
                _ => Message
            };
        }
    }

    public bool CanDelete => true;

    /// <summary>已完成 / 失败 / 已取消的任务可在原队列项上重新打包。</summary>
    public bool CanRetry => Status is PublishJobStatus.Completed
        or PublishJobStatus.Failed
        or PublishJobStatus.Cancelled;

    /// <summary>重置为等待态并换新取消令牌，保留历史日志；不新增队列项。</summary>
    public void PrepareRetry()
    {
        try { _cts.Dispose(); }
        catch { /* ignore */ }
        _cts = new CancellationTokenSource();
        Percent = 0;
        Message = "等待重新打包";
        Status = PublishJobStatus.Waiting;
        AppendLog("已在原任务上重新排队，等待执行…", SetupLogLevel.Info);
    }

    public void AppendLog(string message, SetupLogLevel? level = null)
    {
        var entry = level == null
            ? SetupLogEntry.FromMessage(message)
            : new SetupLogEntry { Level = level.Value, Message = message };
        Logs.Add(entry);
    }

    public void ClearLogs() => Logs.Clear();

    public string GetLogText()
    {
        var sb = new StringBuilder();
        foreach (var e in Logs)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(e.TimeText).Append("] ")
              .Append(e.LevelText).Append(' ').Append(e.Message);
        }
        return sb.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
