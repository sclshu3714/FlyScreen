using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

/// <summary>表格行：勾选 + 规则数据。</summary>
public sealed class PortProxyRuleRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public PortProxyRuleRow(PortProxyRuleInfo rule)
    {
        Rule = rule;
    }

    public PortProxyRuleInfo Rule { get; }

    public string KindToken => Rule.KindToken;
    public string ListenAddress => Rule.ListenAddress;
    public string ListenPort => Rule.ListenPort;
    public string ConnectAddress => Rule.ConnectAddress;
    public string ConnectPort => Rule.ConnectPort;
    public string Protocol => Rule.Protocol;

    public string EditLabel => Loc.T("Tool.PortProxy.Edit");
    public string DeleteLabel => Loc.T("Tool.PortProxy.Delete");

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
