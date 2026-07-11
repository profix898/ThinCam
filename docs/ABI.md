# ThinCam native ABI

The managed package and every platform backend communicate through the versioned C interface in `native/include/thincam.h`.

For the full architecture and platform implementations, see [ARCHITECTURE.md](ARCHITECTURE.md). For native compilation and artifact staging, see [BUILDING.md](BUILDING.md).

## ABI goals

The ABI is intentionally small and C-compatible so it can be implemented by C++, Objective-C++, or another native language without exposing compiler-specific C++ classes to .NET.

The contract covers only:

- ABI and status inspection.
- Permission status and request completion.
- Synchronous device enumeration.
- Camera handle creation.
- Capture start and stop.
- Frame and error callbacks.
- Capability-driven camera controls.
- Handle destruction.

## Version

```c
#define TC_ABI_VERSION 2u
```

Managed code calls `tc_get_abi_version` before exchanging structures or callbacks. A mismatched version is rejected with a managed `CameraException`.

Increment the version for binary-incompatible changes. Do not renumber existing enum members because managed and native values are mapped numerically.

## Calling convention and visibility

All functions and callbacks use the C calling convention declared by `TC_CALL`.

- Windows exports functions with `__declspec(dllexport)` and uses `__cdecl`.
- Other platforms use default C calling convention and explicit symbol visibility.
- iOS links the implementation statically; managed calls resolve through `__Internal`.

All exported functions must catch native exceptions. No C++ or Objective-C exception may unwind across this ABI.

## Exported symbols

Every backend must expose these symbols:

```c
uint32_t tc_get_abi_version(void);
const char* tc_status_message(tc_status status);

tc_status tc_get_permission_status(tc_permission_status* status);
tc_status tc_request_permission(
    tc_permission_callback callback,
    void* user_data);

tc_status tc_enumerate_devices(
    tc_device_callback callback,
    void* user_data);

tc_status tc_camera_open(
    const char* device_id,
    const tc_open_options* options,
    tc_frame_callback frame_callback,
    tc_error_callback error_callback,
    void* user_data,
    tc_camera** camera);

tc_status tc_camera_start(tc_camera* camera);
tc_status tc_camera_stop(tc_camera* camera);
void tc_camera_close(tc_camera* camera);

tc_status tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info);
tc_status tc_camera_get_control(
    tc_camera* camera,
    tc_control_id id,
    tc_control_value* value);
tc_status tc_camera_set_control(
    tc_camera* camera,
    const tc_control_value* value);
```

## Handle lifecycle

The intended lifecycle is:

```text
tc_camera_open
      ↓
tc_camera_start
      ↓
zero or more frame/error callbacks
      ↓
tc_camera_stop, optional because close also stops
      ↓
tc_camera_close
```

`tc_camera_open` allocates a backend-owned opaque handle and stores callback pointers. Depending on the platform, expensive device activation can occur during open or start.

`tc_camera_start` returns only after capture has started or definitively failed.

`tc_camera_stop` is synchronous. No frame or error callback may run after it returns.

`tc_camera_close` stops capture, releases platform resources, and destroys the handle. Callers must not reuse or close the pointer again.

## Callback lifetime rules

### Device callback

Device enumeration callbacks run synchronously before `tc_enumerate_devices` returns.

The `tc_device_info` pointer and its `id` and `name` strings are valid only during the callback. The managed implementation copies them immediately.

### Frame callback

The `tc_frame` pointer and `data` buffer are valid only while the callback is running.

The buffer remains owned by the native backend or operating-system camera API. Managed code validates and copies it before returning.

A backend must not invoke two frame callbacks concurrently for the same camera handle unless the callback contract is changed and the ABI is versioned accordingly. Current implementations serialize callback delivery through their worker/callback mechanisms.

### Error callback

The UTF-8 message is valid only during the callback. `fatal != 0` means capture cannot continue and the managed frame stream should terminate.

### Permission callback

A permission callback can be asynchronous. Its `user_data` must be returned exactly once. Android's native implementation reports `TC_PERMISSION_HOST_ACTION_REQUIRED`; the managed Android layer owns the actual activity-based runtime request.

## Structures

Every structure begins with `struct_size` to support validation and future append-only growth.

### `tc_device_info`

```c
typedef struct tc_device_info {
    uint32_t struct_size;
    const char* id;
    const char* name;
    tc_camera_position position;
    int32_t is_default;
} tc_device_info;
```

- Strings are null-terminated UTF-8.
- `id` is opaque and platform-specific.
- `is_default` is zero or nonzero.

### `tc_open_options`

```c
typedef struct tc_open_options {
    uint32_t struct_size;
    int32_t width;
    int32_t height;
    int32_t frames_per_second;
    tc_pixel_format pixel_format;
} tc_open_options;
```

Dimensions and frame rate are preferences. A backend may choose the nearest supported configuration.

Version 2 accepts only `TC_PIXEL_BGRA32`.

### `tc_frame`

```c
typedef struct tc_frame {
    uint32_t struct_size;
    const uint8_t* data;
    size_t data_length;
    int32_t width;
    int32_t height;
    int32_t stride;
    tc_pixel_format pixel_format;
    int32_t rotation_degrees;
    int32_t mirrored;
    int64_t timestamp_microseconds;
} tc_frame;
```

Version 2 output requirements:

- Top-to-bottom BGRA32.
- Positive width, height, and stride.
- `stride >= width * 4`.
- `data_length` covers all visible rows and any row padding.
- Alpha is opaque.
- Rotation and mirroring are metadata; pixels are not transformed.

Timestamps are for ordering and elapsed-time calculations within a capture session. Their epoch is platform-specific.

## Status values

`tc_status` values map numerically to managed `CameraErrorCode` values.

```text
0  success
1  invalid argument
2  not supported
3  permission denied
4  device not found
5  device busy
6  format not supported
7  not running
8  already running
9  platform error
10 timeout
11 cancelled
```

`tc_status_message` returns a static null-terminated string for a status value.

## Permission values

`tc_permission_status` maps numerically to managed `CameraPermissionStatus`:

```text
0 unknown
1 not determined
2 granted
3 denied
4 restricted
5 host action required
```

The meaning of request differs by platform because Windows and Linux do not provide a ThinCam-owned prompt, Apple uses AVFoundation authorization, and Android requires an activity in managed host code.

## Binary layout requirements

Managed interop structures use sequential layout and matching field types. Take care with:

- `size_t`, which maps to managed `nuint`.
- C enum width, currently expected to be 32 bits on supported compilers.
- `int32_t` fields, which map to managed `int`.
- `int64_t` timestamps, which map to managed `long`.
- Pointer fields, which require unsafe managed code.

Do not use compiler packing pragmas for ABI structures unless managed layout is changed at the same time and the ABI version is incremented.

## Compatibility rules for changes

Usually compatible when carefully implemented:

- Adding a new enum value without renumbering existing values.
- Adding a new exported function.
- Appending fields to a structure while honoring `struct_size`.

Usually incompatible:

- Reordering or removing fields.
- Changing field widths or signedness.
- Changing calling convention.
- Changing callback synchronization or lifetime guarantees.
- Reinterpreting an existing enum value.
- Changing the output layout of an existing pixel format.

Any ABI change should update:

1. `native/include/thincam.h`.
2. Managed enums and structures in `src/ThinCam/Interop/NativeTypes.cs`.
3. P/Invoke declarations in `NativeMethods.cs`.
4. Every native backend.
5. ABI smoke tests and symbol checks.
6. This document and `ARCHITECTURE.md`.
