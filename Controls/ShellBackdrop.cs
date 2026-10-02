using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TinyAtc.Controls;

/// <summary>
/// Acrylic backdrop styled like the shell's own flyouts: neutral by default, tinted with a dark
/// shade of the accent color when the taskbar shows the accent color. Unlike the stock backdrops
/// it stays acrylic while the window is inactive, since the popup is only ever seen in front.
/// </summary>
public sealed partial class ShellBackdrop : SystemBackdrop
{
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;
    private Color? _accentTint;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        _controller = new DesktopAcrylicController();
        ApplyTint();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>Sets the light/dark look and the accent tint (null for the neutral look).</summary>
    public void Update(bool isLight, Color? accentTint)
    {
        _configuration.Theme = isLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        _accentTint = accentTint;
        ApplyTint();
    }

    private void ApplyTint()
    {
        if (_controller is null)
            return;

        if (_accentTint is Color tint)
        {
            _controller.TintColor = tint;
            _controller.FallbackColor = tint;
            _controller.TintOpacity = 0.7f;
            _controller.LuminosityOpacity = 0.9f;
        }
        else
        {
            _controller.ResetProperties();
        }
    }
}
