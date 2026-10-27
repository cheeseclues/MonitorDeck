# Experimental Night Light on/off interface

- Small global switch is intentionally outside per-monitor list.
- `NightLightService` requires an exact expected CloudStore state envelope and only recognized state fields; otherwise it refuses to write.
- On each request it writes a local backup of the original state value first. No admin rights required; HKCU only.
- A first-use consent prompt explains the undocumented registry method and redirects users to the official Windows Settings page if they decline.
- Refresh is every 10 seconds while tray popup is visible and immediately when opened.
- Window `ms-settings:nightlight` is invoked through shell URI activation.
- Night Light schedule and warmth strength are NOT modified.
- Known risk: successful binary readback does not guarantee Windows commits the visible color-temperature transition.
