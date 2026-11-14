using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Models;

public sealed class MachineSlot : INotifyPropertyChanged
{
    private string _name = "";
    private bool _enabled;
    private bool _isLocal;
    private bool _isSelected;
    private bool _isDropTarget;
    private string _statusText = Loc.T("Slot.Disconnected");
    private bool _outboundOk;
    private bool _inboundOk;
    private string _resolvedIp = "";

    private int _index;

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionLabel));
        }
    }

    private bool _twoRow;

    /// <summary>单行：左→右；双行：左上/右上/左下/右下。</summary>
    public string PositionLabel
    {
        get
        {
            if (_twoRow)
            {
                return Index switch
                {
                    0 => "左上",
                    1 => "右上",
                    2 => "左下",
                    3 => "右下",
                    _ => $"#{Index + 1}"
                };
            }

            return Index switch
            {
                0 => "最左",
                1 => "中左",
                2 => "中右",
                3 => "最右",
                _ => $"#{Index + 1}"
            };
        }
    }

    public bool IsTwoRow => _twoRow;

    public void SetLayoutHint(bool twoRow)
    {
        _twoRow = twoRow;
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(IsTwoRow));
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(IsEmpty));
            NotifyCardComputed();
        }
    }

    /// <summary>只读展示用；空位显示占位文案。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_name) ? "（空位）" : _name;

    public bool IsEmpty => string.IsNullOrWhiteSpace(_name);

    /// <summary>本机不可移除；有名称的对端可从布局中拿掉，之后可从发现再添加。</summary>
    public bool CanRemove => !IsLocal && !string.IsNullOrWhiteSpace(_name);

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
    }

    public bool IsLocal
    {
        get => _isLocal;
        set
        {
            if (_isLocal == value) return;
            _isLocal = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySubtitle));
            OnPropertyChanged(nameof(CanRemove));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>布局拖拽时，指针下方的可交换目标格高亮。</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardStatus));
        }
    }

    public bool OutboundOk
    {
        get => _outboundOk;
        set { if (_outboundOk != value) { _outboundOk = value; OnPropertyChanged(); RefreshStatus(); } }
    }

    public bool InboundOk
    {
        get => _inboundOk;
        set { if (_inboundOk != value) { _inboundOk = value; OnPropertyChanged(); RefreshStatus(); } }
    }

    public string ResolvedIp
    {
        get => _resolvedIp;
        set { if (_resolvedIp != value) { _resolvedIp = value; OnPropertyChanged(); } }
    }

    private string _boundRemotesText = "";
    private bool _hasActiveRemote;
    private string _activeRemoteId = "";
    private string _activeRemoteName = "";
    private string _activeRemoteStatusText = "";
    private string _activeRemoteModeLabel = "";
    private string _activeRemoteBindHint = "";
    private bool _activeRemoteIsProjecting;
    private bool _canSwitchRemote;
    private int _boundRemoteCount;
    private bool _showAsRemotePrimary;

    /// <summary>挂在本槽位下的远程/中转设备摘要（仅本机展示）。</summary>
    public string BoundRemotesText
    {
        get => _boundRemotesText;
        set
        {
            var v = value ?? "";
            if (_boundRemotesText == v) return;
            _boundRemotesText = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasBoundRemotes));
        }
    }

    public bool HasBoundRemotes => !string.IsNullOrWhiteSpace(_boundRemotesText);

    public bool HasActiveRemote
    {
        get => _hasActiveRemote;
        set
        {
            if (_hasActiveRemote == value) return;
            _hasActiveRemote = value;
            OnPropertyChanged();
            NotifyCardComputed();
        }
    }

    public string ActiveRemoteId
    {
        get => _activeRemoteId;
        set { if (_activeRemoteId == value) return; _activeRemoteId = value ?? ""; OnPropertyChanged(); }
    }

    public string ActiveRemoteName
    {
        get => _activeRemoteName;
        set
        {
            var v = value ?? "";
            if (_activeRemoteName == v) return;
            _activeRemoteName = v;
            OnPropertyChanged();
            NotifyCardComputed();
        }
    }

    public string ActiveRemoteStatusText
    {
        get => _activeRemoteStatusText;
        set
        {
            var v = value ?? "";
            if (_activeRemoteStatusText == v) return;
            _activeRemoteStatusText = v;
            OnPropertyChanged();
            NotifyCardComputed();
        }
    }

    public string ActiveRemoteModeLabel
    {
        get => _activeRemoteModeLabel;
        set { if (_activeRemoteModeLabel == value) return; _activeRemoteModeLabel = value ?? ""; OnPropertyChanged(); }
    }

    public string ActiveRemoteBindHint
    {
        get => _activeRemoteBindHint;
        set { if (_activeRemoteBindHint == value) return; _activeRemoteBindHint = value ?? ""; OnPropertyChanged(); }
    }

    public bool ActiveRemoteIsProjecting
    {
        get => _activeRemoteIsProjecting;
        set
        {
            if (_activeRemoteIsProjecting == value) return;
            _activeRemoteIsProjecting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAsRemotePrimary));
            NotifyCardComputed();
        }
    }

    public bool CanSwitchRemote
    {
        get => _canSwitchRemote;
        set { if (_canSwitchRemote == value) return; _canSwitchRemote = value; OnPropertyChanged(); }
    }

    public int BoundRemoteCount
    {
        get => _boundRemoteCount;
        set { if (_boundRemoteCount == value) return; _boundRemoteCount = value; OnPropertyChanged(); }
    }

    /// <summary>投影中：远程升为主卡内容。</summary>
    public bool ShowAsRemotePrimary
    {
        get => _showAsRemotePrimary;
        set
        {
            if (_showAsRemotePrimary == value) return;
            _showAsRemotePrimary = value;
            OnPropertyChanged();
            NotifyCardComputed();
        }
    }

    public bool ShowRemoteSubCard => HasActiveRemote && !ShowAsRemotePrimary;
    public bool ShowHostHint => ShowAsRemotePrimary;
    public string CardTitle => ShowAsRemotePrimary ? ActiveRemoteName : DisplayName;
    public string CardStatus => ShowAsRemotePrimary
        ? (string.IsNullOrWhiteSpace(ActiveRemoteStatusText) ? Loc.T("Remote.Projecting") : ActiveRemoteStatusText)
        : StatusText;
    public string CardHostHint => ShowAsRemotePrimary && !string.IsNullOrWhiteSpace(Name)
        ? string.Format(Loc.T("Remote.BoundHost"), Name)
        : "";
    public string CardEdgeHint => ShowAsRemotePrimary ? Loc.T("Remote.EdgeToRemote") : "";

    public void ClearRemoteCard()
    {
        BoundRemotesText = "";
        HasActiveRemote = false;
        ActiveRemoteId = "";
        ActiveRemoteName = "";
        ActiveRemoteStatusText = "";
        ActiveRemoteModeLabel = "";
        ActiveRemoteBindHint = "";
        ActiveRemoteIsProjecting = false;
        CanSwitchRemote = false;
        BoundRemoteCount = 0;
        ShowAsRemotePrimary = false;
    }

    private void NotifyCardComputed()
    {
        OnPropertyChanged(nameof(ShowRemoteSubCard));
        OnPropertyChanged(nameof(ShowHostHint));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardStatus));
        OnPropertyChanged(nameof(CardHostHint));
        OnPropertyChanged(nameof(CardEdgeHint));
    }

    public string DisplaySubtitle => IsLocal ? Loc.T("Slot.Local") : StatusText;

    public void RefreshStatus()
    {
        if (IsLocal)
        {
            StatusText = Loc.T("Slot.Local");
            OnPropertyChanged(nameof(DisplaySubtitle));
            return;
        }

        // 单 TCP 全双工：任一方向已连通即视为布局「双向」
        if (OutboundOk || InboundOk)
            StatusText = Loc.T("Slot.Both");
        else
            StatusText = Loc.T("Slot.Disconnected");

        OnPropertyChanged(nameof(DisplaySubtitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
