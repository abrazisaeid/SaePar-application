# Preview 7 — MAUI Resource Startup Fix

## Symptom
At runtime, DashboardPage failed in `InitializeComponent()` with:

`Microsoft.Maui.Controls.Xaml.XamlParseException: StaticResource not found for key TitleLabel`

## Root cause
`TitleLabel` already existed in `Resources/Styles/Styles.xaml`.

The actual problem was startup ordering:

1. MAUI DI attempted to construct `App`.
2. `App` constructor required an `AppShell` instance.
3. To create `AppShell`, DI first created `DashboardPage`, `ConfigsPage`, etc.
4. Those pages called `InitializeComponent()` and looked up application-level `StaticResource` keys.
5. But `App.InitializeComponent()` had not run yet, so `Application.Resources` had not loaded the merged dictionaries.

## Fix
`App` now takes `IServiceProvider`, calls `InitializeComponent()` first, and only resolves `AppShell` inside `CreateWindow()`.

This guarantees global colors/styles exist before any page XAML is constructed.
