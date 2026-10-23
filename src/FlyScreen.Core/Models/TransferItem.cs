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
    private int _batchIndex;
    private int _batchCount;
    private long _batchTotalBytes;

    public Guid Id { get; set; } = Guid.NewGuid();
    public TransferDirection Direction { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>收件箱缓存路径（可含 guid 前缀）；发送项为源路径；文件夹批量为根目录。</summary>
    public string FullPath { get; set; } = "";
    public long TotalBytes { get; set; }
    public string PeerName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>与对端对齐的传输批次号（删列表时同步清理用）。</summary>
    public long Sequence { get; set; }
    /// <summary>文件夹批量键；单文件为空。</summary>
    public string? GroupKey { get; set; }

    /// <summary>文件夹批量：当前文件序号（从 1 起）；0 表示非批量。</summary>
    public int BatchIndex
    {
        get => _batchIndex;
        set
        {
            if (_batchIndex == value) return;
            _batchIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文件夹批量：总文件数；≤1 表示普通单文件。</summary>
    public int BatchCount
    {
        get => _batchCount;
        set
        {
            if (_batchCount == value) return;
            _batchCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFolderBatch));
        }
    }

    public bool IsFolderBatch => BatchCount > 1;

    /// <summary>文件夹全部文件字节合计（完成后 SizeText 用）。</summary>
    public long BatchTotalBytes
    {
        get => _batchTotalBytes;
        set
        {
            if (_batchTotalBytes == value) return;
            _batchTotalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

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
            OnPropertyChanged();
            if (progressChanged)
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(Status));
            }
            OnPropertyChanged(nameof(SizeText));
        }
    }

    /// <summary>当前单文件进度 0–100；用于行背景与状态列百分比。</summary>
    public double Progress => _progress;

    /// <summary>0–1，供 MAUI ProgressBar 绑定。</summary>
    public double ProgressFraction => Math.Clamp(_progress / 100.0, 0, 1);

    /// <summary>排队/传输中显示行进度背景；完成/失败/取消后隐藏。</summary>
    public bool ShowProgressBackground =>
        State is TransferState.Queued or TransferState.Transferring;

    public TransferState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            if (value is not (TransferState.Queued or TransferState.Transferring))
                _status = StateLabel(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(ShowProgressBackground));
        }
    }

    /// <summary>传输中显示 0%–100%；完成后显示「已完成」等终态文案。</summary>
    public string Status
    {
        get
        {
            if (State is TransferState.Queued or TransferState.Transferring)
                return $"{(int)Math.Round(_progress)}%";
            return _status;
        }
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string DirectionText => Direction == TransferDirection.Send
        ? Loc.T("Xfer.Send")
        : Loc.T("Xfer.Receive");

    /// <summary>语言切换后刷新方向/状态文案。</summary>
    public void RefreshLocalized()
    {
        if (State is not (TransferState.Queued or TransferState.Transferring))
            _status = StateLabel(State);
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(Status));
    }

    public void SetStateLabel(TransferState state)
    {
        _state = state;
        _status = StateLabel(state);
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ShowProgressBackground));
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

            if (State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
            {
                var total = BatchTotalBytes > 0 ? BatchTotalBytes : TotalBytes;
                return Fmt(total);
            }

            // 传输中：当前文件已传/当前文件大小
            return $"{Fmt(BytesTransferred)} / {Fmt(TotalBytes)}";
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
