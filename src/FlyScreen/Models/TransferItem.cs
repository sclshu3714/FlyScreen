using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers;

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
    private string _status = "排队中";
    private long _bytesTransferred;

    public Guid Id { get; init; } = Guid.NewGuid();
    public TransferDirection Direction { get; init; }
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long TotalBytes { get; init; }
    public string PeerName { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set
        {
            if (_bytesTransferred == value) return;
            _bytesTransferred = value;
            OnPropertyChanged();
            Progress = TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * value / TotalBytes, 0, 100);
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public double Progress
    {
        get => _progress;
        private set { if (Math.Abs(_progress - value) > 0.01) { _progress = value; OnPropertyChanged(); } }
    }

    public TransferState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            Status = value switch
            {
                TransferState.Queued => "排队中",
                TransferState.Transferring => "传输中",
                TransferState.Completed => "已完成",
                TransferState.Failed => "失败",
                TransferState.Cancelled => "已取消",
                _ => Status
            };
        }
    }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public string DirectionText => Direction == TransferDirection.Send ? "发送" : "接收";

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
        UiThread.Post(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}
