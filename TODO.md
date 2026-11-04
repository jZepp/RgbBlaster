# TODO

## Service and web GUI, next phases
- Sound-reactive effects: WASAPI loopback capture, level and spectrum bands feeding effects. Check that loopback capture works from a session 0 service.
- Screen-reactive effects: a service in session 0 cannot capture the desktop. Needs a small per-user agent that captures the screen and sends colors to the service.
- LCD designer: build screens in the GUI (text, sensor gauges, images, backgrounds) instead of only uploading images.
- LCD frame rate: GIFs are shown with a 50 ms minimum frame time. Test higher rates on the device.
- Install script: publish, register the Windows service, set recovery options. Stop the CLI `run` before the service starts; both open the same devices.
- Optional LAN access with authentication (Web.AllowLan exists but has no login).
