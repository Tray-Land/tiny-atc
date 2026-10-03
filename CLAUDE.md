@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `FlyoutPage.OnHidden` / `Dispose`.
- Network work runs only while visible, through `Services/ForegroundPoller`. The one exception is background work the tray icon itself displays, and that needs a documented reason.
- The only build-time key is the optional `AzureMapsKey` in `OAuth.resw` (gitignored; read through `Services/Secrets`; without it the map falls back to OSM tiles). The user's own OpenSky API client (ID + secret) lives in PasswordVault via `Services/OpenSkyAccount`. Nothing secret goes in `SettingsService`.
- UI-free logic (OpenSky client and parser, geo math, airport traffic classification, marquee frames) lives in `TinyAtc.Core` and is tested by `TinyAtc.Tests` (`dotnet test TinyAtc.Tests`).
- The tray marquee (`Services/TrayMarquee`) is the one background mode: opt-in, polls on `PollPolicy.Marquee`, and reuses the map's data when the flyout is open.
- The repo is MIT licensed (`LICENSE`). When the app's features are working, write a descriptive `README.md` (what it does, screenshot, install, setup/keys, privacy, build from source, license); the `winui-tray-app` skill lists the sections.
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so a plain `net10.0` test project can cover it.
