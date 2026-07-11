# ThinCam Avalonia demo

The demo is a shared Avalonia UI consumed by three thin platform heads:

- `ThinCam.Demo.Desktop` — Windows, Linux, and macOS
- `ThinCam.Demo.Android`
- `ThinCam.Demo.iOS`

The UI uses the reusable `ThinCam.Avalonia.CameraPreview` control and a
`CameraPreviewSource` owned by the view model. It exercises permission handling,
device enumeration, frame preview, snapshots, exposure, focus, zoom, Light,
format negotiation, frame statistics, and diagnostic export.

See [`../../docs/AVALONIA.md`](../../docs/AVALONIA.md) for build and usage details.
