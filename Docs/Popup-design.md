# Compact tray popup design

- `QuickPanel.xaml` / `.cs`: borderless WPF popup, menu header/commands, click-outside dismissal and row management.
- `QuickMonitorRow.xaml` / `.cs`: shows hardware brightness and contrast only when corresponding DDC/CI VCP probes returned valid values, otherwise shows software brightness. A monitor with hardware contrast but no hardware brightness has a hybrid row.
- `MainWindow.xaml.cs`: owner of the shared monitor enumeration and DDC/CI/dimming services. Opens quick popup from tray. Applies debounced writes and synchronizes readings with both surfaces.
- Physical Win32 `GetWindowRect` / `SetWindowPos` position the popup near the lower-right work-area edge on the display where the tray icon was clicked; the placement monitor remains stable if the pointer moves while the panel is open.

Potential next steps: per-monitor aliases, distinct popover icon, rounded system tray icon, profiles, keyboard shortcuts, settings persistence, refresh throttling and hardware contrast warning/disable for Neo G8.
