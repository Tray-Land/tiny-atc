using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyAtc.Services;

namespace TinyAtc.Views;

/// <summary>
/// The flyout's content. Its data refreshes only while the flyout is on screen; see
/// <see cref="ForegroundPoller"/>.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ForegroundPoller _poller;

    public FlyoutPage()
    {
        InitializeComponent();
        TitleText.Text = App.DisplayName;
        _poller = new ForegroundPoller(DispatcherQueue, RefreshInterval, RefreshAsync);
        _poller.Failed += (_, ex) => ShowError(ex);
    }

    /// <summary>The flyout opened: refresh if the data is stale and poll until it hides.</summary>
    public void OnShown() => _poller.Start();

    /// <summary>The flyout hid: stop polling and abandon any request in flight.</summary>
    public void OnHidden() => _poller.Stop();

    public void Dispose() => _poller.Stop();

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        LoadingRing.IsActive = true;
        try
        {
            // Replace with the app's real fetch; pass cancellationToken through to HttpClient.
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            ValueText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);

            StatusBar.IsOpen = false;
            UpdatedText.Text = $"Updated {DateTime.Now:t}";
            App.Current.SetTrayStatus($"Updated {DateTime.Now:t}");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void ShowError(Exception ex)
    {
        bool online = ConnectivityService.IsInternetAvailable;
        StatusBar.Title = online ? "Can't reach the service" : "No internet connection";
        StatusBar.Message = ex is HttpRequestException or TaskCanceledException
            ? online ? "Retrying shortly." : "This will update when you're back online."
            : ex.Message;
        StatusBar.IsOpen = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = _poller.RefreshNowAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}
