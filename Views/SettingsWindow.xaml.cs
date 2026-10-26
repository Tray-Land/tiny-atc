using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyAtc.Tray;
using TinyAtc.Airports;
using TinyAtc.Formatting;
using TinyAtc.OpenSky;
using TinyAtc.Services;
using Windows.ApplicationModel;
using WinUIEx;

namespace TinyAtc.Views;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly List<Airport> _suggestions = [];
    private bool _loading = true;
    private CancellationTokenSource? _signIn;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = $"{App.DisplayName} Settings";
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowPlacementService.CenterOnPrimary(this, 520, 720);

        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        LoadValues();
        _ = LoadStartupStateAsync();

        SettingsService.Changed += OnSettingsChanged;
        OpenSkyService.AccountChanged += OnAccountChanged;
        Closed += (_, _) =>
        {
            SettingsService.Changed -= OnSettingsChanged;
            OpenSkyService.AccountChanged -= OnAccountChanged;
            _signIn?.Cancel();
        };
        _loading = false;
    }

    private void LoadValues()
    {
        _loading = true;
        LocationModeButtons.SelectedIndex = (int)SettingsService.LocationMode;
        MarqueeToggle.IsOn = SettingsService.MarqueeEnabled;
        SpeedBox.SelectedIndex = (int)SettingsService.MarqueeSpeed;
        AirlineColorsToggle.IsOn = SettingsService.MarqueeAirlineColors;
        UnitsBox.SelectedIndex = (int)SettingsService.Units;
        FillRadiusChoices();
        LoadMessage();
        UpdateLocationStatus();
        UpdateFavorites();
        UpdateAccount();
        _loading = false;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Changes from elsewhere (the tray menu's marquee toggle, the map's Save view) show up here too.
        _loading = true;
        MarqueeToggle.IsOn = SettingsService.MarqueeEnabled;
        UpdateLocationStatus();
        UpdateFavorites();
        _loading = false;
    }

    // ---- Location ----

    private void UpdateLocationStatus()
    {
        bool device = SettingsService.LocationMode == LocationMode.Device;
        LocateButton.Visibility = device ? Visibility.Visible : Visibility.Collapsed;
        LocationStatus.Text = (device ? SettingsService.DeviceLocation : SettingsService.SavedCenter) is { } p
            ? $"{(device ? "Located at" : "Saved view at")} {p.Latitude:0.###}, {p.Longitude:0.###}"
            : device
                ? "Not located yet."
                : "No saved view yet. Pan the map and select Save view.";
    }

    private void LocationModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LocationModeButtons.SelectedIndex >= 0)
        {
            SettingsService.LocationMode = (LocationMode)LocationModeButtons.SelectedIndex;
            UpdateLocationStatus();
        }
    }

    private async void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        LocateButton.IsEnabled = false;
        LocationStatus.Text = "Locating…";
        string? error = await LocationService.RefreshIfStaleAsync(force: true);
        LocateButton.IsEnabled = true;
        UpdateLocationStatus();
        if (error is not null)
        {
            LocationStatus.Text = error;
        }
    }

    // ---- Tray icon ----

    private void FillRadiusChoices()
    {
        UnitSystem units = SettingsService.Units;
        RadiusBox.ItemsSource = SettingsService.RadiusChoicesKm.Select(km => FlightFormat.Distance(km, units)).ToList();
        RadiusBox.SelectedIndex = Array.IndexOf(SettingsService.RadiusChoicesKm, SettingsService.OverheadRadiusKm);
    }

    // ---- Marquee message ----

    private static readonly OverheadFlight SampleFlight = new(
        new StateVector("a1b2c3", "UAL123", "United States", null, 0, -122.3, 47.6, 10668, false, 230, 45, -5, 10700, "1200", 0, 0),
        DistanceKm: 7.4,
        BearingDegrees: 45);

    private readonly ObservableCollection<MarqueeToken> _messageTokens = [];

    private void LoadMessage()
    {
        _loading = true;
        _messageTokens.Clear();
        foreach (string token in SettingsService.MarqueeTokens)
        {
            _messageTokens.Add(MarqueeToken.FromValue(token));
        }

        MessageBox.ItemsSource = _messageTokens;
        MessageBox.SuggestedItemsSource = MarqueeToken.AllFields();
        _messageTokens.CollectionChanged += MessageTokens_CollectionChanged;
        UpdateMessagePreview();
        _loading = false;
    }

    private void MessageTokens_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsService.MarqueeTokens = (string[])[.. _messageTokens.Select(t => t.Value)];
        UpdateMessagePreview();
    }

    private void UpdateMessagePreview()
    {
        string text = Overhead.Describe(SampleFlight, SettingsService.Units, (string[])[.. _messageTokens.Select(t => t.Value)]);
        MessagePreview.Text = _messageTokens.Count == 0 ? $"Empty: shows the default. Example: {text}" : $"Example: {text}";
    }

    private void MessageBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            MessageBox.SuggestedItemsSource = MarqueeToken.AllFields()
                .Where(t => t.Label.Contains(sender.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }
    }

    // Typing a field's name picks that field; anything else becomes literal text.
    private void MessageBox_TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        string text = args.TokenText.Trim();
        if (text.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        args.Item = MarqueeToken.AllFields().FirstOrDefault(t => t.Label.Equals(text, StringComparison.CurrentCultureIgnoreCase))
            ?? MarqueeToken.FromValue(text);
    }

    private void ResetMessage_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.MarqueeTokens = MarqueeTemplate.Default;
        _messageTokens.CollectionChanged -= MessageTokens_CollectionChanged;
        LoadMessage();
    }

    private void MarqueeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.MarqueeEnabled = MarqueeToggle.IsOn;
        }
    }

    private void AirlineColorsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.MarqueeAirlineColors = AirlineColorsToggle.IsOn;
        }
    }

    private void RadiusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RadiusBox.SelectedIndex >= 0)
        {
            SettingsService.OverheadRadiusKm = SettingsService.RadiusChoicesKm[RadiusBox.SelectedIndex];
        }
    }

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SpeedBox.SelectedIndex >= 0)
        {
            SettingsService.MarqueeSpeed = (MarqueeSpeed)SpeedBox.SelectedIndex;
        }
    }

    private void UnitsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && UnitsBox.SelectedIndex >= 0)
        {
            SettingsService.Units = (UnitSystem)UnitsBox.SelectedIndex;
            _loading = true;
            FillRadiusChoices();
            UpdateMessagePreview();
            _loading = false;
        }
    }

    // ---- Favorite airports ----

    private void AirportSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestions.Clear();
        _suggestions.AddRange(AirportService.Catalog.Search(sender.Text, max: 8));
        sender.ItemsSource = _suggestions.Select(a => $"{a.Codes}  {a.Name} ({a.Place})").ToList();
    }

    private void AirportSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        int index = sender.Items.IndexOf(args.SelectedItem);
        if (index >= 0 && index < _suggestions.Count)
        {
            AddFavorite(_suggestions[index]);
        }
    }

    private void AirportSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter without picking a suggestion takes the best match.
        if (args.ChosenSuggestion is null && AirportService.Catalog.Search(args.QueryText, max: 1) is [{ } best])
        {
            AddFavorite(best);
        }
    }

    private void AddFavorite(Airport airport)
    {
        SettingsService.FavoriteAirports = (string[])[.. SettingsService.FavoriteAirports, airport.Icao];
        AirportSearch.Text = "";
        AirportSearch.ItemsSource = null;
    }

    private void UpdateFavorites()
    {
        FavoritesPanel.Children.Clear();
        foreach (Airport airport in AirportService.Favorites)
        {
            Grid row = new() { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"{airport.Codes}  {airport.Name}", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = airport.Place,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            row.Children.Add(text);

            Button remove = new()
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 }, // Delete
                Style = (Style)Application.Current.Resources["FlyoutIconButtonStyle"],
            };
            string name = $"Remove {airport.Icao}";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, name);
            ToolTipService.SetToolTip(remove, name);
            remove.Click += (_, _) => SettingsService.FavoriteAirports =
                SettingsService.FavoriteAirports.Where(code => !code.Equals(airport.Icao, StringComparison.OrdinalIgnoreCase)).ToList();
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            FavoritesPanel.Children.Add(row);
        }

        if (FavoritesPanel.Children.Count == 0)
        {
            FavoritesPanel.Children.Add(new TextBlock
            {
                Text = "No favorites yet.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
    }

    // ---- OpenSky account ----

    private void OnAccountChanged(object? sender, EventArgs e) => UpdateAccount();

    private void UpdateAccount()
    {
        OpenSkyCredentials? credentials = OpenSkyService.Client.Credentials;
        AccountTitle.Text = credentials is null ? "Not signed in" : "Signed in with your API client";
        AccountDescription.Text = credentials is null
            ? "Without an account OpenSky allows 400 requests a day. A free API client raises that to 4,000 and updates the map twice as often."
            : "OpenSky allows 4,000 requests a day. The secret is stored in Windows Credential Manager.";
        ClientIdBox.Text = credentials?.ClientId ?? "";
        ClientSecretBox.Password = "";
        ClientSecretBox.PlaceholderText = credentials is null ? "" : "Saved";
        SignOutButton.Visibility = credentials is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        string id = ClientIdBox.Text.Trim();
        string secret = ClientSecretBox.Password.Trim();
        if (id.Length == 0 || secret.Length == 0)
        {
            SignInError.Text = "Enter both the client ID and the client secret.";
            return;
        }

        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        SignInError.Text = "";
        SignInButton.IsEnabled = false;
        SignInRing.IsActive = true;
        try
        {
            await OpenSkyService.SignInAsync(new OpenSkyCredentials(id, secret), _signIn.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OpenSkyAuthException ex)
        {
            SignInError.Text = ex.Message;
        }
        catch (Exception)
        {
            SignInError.Text = ConnectivityService.IsInternetAvailable ? "Can't reach OpenSky. Try again shortly." : "No internet connection.";
        }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInRing.IsActive = false;
        }
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        SignInError.Text = "";
        OpenSkyService.SignOut();
    }

    // ---- General ----

    private static string GetVersion()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "(unpackaged)";
        }
    }

    private async Task LoadStartupStateAsync()
    {
        ShowStartupState(await StartupService.GetStateAsync());
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }
}
