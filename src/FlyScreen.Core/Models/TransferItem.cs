using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
// PropertyChanged 经 UiMarshal 回投 UI（由 App 注入）

namespace FlyScreen.Models;

public enum TransferDirection
{
    Send,
    Receive
}

public enum TransferState
{
    Queued,
    Transferring,
    Completed,
    Failed,
    Cancelled
}

public sealed class TransferItem : INotifyPropertyChanged
{
    private double _progress;
    private TransferState _state = TransferState.Queued;
    private string _status = Loc.T("Xfer.Queued");
    private long _bytesTransferred;

    public Guid Id { get; set; } = Guid.NewGuid();
    public TransferDirection Direction { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>收件箱缓存路径（可含 guid 前缀）；发送项为源路径。</summary>
    public string FullPath { get; set; } = "";
    public long TotalBytes { get; set; }
    public string PeerName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set
        {
            if (_bytesTransferred == value) return;
            _bytesTransferred = value;
            var next = TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * value / TotalBytes, 0, 100);
            var progressChanged = Math.Abs(_progress - next) > 0.01;
            if (progressChanged) _progress = next;
            // 合并通知，减少 ListView/ProgressBar 布局风暴
            OnPropertyChanged();
            if (progressChanged) OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public double Progress => _progress;

    public TransferState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            Status = StateLabel(value);
        }
    }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public string DirectionText => Direction == TransferDirection.Send
        ? Loc.T("Xfer.Send")
        : Loc.T("Xfer.Receive");

    /// <summary>语言切换后刷新方向/状态文案。</summary>
    public void RefreshLocalized()
    {
        Status = StateLabel(State);
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(Status));
    }

    private static string StateLabel(TransferState state) => state switch
    {
        TransferState.Queued => Loc.T("Xfer.Queued"),
        TransferState.Transferring => Loc.T("Xfer.Transferring"),
        TransferState.Completed => Loc.T("Xfer.Completed"),
        TransferState.Failed => Loc.T("Xfer.Failed"),
        TransferState.Cancelled => Loc.T("Xfer.Cancelled"),
        _ => Loc.T("Xfer.Queued")
    };

    public string SizeText
    {
        get
        {
            static string Fmt(long b)
            {
                return b switch
                {
                    >= 1L << 30 => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
                    >= 1L << 20 => $"{b / (1024.0 * 1024):0.##} MB",
                    >= 1L << 10 => $"{b / 1024.0:0.##} KB",
                    _ => $"{b} B"
                };
            }

            return State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled
                ? Fmt(TotalBytes)
                : $"{Fmt(BytesTransferred)} / {Fmt(TotalBytes)}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        // 传输进度常在后台线程更新，必须切回 UI 线程再通知绑定
        UiMarshal.Post(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}
