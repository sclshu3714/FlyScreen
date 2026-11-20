using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using Xunit;

namespace FlyScreen.Core.Tests;

public sealed class ProgramUiCompatibilityTests
{
    [Theory]
    [InlineData(ProgramHostKind.WinWpf, ProgramUiKind.Wpf, true)]
    [InlineData(ProgramHostKind.MauiWindows, ProgramUiKind.Wpf, true)]
    [InlineData(ProgramHostKind.MauiMobile, ProgramUiKind.Wpf, false)]
    [InlineData(ProgramHostKind.AvaloniaDesktop, ProgramUiKind.Wpf, false)]
    [InlineData(ProgramHostKind.MauiWindows, ProgramUiKind.Maui, true)]
    [InlineData(ProgramHostKind.MauiMobile, ProgramUiKind.Maui, true)]
    [InlineData(ProgramHostKind.MauiMacCatalyst, ProgramUiKind.Maui, true)]
    [InlineData(ProgramHostKind.WinWpf, ProgramUiKind.Maui, false)]
    [InlineData(ProgramHostKind.AvaloniaDesktop, ProgramUiKind.Maui, false)]
    [InlineData(ProgramHostKind.AvaloniaDesktop, ProgramUiKind.Avalonia, true)]
    [InlineData(ProgramHostKind.MauiWindows, ProgramUiKind.Avalonia, false)]
    [InlineData(ProgramHostKind.WinWpf, ProgramUiKind.None, true)]
    public void CanRun_MatchesMatrix(ProgramHostKind host, ProgramUiKind ui, bool expected)
        => Assert.Equal(expected, ProgramUiCompatibility.CanRun(host, ui));

    [Theory]
    [InlineData(ProgramHostKind.WinWpf, ProgramUiKind.Wpf)]
    [InlineData(ProgramHostKind.MauiWindows, ProgramUiKind.Maui)]
    [InlineData(ProgramHostKind.MauiMobile, ProgramUiKind.Maui)]
    [InlineData(ProgramHostKind.MauiMacCatalyst, ProgramUiKind.Maui)]
    [InlineData(ProgramHostKind.AvaloniaDesktop, ProgramUiKind.Avalonia)]
    public void DefaultCreateUiKind_MatchesHost(ProgramHostKind host, ProgramUiKind expected)
        => Assert.Equal(expected, ProgramUiCompatibility.DefaultCreateUiKind(host));

    [Fact]
    public void FromCaps_ResolvesProgHost()
    {
        Assert.Equal(ProgramHostKind.WinWpf, ProgramUiCompatibility.FromCaps([PeerCaps.ProgHostWinWpf]));
        Assert.Equal(ProgramHostKind.MauiWindows, ProgramUiCompatibility.FromCaps([PeerCaps.ProgHostMauiWin]));
        Assert.Equal(ProgramHostKind.MauiMobile, ProgramUiCompatibility.FromCaps([PeerCaps.ProgHostMauiMobile]));
        Assert.Equal(ProgramHostKind.MauiMacCatalyst, ProgramUiCompatibility.FromCaps([PeerCaps.ProgHostMauiMac]));
        Assert.Equal(ProgramHostKind.AvaloniaDesktop, ProgramUiCompatibility.FromCaps([PeerCaps.ProgHostAvalonia]));
        Assert.Equal(ProgramHostKind.Unknown, ProgramUiCompatibility.FromCaps([PeerCaps.EdgeMouse]));
    }

    [Fact]
    public void PeerCanRun_Wpf_AcceptsWinWpfAndMauiWin()
    {
        Assert.True(ProgramUiCompatibility.PeerCanRun([PeerCaps.ProgHostWinWpf], ProgramUiKind.Wpf));
        Assert.True(ProgramUiCompatibility.PeerCanRun([PeerCaps.ProgHostMauiWin], ProgramUiKind.Wpf));
        Assert.False(ProgramUiCompatibility.PeerCanRun([PeerCaps.ProgHostMauiMobile], ProgramUiKind.Wpf));
        Assert.False(ProgramUiCompatibility.PeerCanRun([PeerCaps.ProgHostAvalonia], ProgramUiKind.Wpf));
    }
}
