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

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
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

    public string DisplaySubtitle => IsLocal ? Loc.T("Slot.Local") : StatusText;

    public void RefreshStatus()
    {
        if (IsLocal)
        {
            StatusText = Loc.T("Slot.Local");
            OnPropertyChanged(nameof(DisplaySubtitle));
            return;
        }

        if (OutboundOk && InboundOk)
            StatusText = Loc.T("Slot.Both");
        else if (OutboundOk)
            StatusText = Loc.T("Slot.OutOnly");
        else if (InboundOk)
            StatusText = Loc.T("Slot.InOnly");
        else
            StatusText = Loc.T("Slot.Disconnected");

        OnPropertyChanged(nameof(DisplaySubtitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
