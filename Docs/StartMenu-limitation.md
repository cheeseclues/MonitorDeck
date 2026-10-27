# Start menu dimming: current limitation

Normal WinForms/WPF topmost overlays do not reliably cover every Windows 11
shell surface. On this machine Start remained bright even after a 250 ms
`HWND_TOPMOST` z-order refresh. We removed that timer rather than fight the
shell in an endless loop. Capture-excluded overlays remain the default.

Options to investigate separately:
- Hardware DDC/CI brightness where connection passes DDC commands.
- Per-output GPU output dimming (gamma/output control), with caveats: color
  fidelity, HDR, Windows Night Light compatibility, screenshot/OBS behavior.
- USB-C adapter with native DisplayPort support where user is willing to
  change a cable; not required for this v1.0.16 release.

Do not promise that a transparent overlay can cover secured/system surfaces.
