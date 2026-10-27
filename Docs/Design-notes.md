# MonitorDeck v1: behavior contracts

## Discovery is independent of hardware control

Display enumeration has no DDC probe as a prerequisite. Each active monitor gets a UI card before its brightness and contrast capabilities have been checked.

## Detection

- Windows messages: `WM_DISPLAYCHANGE`, `WM_DEVICECHANGE`, resume from suspend.
- Event-triggered rescan is debounced 1200 ms to handle multiple device events.
- Fallback Windows desktop topology fingerprint every 6 seconds.
- The fallback fingerprint has no access to independent physical on/off states if Windows doesn't emit a topology change.

## Commands

- MCCS standard VCP feature 0x10 = brightness, 0x12 = contrast.
- An invalid maximum (0) disables the slider.
- Readbacks are made after writes; failures are visible, never silently treated as success.
- Hardware APIs may require corrections for odd monitors. They do not grant access to the physical display's custom proprietary protocol.

## Useful next improvements

- Resolve generic Windows monitor names using EDID, not only EnumDisplayDevices.
- Persistent monitor nicknames/configuration.
- Software dimming only as an explicit fallback for hardware-inaccessible monitors, avoiding global GPU gamma changes.
- Per-monitor diagnostics export log.
- Real Windows-native Visual Studio CI build and test.
- Further hotplug wake/sleep behavior on the AMD GPU.
