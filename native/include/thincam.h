#ifndef THINCAM_H
#define THINCAM_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
  #if defined(THINCAM_BUILDING)
    #define TC_API __declspec(dllexport)
  #else
    #define TC_API __declspec(dllimport)
  #endif
  #define TC_CALL __cdecl
#else
  #define TC_API __attribute__((visibility("default")))
  #define TC_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define TC_ABI_VERSION 2u

typedef struct tc_camera tc_camera;

typedef enum tc_status {
    TC_OK = 0,
    TC_ERROR_INVALID_ARGUMENT = 1,
    TC_ERROR_NOT_SUPPORTED = 2,
    TC_ERROR_PERMISSION_DENIED = 3,
    TC_ERROR_DEVICE_NOT_FOUND = 4,
    TC_ERROR_DEVICE_BUSY = 5,
    TC_ERROR_FORMAT_NOT_SUPPORTED = 6,
    TC_ERROR_NOT_RUNNING = 7,
    TC_ERROR_ALREADY_RUNNING = 8,
    TC_ERROR_PLATFORM = 9,
    TC_ERROR_TIMEOUT = 10,
    TC_ERROR_CANCELLED = 11
} tc_status;

typedef enum tc_permission_status {
    TC_PERMISSION_UNKNOWN = 0,
    TC_PERMISSION_NOT_DETERMINED = 1,
    TC_PERMISSION_GRANTED = 2,
    TC_PERMISSION_DENIED = 3,
    TC_PERMISSION_RESTRICTED = 4,
    TC_PERMISSION_HOST_ACTION_REQUIRED = 5
} tc_permission_status;

typedef enum tc_camera_position {
    TC_POSITION_UNSPECIFIED = 0,
    TC_POSITION_FRONT = 1,
    TC_POSITION_BACK = 2,
    TC_POSITION_EXTERNAL = 3
} tc_camera_position;

typedef enum tc_pixel_format {
    TC_PIXEL_BGRA32 = 1
} tc_pixel_format;

typedef enum tc_control_id {
    TC_CONTROL_EXPOSURE_MODE = 1,
    TC_CONTROL_EXPOSURE_COMPENSATION_EV = 2,
    TC_CONTROL_EXPOSURE_DURATION_US = 3,
    TC_CONTROL_EXPOSURE_ISO = 4,

    TC_CONTROL_FOCUS_MODE = 10,
    TC_CONTROL_FOCUS_POSITION = 11,

    TC_CONTROL_ZOOM_FACTOR = 20,

    TC_CONTROL_LIGHT_ENABLED = 30,
    TC_CONTROL_LIGHT_LEVEL = 31
} tc_control_id;

typedef enum tc_control_value_type {
    TC_CONTROL_VALUE_BOOL = 1,
    TC_CONTROL_VALUE_INT64 = 2,
    TC_CONTROL_VALUE_DOUBLE = 3,
    TC_CONTROL_VALUE_ENUM = 4
} tc_control_value_type;

typedef enum tc_control_flags {
    TC_CONTROL_FLAG_NONE = 0,
    TC_CONTROL_FLAG_READABLE = 1u << 0,
    TC_CONTROL_FLAG_WRITABLE = 1u << 1
} tc_control_flags;

typedef enum tc_exposure_mode {
    TC_EXPOSURE_MODE_AUTO = 1,
    TC_EXPOSURE_MODE_MANUAL = 2,
    TC_EXPOSURE_MODE_LOCKED = 3
} tc_exposure_mode;

typedef enum tc_focus_mode {
    TC_FOCUS_MODE_AUTO = 1,
    TC_FOCUS_MODE_CONTINUOUS_AUTO = 2,
    TC_FOCUS_MODE_MANUAL = 3,
    TC_FOCUS_MODE_LOCKED = 4
} tc_focus_mode;

typedef union tc_control_data {
    int32_t boolean_value;
    int32_t enum_value;
    int64_t integer_value;
    double double_value;
} tc_control_data;

typedef struct tc_device_info {
    uint32_t struct_size;
    const char* id;
    const char* name;
    tc_camera_position position;
    int32_t is_default;
} tc_device_info;

typedef struct tc_open_options {
    uint32_t struct_size;
    int32_t width;
    int32_t height;
    int32_t frames_per_second;
    tc_pixel_format pixel_format;
} tc_open_options;

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

typedef struct tc_control_info {
    uint32_t struct_size;
    tc_control_id id;
    tc_control_value_type value_type;
    uint32_t flags;
    double minimum;
    double maximum;
    double step;
    double default_value;
    uint64_t supported_enum_values;
} tc_control_info;

typedef struct tc_control_value {
    uint32_t struct_size;
    tc_control_id id;
    tc_control_value_type value_type;
    uint32_t reserved;
    tc_control_data value;
} tc_control_value;

typedef void (TC_CALL *tc_device_callback)(const tc_device_info* device, void* user_data);
typedef void (TC_CALL *tc_frame_callback)(const tc_frame* frame, void* user_data);
typedef void (TC_CALL *tc_error_callback)(tc_status status, const char* message, int32_t fatal, void* user_data);
typedef void (TC_CALL *tc_permission_callback)(tc_permission_status status, void* user_data);

TC_API uint32_t TC_CALL tc_get_abi_version(void);
TC_API const char* TC_CALL tc_status_message(tc_status status);

TC_API tc_status TC_CALL tc_get_permission_status(tc_permission_status* status);
TC_API tc_status TC_CALL tc_request_permission(tc_permission_callback callback, void* user_data);

/* Device callbacks are invoked synchronously before this function returns. */
TC_API tc_status TC_CALL tc_enumerate_devices(tc_device_callback callback, void* user_data);

TC_API tc_status TC_CALL tc_camera_open(
    const char* device_id,
    const tc_open_options* options,
    tc_frame_callback frame_callback,
    tc_error_callback error_callback,
    void* user_data,
    tc_camera** camera);

/* Start returns only after the capture pipeline has started or definitively failed. */
TC_API tc_status TC_CALL tc_camera_start(tc_camera* camera);

/* Stop is synchronous. No callbacks occur after it returns. */
TC_API tc_status TC_CALL tc_camera_stop(tc_camera* camera);
TC_API void TC_CALL tc_camera_close(tc_camera* camera);

/* Camera controls are capability driven. Unsupported controls return TC_ERROR_NOT_SUPPORTED. */
TC_API tc_status TC_CALL tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info);

TC_API tc_status TC_CALL tc_camera_get_control(
    tc_camera* camera,
    tc_control_id id,
    tc_control_value* value);

TC_API tc_status TC_CALL tc_camera_set_control(
    tc_camera* camera,
    const tc_control_value* value);

#ifdef __cplusplus
}
#endif

#endif
