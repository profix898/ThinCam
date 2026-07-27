# ThinCam Avalonia demo

The demo is a shared Avalonia UI consumed by three thin platform heads:

- `ThinCamDemo.Desktop` — Windows, Linux, and macOS
- `ThinCamDemo.Android`
- `ThinCamDemo.iOS`

The UI uses the reusable `ThinCam.Avalonia.CameraPreview` control and a
`CameraPreviewSource` owned by the view model. It exercises permission handling,
device enumeration, frame preview, snapshots, exposure, focus, zoom, Light,
format negotiation, frame statistics, and diagnostic export.

See [`../../Docs/AVALONIA.md`](../../Docs/AVALONIA.md) for build and usage details.
