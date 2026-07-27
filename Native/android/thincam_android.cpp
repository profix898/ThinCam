#include <camera/NdkCameraDevice.h>
#include <camera/NdkCameraManager.h>
#include <camera/NdkCameraMetadata.h>
#include <camera/NdkCameraMetadataTags.h>
#include <media/NdkImage.h>
#include <media/NdkImageReader.h>

#include "thincam.h"
#include "thincam_common.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <climits>
#include <cstdint>
#include <mutex>
#include <new>
#include <string>
#include <vector>

struct tc_camera {
    std::string device_id;
    tc_open_options options{};
    tc_frame_callback frame_callback = nullptr;
    tc_error_callback error_callback = nullptr;
    void* user_data = nullptr;

    ACameraManager* manager = nullptr;
    ACameraDevice* device = nullptr;
    AImageReader* image_reader = nullptr;
    ANativeWindow* window = nullptr;
    ACameraOutputTarget* output_target = nullptr;
    ACaptureRequest* request = nullptr;
    ACaptureSessionOutput* session_output = nullptr;
    ACaptureSessionOutputContainer* output_container = nullptr;
    ACameraCaptureSession* session = nullptr;

    std::atomic<bool> running{false};
    std::atomic<bool> stopping{false};
    std::atomic<int> image_callbacks{0};
    std::mutex lifecycle_mutex;
    std::mutex callback_mutex;
    std::condition_variable callback_condition;
    std::mutex frame_mutex;
    bool session_ready = false;
    bool session_closed = false;

    int width = 0;
    int height = 0;
    int sensor_orientation = 0;
    int mirrored = 0;
    std::vector<uint8_t> bgra;

    uint64_t exposure_modes = 0;
    uint8_t automatic_exposure_mode = ACAMERA_CONTROL_AE_MODE_OFF;
    bool exposure_lock_supported = false;
    int32_t exposure_mode = TC_EXPOSURE_MODE_AUTO;
    bool exposure_compensation_supported = false;
    int32_t exposure_compensation_min = 0;
    int32_t exposure_compensation_max = 0;
    int32_t exposure_compensation_current = 0;
    double exposure_compensation_step_ev = 0.0;
    bool manual_exposure_supported = false;
    int64_t exposure_duration_min_ns = 0;
    int64_t exposure_duration_max_ns = 0;
    int64_t exposure_duration_current_ns = 0;
    int32_t iso_min = 0;
    int32_t iso_max = 0;
    int32_t iso_current = 0;

    uint64_t focus_modes = 0;
    int32_t focus_mode = TC_FOCUS_MODE_LOCKED;
    uint8_t automatic_focus_mode = ACAMERA_CONTROL_AF_MODE_OFF;
    uint8_t continuous_focus_mode = ACAMERA_CONTROL_AF_MODE_OFF;
    float minimum_focus_distance = 0.0f;
    double focus_position = 1.0;

    float maximum_zoom_factor = 1.0f;
    double zoom_factor = 1.0;
    std::array<int32_t, 4> active_array{0, 0, 0, 0};

    bool light_available = false;
    bool light_enabled = false;
};

namespace {

tc_status camera_status(camera_status_t status) {
    switch (status) {
        case ACAMERA_OK: return TC_OK;
        case ACAMERA_ERROR_INVALID_PARAMETER: return TC_ERROR_INVALID_ARGUMENT;
        case ACAMERA_ERROR_CAMERA_IN_USE:
        case ACAMERA_ERROR_MAX_CAMERA_IN_USE: return TC_ERROR_DEVICE_BUSY;
        case ACAMERA_ERROR_PERMISSION_DENIED: return TC_ERROR_PERMISSION_DENIED;
        case ACAMERA_ERROR_CAMERA_DISABLED: return TC_ERROR_PERMISSION_DENIED;
        case ACAMERA_ERROR_NOT_ENOUGH_MEMORY:
        case ACAMERA_ERROR_CAMERA_DEVICE:
        case ACAMERA_ERROR_CAMERA_SERVICE:
        case ACAMERA_ERROR_UNKNOWN:
        default: return TC_ERROR_PLATFORM;
    }
}

// Android reports administratively disabled cameras as permission denied.
tc_status normalized_camera_status(camera_status_t status) {
    if (status == ACAMERA_ERROR_CAMERA_DISABLED) return TC_ERROR_PERMISSION_DENIED;
    return camera_status(status);
}

tc_camera_position lens_position(uint8_t facing) {
    switch (facing) {
        case ACAMERA_LENS_FACING_FRONT: return TC_POSITION_FRONT;
        case ACAMERA_LENS_FACING_BACK: return TC_POSITION_BACK;
        case ACAMERA_LENS_FACING_EXTERNAL: return TC_POSITION_EXTERNAL;
        default: return TC_POSITION_UNSPECIFIED;
    }
}

bool metadata_byte(ACameraMetadata* metadata, uint32_t tag, uint8_t* value) {
    if (!metadata || !value) return false;
    ACameraMetadata_const_entry entry{};
    if (ACameraMetadata_getConstEntry(metadata, tag, &entry) != ACAMERA_OK || entry.count < 1) {
        return false;
    }
    *value = entry.data.u8[0];
    return true;
}

bool metadata_int32(ACameraMetadata* metadata, uint32_t tag, int32_t* value) {
    if (!metadata || !value) return false;
    ACameraMetadata_const_entry entry{};
    if (ACameraMetadata_getConstEntry(metadata, tag, &entry) != ACAMERA_OK || entry.count < 1) {
        return false;
    }
    *value = entry.data.i32[0];
    return true;
}


bool metadata_float(ACameraMetadata* metadata, uint32_t tag, float* value) {
    if (!metadata || !value) return false;
    ACameraMetadata_const_entry entry{};
    if (ACameraMetadata_getConstEntry(metadata, tag, &entry) != ACAMERA_OK || entry.count < 1) {
        return false;
    }
    *value = entry.data.f[0];
    return true;
}

bool metadata_contains_byte(ACameraMetadata* metadata, uint32_t tag, uint8_t requested) {
    if (!metadata) return false;
    ACameraMetadata_const_entry entry{};
    if (ACameraMetadata_getConstEntry(metadata, tag, &entry) != ACAMERA_OK) return false;
    for (uint32_t index = 0; index < entry.count; ++index) {
        if (entry.data.u8[index] == requested) return true;
    }
    return false;
}

bool load_control_characteristics(tc_camera* camera) {
    if (!camera || !camera->manager) return false;
    ACameraMetadata* metadata = nullptr;
    if (ACameraManager_getCameraCharacteristics(
            camera->manager, camera->device_id.c_str(), &metadata) != ACAMERA_OK || !metadata) {
        return false;
    }

    ACameraMetadata_const_entry entry{};
    uint8_t exposure_lock_available = 0;
    camera->exposure_lock_supported =
        metadata_byte(metadata, ACAMERA_CONTROL_AE_LOCK_AVAILABLE, &exposure_lock_available) &&
        exposure_lock_available != 0;

    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_CONTROL_AE_AVAILABLE_MODES, &entry) == ACAMERA_OK) {
        for (uint32_t index = 0; index < entry.count; ++index) {
            const uint8_t mode = entry.data.u8[index];
            if (mode == ACAMERA_CONTROL_AE_MODE_ON ||
                mode == ACAMERA_CONTROL_AE_MODE_ON_AUTO_FLASH ||
                mode == ACAMERA_CONTROL_AE_MODE_ON_ALWAYS_FLASH ||
                mode == ACAMERA_CONTROL_AE_MODE_ON_AUTO_FLASH_REDEYE) {
                camera->exposure_modes |= thincam::enum_flag(TC_EXPOSURE_MODE_AUTO);
                if (camera->automatic_exposure_mode == ACAMERA_CONTROL_AE_MODE_OFF ||
                    mode == ACAMERA_CONTROL_AE_MODE_ON) {
                    camera->automatic_exposure_mode = mode;
                }
                if (camera->exposure_lock_supported) {
                    camera->exposure_modes |= thincam::enum_flag(TC_EXPOSURE_MODE_LOCKED);
                }
            }
            if (mode == ACAMERA_CONTROL_AE_MODE_OFF) {
                camera->exposure_modes |= thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL);
            }
        }
    }

    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_CONTROL_AE_COMPENSATION_RANGE, &entry) == ACAMERA_OK &&
        entry.count >= 2) {
        camera->exposure_compensation_min = entry.data.i32[0];
        camera->exposure_compensation_max = entry.data.i32[1];
        camera->exposure_compensation_supported =
            camera->exposure_compensation_max > camera->exposure_compensation_min;
    }
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_CONTROL_AE_COMPENSATION_STEP, &entry) == ACAMERA_OK &&
        entry.count >= 1 && entry.data.r[0].denominator != 0) {
        camera->exposure_compensation_step_ev =
            static_cast<double>(entry.data.r[0].numerator) /
            static_cast<double>(entry.data.r[0].denominator);
    }
    if (camera->exposure_compensation_step_ev <= 0.0) {
        camera->exposure_compensation_supported = false;
    }

    const bool manual_capability = metadata_contains_byte(
        metadata, ACAMERA_REQUEST_AVAILABLE_CAPABILITIES,
        ACAMERA_REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR);
    if (manual_capability &&
        ACameraMetadata_getConstEntry(metadata, ACAMERA_SENSOR_INFO_EXPOSURE_TIME_RANGE, &entry) == ACAMERA_OK &&
        entry.count >= 2) {
        camera->exposure_duration_min_ns = entry.data.i64[0];
        camera->exposure_duration_max_ns = entry.data.i64[1];
    }
    if (manual_capability &&
        ACameraMetadata_getConstEntry(metadata, ACAMERA_SENSOR_INFO_SENSITIVITY_RANGE, &entry) == ACAMERA_OK &&
        entry.count >= 2) {
        camera->iso_min = entry.data.i32[0];
        camera->iso_max = entry.data.i32[1];
    }
    camera->manual_exposure_supported =
        camera->exposure_duration_min_ns > 0 &&
        camera->exposure_duration_max_ns >= camera->exposure_duration_min_ns &&
        camera->iso_min > 0 && camera->iso_max >= camera->iso_min;
    camera->exposure_duration_current_ns = camera->exposure_duration_min_ns;
    camera->iso_current = camera->iso_min;

    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_CONTROL_AF_AVAILABLE_MODES, &entry) == ACAMERA_OK) {
        for (uint32_t index = 0; index < entry.count; ++index) {
            switch (entry.data.u8[index]) {
                case ACAMERA_CONTROL_AF_MODE_AUTO:
                case ACAMERA_CONTROL_AF_MODE_MACRO:
                    camera->focus_modes |= thincam::enum_flag(TC_FOCUS_MODE_AUTO);
                    if (camera->automatic_focus_mode == ACAMERA_CONTROL_AF_MODE_OFF ||
                        entry.data.u8[index] == ACAMERA_CONTROL_AF_MODE_AUTO) {
                        camera->automatic_focus_mode = entry.data.u8[index];
                    }
                    break;
                case ACAMERA_CONTROL_AF_MODE_CONTINUOUS_PICTURE:
                case ACAMERA_CONTROL_AF_MODE_CONTINUOUS_VIDEO:
                    camera->focus_modes |= thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO);
                    if (camera->continuous_focus_mode == ACAMERA_CONTROL_AF_MODE_OFF ||
                        entry.data.u8[index] == ACAMERA_CONTROL_AF_MODE_CONTINUOUS_VIDEO) {
                        camera->continuous_focus_mode = entry.data.u8[index];
                    }
                    break;
                case ACAMERA_CONTROL_AF_MODE_OFF:
                    // Manual focus support is established below only when the lens reports
                    // a usable focus-distance range. Camera2 NDK does not provide a portable
                    // way to snapshot the current lens distance for a true focus lock here.
                    break;
                default:
                    break;
            }
        }
    }
    metadata_float(metadata, ACAMERA_LENS_INFO_MINIMUM_FOCUS_DISTANCE, &camera->minimum_focus_distance);
    if (camera->minimum_focus_distance > 0.0f &&
        metadata_contains_byte(metadata, ACAMERA_CONTROL_AF_AVAILABLE_MODES, ACAMERA_CONTROL_AF_MODE_OFF)) {
        camera->focus_modes |= thincam::enum_flag(TC_FOCUS_MODE_MANUAL);
    }
    if (camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO)) {
        camera->focus_mode = TC_FOCUS_MODE_CONTINUOUS_AUTO;
    } else if (camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_AUTO)) {
        camera->focus_mode = TC_FOCUS_MODE_AUTO;
    } else if (camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_MANUAL)) {
        camera->focus_mode = TC_FOCUS_MODE_MANUAL;
    }

    metadata_float(metadata, ACAMERA_SCALER_AVAILABLE_MAX_DIGITAL_ZOOM, &camera->maximum_zoom_factor);
    if (camera->maximum_zoom_factor < 1.0f) camera->maximum_zoom_factor = 1.0f;
    if (ACameraMetadata_getConstEntry(metadata, ACAMERA_SENSOR_INFO_ACTIVE_ARRAY_SIZE, &entry) == ACAMERA_OK &&
        entry.count >= 4) {
        for (size_t index = 0; index < 4; ++index) camera->active_array[index] = entry.data.i32[index];
    }

    uint8_t flash = 0;
    camera->light_available = metadata_byte(metadata, ACAMERA_FLASH_INFO_AVAILABLE, &flash) && flash != 0;

    ACameraMetadata_free(metadata);
    return true;
}

bool select_output_size(
    ACameraManager* manager,
    const std::string& id,
    int requested_width,
    int requested_height,
    int* selected_width,
    int* selected_height,
    int* sensor_orientation,
    int* mirrored) {

    ACameraMetadata* metadata = nullptr;
    if (ACameraManager_getCameraCharacteristics(manager, id.c_str(), &metadata) != ACAMERA_OK || !metadata) {
        return false;
    }

    uint8_t facing = ACAMERA_LENS_FACING_EXTERNAL;
    metadata_byte(metadata, ACAMERA_LENS_FACING, &facing);
    *mirrored = facing == ACAMERA_LENS_FACING_FRONT ? 1 : 0;

    int32_t orientation = 0;
    if (metadata_int32(metadata, ACAMERA_SENSOR_ORIENTATION, &orientation)) {
        *sensor_orientation = orientation;
    }

    ACameraMetadata_const_entry configurations{};
    const camera_status_t status = ACameraMetadata_getConstEntry(
        metadata,
        ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS,
        &configurations);

    bool found = false;
    int best_width = 0;
    int best_height = 0;
    int64_t best_score = INT64_MAX;

    if (status == ACAMERA_OK) {
        for (uint32_t index = 0; index + 3 < configurations.count; index += 4) {
            const int32_t format = configurations.data.i32[index];
            const int32_t width = configurations.data.i32[index + 1];
            const int32_t height = configurations.data.i32[index + 2];
            const int32_t input = configurations.data.i32[index + 3];

            if (input != 0 || format != AIMAGE_FORMAT_YUV_420_888 || width <= 0 || height <= 0) {
                continue;
            }

            const int64_t score =
                std::llabs(static_cast<int64_t>(width) - requested_width) +
                std::llabs(static_cast<int64_t>(height) - requested_height);

            if (score < best_score) {
                best_score = score;
                best_width = width;
                best_height = height;
                found = true;
            }
        }
    }

    ACameraMetadata_free(metadata);

    if (found) {
        *selected_width = best_width;
        *selected_height = best_height;
    }
    return found;
}

void report_camera_error(tc_camera* camera, tc_status status, const char* message, bool fatal) noexcept {
    if (camera && camera->error_callback) {
        camera->error_callback(status, message, fatal ? 1 : 0, camera->user_data);
    }
}

void on_device_disconnected(void* context, ACameraDevice*) {
    auto* camera = static_cast<tc_camera*>(context);
    if (!camera || camera->stopping.load(std::memory_order_acquire)) return;
    camera->running.store(false, std::memory_order_release);
    report_camera_error(camera, TC_ERROR_DEVICE_NOT_FOUND, "The Android camera was disconnected.", true);
}

void on_device_error(void* context, ACameraDevice*, int error) {
    auto* camera = static_cast<tc_camera*>(context);
    if (!camera || camera->stopping.load(std::memory_order_acquire)) return;
    camera->running.store(false, std::memory_order_release);
    report_camera_error(
        camera,
        error == ERROR_CAMERA_IN_USE || error == ERROR_MAX_CAMERAS_IN_USE
            ? TC_ERROR_DEVICE_BUSY
            : TC_ERROR_PLATFORM,
        "The Android camera device reported an error.",
        true);
}

void on_session_closed(void* context, ACameraCaptureSession*) {
    auto* camera = static_cast<tc_camera*>(context);
    if (!camera) return;
    {
        std::lock_guard<std::mutex> lock(camera->callback_mutex);
        camera->session_closed = true;
    }
    camera->callback_condition.notify_all();
}

void on_session_ready(void* context, ACameraCaptureSession*) {
    auto* camera = static_cast<tc_camera*>(context);
    if (!camera) return;
    {
        std::lock_guard<std::mutex> lock(camera->callback_mutex);
        camera->session_ready = true;
    }
    camera->callback_condition.notify_all();
}

void on_session_active(void* context, ACameraCaptureSession*) {
    on_session_ready(context, nullptr);
}

void on_image_available(void* context, AImageReader* reader) {
    auto* camera = static_cast<tc_camera*>(context);
    if (!camera) return;

    camera->image_callbacks.fetch_add(1, std::memory_order_acq_rel);
    struct callback_guard {
        tc_camera* camera;
        ~callback_guard() {
            if (camera->image_callbacks.fetch_sub(1, std::memory_order_acq_rel) == 1) {
                camera->callback_condition.notify_all();
            }
        }
    } guard{camera};

    AImage* image = nullptr;
    try {
        if (!camera->running.load(std::memory_order_acquire)) return;
        std::lock_guard<std::mutex> frame_lock(camera->frame_mutex);
        if (!camera->running.load(std::memory_order_acquire)) return;

        if (AImageReader_acquireLatestImage(reader, &image) != AMEDIA_OK || !image) return;

        int32_t width = 0;
        int32_t height = 0;
        int64_t timestamp_ns = 0;
        AImage_getWidth(image, &width);
        AImage_getHeight(image, &height);
        AImage_getTimestamp(image, &timestamp_ns);

        uint8_t* y_data = nullptr;
        uint8_t* u_data = nullptr;
        uint8_t* v_data = nullptr;
        int y_length = 0;
        int u_length = 0;
        int v_length = 0;
        int32_t y_row_stride = 0;
        int32_t u_row_stride = 0;
        int32_t v_row_stride = 0;
        int32_t y_pixel_stride = 0;
        int32_t u_pixel_stride = 0;
        int32_t v_pixel_stride = 0;

        const bool valid =
            width > 0 && height > 0 &&
            AImage_getPlaneData(image, 0, &y_data, &y_length) == AMEDIA_OK &&
            AImage_getPlaneData(image, 1, &u_data, &u_length) == AMEDIA_OK &&
            AImage_getPlaneData(image, 2, &v_data, &v_length) == AMEDIA_OK &&
            AImage_getPlaneRowStride(image, 0, &y_row_stride) == AMEDIA_OK &&
            AImage_getPlaneRowStride(image, 1, &u_row_stride) == AMEDIA_OK &&
            AImage_getPlaneRowStride(image, 2, &v_row_stride) == AMEDIA_OK &&
            AImage_getPlanePixelStride(image, 0, &y_pixel_stride) == AMEDIA_OK &&
            AImage_getPlanePixelStride(image, 1, &u_pixel_stride) == AMEDIA_OK &&
            AImage_getPlanePixelStride(image, 2, &v_pixel_stride) == AMEDIA_OK;

        if (valid) {
            const int output_stride = width * 4;
            camera->bgra.resize(static_cast<size_t>(output_stride) * static_cast<size_t>(height));

            thincam::yuv420_888_to_bgra(
                y_data, y_length, y_row_stride, y_pixel_stride,
                u_data, u_length, u_row_stride, u_pixel_stride,
                v_data, v_length, v_row_stride, v_pixel_stride,
                width, height,
                camera->bgra.data(), output_stride);

            tc_frame frame{};
            frame.struct_size = sizeof(tc_frame);
            frame.data = camera->bgra.data();
            frame.data_length = camera->bgra.size();
            frame.width = width;
            frame.height = height;
            frame.stride = output_stride;
            frame.pixel_format = TC_PIXEL_BGRA32;
            frame.rotation_degrees = camera->sensor_orientation;
            frame.mirrored = camera->mirrored;
            frame.timestamp_microseconds = timestamp_ns > 0
                ? timestamp_ns / 1000
                : thincam::monotonic_microseconds();

            if (camera->frame_callback) camera->frame_callback(&frame, camera->user_data);
        }

        AImage_delete(image);
    } catch (...) {
        if (image) AImage_delete(image);
        camera->running.store(false, std::memory_order_release);
        report_camera_error(
            camera,
            TC_ERROR_PLATFORM,
            "Unhandled error while processing an Android camera frame.",
            true);
    }
}

void cleanup_session(tc_camera* camera) {
    if (!camera) return;

    if (camera->image_reader) {
        AImageReader_ImageListener no_listener{};
        AImageReader_setImageListener(camera->image_reader, &no_listener);
    }

    if (camera->session) {
        ACameraCaptureSession_stopRepeating(camera->session);
        ACameraCaptureSession_abortCaptures(camera->session);

        {
            std::lock_guard<std::mutex> lock(camera->callback_mutex);
            camera->session_closed = false;
        }
        ACameraCaptureSession_close(camera->session);

        std::unique_lock<std::mutex> lock(camera->callback_mutex);
        camera->callback_condition.wait(
            lock,
            [camera] { return camera->session_closed; });
        camera->session = nullptr;
    }

    {
        std::unique_lock<std::mutex> lock(camera->callback_mutex);
        camera->callback_condition.wait(
            lock,
            [camera] { return camera->image_callbacks.load(std::memory_order_acquire) == 0; });
    }

    if (camera->request && camera->output_target) {
        ACaptureRequest_removeTarget(camera->request, camera->output_target);
    }
    if (camera->request) {
        ACaptureRequest_free(camera->request);
        camera->request = nullptr;
    }
    if (camera->output_target) {
        ACameraOutputTarget_free(camera->output_target);
        camera->output_target = nullptr;
    }
    if (camera->output_container && camera->session_output) {
        ACaptureSessionOutputContainer_remove(camera->output_container, camera->session_output);
    }
    if (camera->session_output) {
        ACaptureSessionOutput_free(camera->session_output);
        camera->session_output = nullptr;
    }
    if (camera->output_container) {
        ACaptureSessionOutputContainer_free(camera->output_container);
        camera->output_container = nullptr;
    }
    if (camera->image_reader) {
        AImageReader_delete(camera->image_reader);
        camera->image_reader = nullptr;
        camera->window = nullptr;
    }
    if (camera->device) {
        ACameraDevice_close(camera->device);
        camera->device = nullptr;
    }
}


tc_status request_u8(ACaptureRequest* request, uint32_t tag, uint8_t value) {
    if (!request) return TC_ERROR_NOT_RUNNING;
    return normalized_camera_status(ACaptureRequest_setEntry_u8(request, tag, 1, &value));
}

tc_status request_i32(ACaptureRequest* request, uint32_t tag, const int32_t* values, uint32_t count) {
    if (!request || !values || count == 0) return TC_ERROR_INVALID_ARGUMENT;
    return normalized_camera_status(ACaptureRequest_setEntry_i32(request, tag, count, values));
}

tc_status request_i64(ACaptureRequest* request, uint32_t tag, int64_t value) {
    if (!request) return TC_ERROR_NOT_RUNNING;
    return normalized_camera_status(ACaptureRequest_setEntry_i64(request, tag, 1, &value));
}

tc_status request_float(ACaptureRequest* request, uint32_t tag, float value) {
    if (!request) return TC_ERROR_NOT_RUNNING;
    return normalized_camera_status(ACaptureRequest_setEntry_float(request, tag, 1, &value));
}

tc_status resubmit_request(tc_camera* camera) {
    if (!camera || !camera->request || !camera->session ||
        !camera->running.load(std::memory_order_acquire)) {
        return TC_ERROR_NOT_RUNNING;
    }
    int sequence_id = 0;
    return normalized_camera_status(ACameraCaptureSession_setRepeatingRequest(
        camera->session, nullptr, 1, &camera->request, &sequence_id));
}

tc_status trigger_auto_focus(tc_camera* camera, uint8_t mode) {
    if (!camera || !camera->request || !camera->session ||
        !camera->running.load(std::memory_order_acquire)) {
        return TC_ERROR_NOT_RUNNING;
    }

    tc_status status = request_u8(camera->request, ACAMERA_CONTROL_AF_MODE, mode);
    if (status != TC_OK) return status;
    status = request_u8(camera->request, ACAMERA_CONTROL_AF_TRIGGER, ACAMERA_CONTROL_AF_TRIGGER_START);
    if (status != TC_OK) return status;

    int sequence_id = 0;
    status = normalized_camera_status(ACameraCaptureSession_capture(
        camera->session, nullptr, 1, &camera->request, &sequence_id));

    // Triggers are edge-like commands. Keep them out of the repeating request after
    // the one-shot capture has been submitted.
    const tc_status reset_status = request_u8(
        camera->request, ACAMERA_CONTROL_AF_TRIGGER, ACAMERA_CONTROL_AF_TRIGGER_IDLE);
    if (status != TC_OK) return status;
    if (reset_status != TC_OK) return reset_status;
    return resubmit_request(camera);
}

bool valid_active_array(const tc_camera* camera) {
    return camera &&
        camera->active_array[2] > camera->active_array[0] &&
        camera->active_array[3] > camera->active_array[1];
}

tc_status apply_zoom_crop(tc_camera* camera, double factor) {
    if (!camera || !camera->request || !valid_active_array(camera) ||
        !std::isfinite(factor) || factor < 1.0 || factor > camera->maximum_zoom_factor) {
        return TC_ERROR_INVALID_ARGUMENT;
    }

    const int32_t left = camera->active_array[0];
    const int32_t top = camera->active_array[1];
    const int32_t right = camera->active_array[2];
    const int32_t bottom = camera->active_array[3];
    const double full_width = static_cast<double>(right - left);
    const double full_height = static_cast<double>(bottom - top);
    const int32_t crop_width = std::max(2, static_cast<int32_t>(std::llround(full_width / factor)) & ~1);
    const int32_t crop_height = std::max(2, static_cast<int32_t>(std::llround(full_height / factor)) & ~1);
    const int32_t center_x = left + (right - left) / 2;
    const int32_t center_y = top + (bottom - top) / 2;
    const int32_t crop[4] = {
        center_x - crop_width / 2,
        center_y - crop_height / 2,
        center_x + crop_width / 2,
        center_y + crop_height / 2
    };
    return request_i32(camera->request, ACAMERA_SCALER_CROP_REGION, crop, 4);
}

tc_status apply_initial_controls(tc_camera* camera) {
    if (!camera || !camera->request) return TC_ERROR_INVALID_ARGUMENT;
    tc_status status = TC_OK;

    if (camera->exposure_modes & thincam::enum_flag(TC_EXPOSURE_MODE_AUTO)) {
        status = request_u8(
            camera->request, ACAMERA_CONTROL_AE_MODE, camera->automatic_exposure_mode);
        if (status != TC_OK) return status;
        if (camera->exposure_lock_supported) {
            status = request_u8(camera->request, ACAMERA_CONTROL_AE_LOCK, ACAMERA_CONTROL_AE_LOCK_OFF);
            if (status != TC_OK) return status;
        }
        camera->exposure_mode = TC_EXPOSURE_MODE_AUTO;
    }
    if (camera->exposure_compensation_supported) {
        const int32_t compensation = 0;
        status = request_i32(camera->request, ACAMERA_CONTROL_AE_EXPOSURE_COMPENSATION, &compensation, 1);
        if (status != TC_OK) return status;
        camera->exposure_compensation_current = 0;
    }

    if (camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO)) {
        status = request_u8(camera->request, ACAMERA_CONTROL_AF_MODE, camera->continuous_focus_mode);
        if (status != TC_OK) return status;
        camera->focus_mode = TC_FOCUS_MODE_CONTINUOUS_AUTO;
    } else if (camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_AUTO)) {
        status = request_u8(camera->request, ACAMERA_CONTROL_AF_MODE, camera->automatic_focus_mode);
        if (status != TC_OK) return status;
        camera->focus_mode = TC_FOCUS_MODE_AUTO;
    }

    if (camera->maximum_zoom_factor > 1.0f && valid_active_array(camera)) {
        status = apply_zoom_crop(camera, 1.0);
        if (status != TC_OK) return status;
        camera->zoom_factor = 1.0;
    }
    if (camera->light_available) {
        status = request_u8(camera->request, ACAMERA_FLASH_MODE, ACAMERA_FLASH_MODE_OFF);
        if (status != TC_OK) return status;
        camera->light_enabled = false;
    }
    return TC_OK;
}

tc_status android_control_info(tc_camera* camera, tc_control_id id, tc_control_info* info) {
    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    const uint32_t flags = TC_CONTROL_FLAG_READABLE | TC_CONTROL_FLAG_WRITABLE;

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE:
            if (camera->exposure_modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, flags, 0, 0, 0,
                (camera->exposure_modes & thincam::enum_flag(TC_EXPOSURE_MODE_AUTO))
                    ? TC_EXPOSURE_MODE_AUTO
                    : TC_EXPOSURE_MODE_MANUAL,
                camera->exposure_modes);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
            if (!camera->exposure_compensation_supported) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, flags,
                camera->exposure_compensation_min * camera->exposure_compensation_step_ev,
                camera->exposure_compensation_max * camera->exposure_compensation_step_ev,
                camera->exposure_compensation_step_ev, 0.0);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_DURATION_US:
            if (!camera->manual_exposure_supported) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_INT64, flags,
                camera->exposure_duration_min_ns / 1000.0,
                camera->exposure_duration_max_ns / 1000.0,
                1.0,
                camera->exposure_duration_min_ns / 1000.0);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_ISO:
            if (!camera->manual_exposure_supported) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, flags,
                camera->iso_min, camera->iso_max, 1.0, camera->iso_min);
            return TC_OK;
        case TC_CONTROL_FOCUS_MODE:
            if (camera->focus_modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, flags, 0, 0, 0,
                camera->focus_mode, camera->focus_modes);
            return TC_OK;
        case TC_CONTROL_FOCUS_POSITION:
            if (!(camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_MANUAL)) ||
                camera->minimum_focus_distance <= 0.0f) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, flags, 0.0, 1.0, 0.0, 1.0);
            return TC_OK;
        case TC_CONTROL_ZOOM_FACTOR:
            if (camera->maximum_zoom_factor <= 1.0f || !valid_active_array(camera)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, flags, 1.0,
                camera->maximum_zoom_factor, 0.0, 1.0);
            return TC_OK;
        case TC_CONTROL_LIGHT_ENABLED:
            if (!camera->light_available) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_BOOL, flags, 0.0, 1.0, 1.0, 0.0);
            return TC_OK;
        case TC_CONTROL_LIGHT_LEVEL:
            return TC_ERROR_NOT_SUPPORTED;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status android_get_control(tc_camera* camera, tc_control_id id, tc_control_value* value) {
    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE:
            if (camera->exposure_modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::enum_control(id, camera->exposure_mode);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
            if (!camera->exposure_compensation_supported) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::double_control(
                id, camera->exposure_compensation_current * camera->exposure_compensation_step_ev);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_DURATION_US:
            if (!camera->manual_exposure_supported || camera->exposure_mode != TC_EXPOSURE_MODE_MANUAL) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::int64_control(id, camera->exposure_duration_current_ns / 1000);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_ISO:
            if (!camera->manual_exposure_supported || camera->exposure_mode != TC_EXPOSURE_MODE_MANUAL) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::double_control(id, camera->iso_current);
            return TC_OK;
        case TC_CONTROL_FOCUS_MODE:
            if (camera->focus_modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::enum_control(id, camera->focus_mode);
            return TC_OK;
        case TC_CONTROL_FOCUS_POSITION:
            if (!(camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_MANUAL)) ||
                (camera->focus_mode != TC_FOCUS_MODE_MANUAL && camera->focus_mode != TC_FOCUS_MODE_LOCKED)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::double_control(id, camera->focus_position);
            return TC_OK;
        case TC_CONTROL_ZOOM_FACTOR:
            if (camera->maximum_zoom_factor <= 1.0f) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::double_control(id, camera->zoom_factor);
            return TC_OK;
        case TC_CONTROL_LIGHT_ENABLED:
            if (!camera->light_available) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::bool_control(id, camera->light_enabled);
            return TC_OK;
        case TC_CONTROL_LIGHT_LEVEL:
            return TC_ERROR_NOT_SUPPORTED;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status android_set_control(tc_camera* camera, const tc_control_value* value) {
    if (!camera || !value || !camera->request) return TC_ERROR_INVALID_ARGUMENT;
    tc_status status = TC_OK;

    switch (value->id) {
        case TC_CONTROL_EXPOSURE_MODE:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) return TC_ERROR_INVALID_ARGUMENT;
            if (!(camera->exposure_modes & thincam::enum_flag(value->value.enum_value))) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            switch (value->value.enum_value) {
                case TC_EXPOSURE_MODE_AUTO:
                    status = request_u8(
                        camera->request, ACAMERA_CONTROL_AE_MODE, camera->automatic_exposure_mode);
                    if (status == TC_OK && camera->exposure_lock_supported) {
                        status = request_u8(
                            camera->request, ACAMERA_CONTROL_AE_LOCK, ACAMERA_CONTROL_AE_LOCK_OFF);
                    }
                    break;
                case TC_EXPOSURE_MODE_LOCKED:
                    if (!camera->exposure_lock_supported) return TC_ERROR_NOT_SUPPORTED;
                    status = request_u8(
                        camera->request, ACAMERA_CONTROL_AE_MODE, camera->automatic_exposure_mode);
                    if (status == TC_OK) status = request_u8(
                        camera->request, ACAMERA_CONTROL_AE_LOCK, ACAMERA_CONTROL_AE_LOCK_ON);
                    break;
                case TC_EXPOSURE_MODE_MANUAL:
                    if (!camera->manual_exposure_supported) return TC_ERROR_NOT_SUPPORTED;
                    if (camera->exposure_lock_supported) {
                        status = request_u8(camera->request, ACAMERA_CONTROL_AE_LOCK, ACAMERA_CONTROL_AE_LOCK_OFF);
                    }
                    if (status == TC_OK) status = request_u8(
                        camera->request, ACAMERA_CONTROL_AE_MODE, ACAMERA_CONTROL_AE_MODE_OFF);
                    if (status == TC_OK) status = request_i64(
                        camera->request, ACAMERA_SENSOR_EXPOSURE_TIME,
                        camera->exposure_duration_current_ns);
                    if (status == TC_OK) {
                        const int32_t iso = camera->iso_current;
                        status = request_i32(camera->request, ACAMERA_SENSOR_SENSITIVITY, &iso, 1);
                    }
                    break;
                default:
                    return TC_ERROR_INVALID_ARGUMENT;
            }
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->exposure_mode = value->value.enum_value;
            return status;
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value)) return TC_ERROR_INVALID_ARGUMENT;
            if (!camera->exposure_compensation_supported) return TC_ERROR_NOT_SUPPORTED;
            const double raw_value = value->value.double_value / camera->exposure_compensation_step_ev;
            const int32_t raw = static_cast<int32_t>(std::llround(raw_value));
            if (raw < camera->exposure_compensation_min || raw > camera->exposure_compensation_max ||
                std::abs(raw_value - raw) > 1e-6) return TC_ERROR_INVALID_ARGUMENT;
            status = request_i32(camera->request, ACAMERA_CONTROL_AE_EXPOSURE_COMPENSATION, &raw, 1);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->exposure_compensation_current = raw;
            return status;
        }
        case TC_CONTROL_EXPOSURE_DURATION_US: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_INT64) ||
                value->value.integer_value <= 0 ||
                value->value.integer_value > INT64_MAX / 1000) return TC_ERROR_INVALID_ARGUMENT;
            if (!camera->manual_exposure_supported) return TC_ERROR_NOT_SUPPORTED;
            const int64_t nanoseconds = value->value.integer_value * 1000;
            if (nanoseconds < camera->exposure_duration_min_ns ||
                nanoseconds > camera->exposure_duration_max_ns) return TC_ERROR_INVALID_ARGUMENT;
            status = request_i64(camera->request, ACAMERA_SENSOR_EXPOSURE_TIME, nanoseconds);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->exposure_duration_current_ns = nanoseconds;
            return status;
        }
        case TC_CONTROL_EXPOSURE_ISO: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->manual_exposure_supported) return TC_ERROR_NOT_SUPPORTED;
            const int32_t iso = static_cast<int32_t>(std::llround(value->value.double_value));
            if (iso < camera->iso_min || iso > camera->iso_max ||
                std::abs(value->value.double_value - iso) > 1e-6) return TC_ERROR_INVALID_ARGUMENT;
            status = request_i32(camera->request, ACAMERA_SENSOR_SENSITIVITY, &iso, 1);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->iso_current = iso;
            return status;
        }
        case TC_CONTROL_FOCUS_MODE:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!(camera->focus_modes & thincam::enum_flag(value->value.enum_value))) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            switch (value->value.enum_value) {
                case TC_FOCUS_MODE_AUTO:
                    status = trigger_auto_focus(camera, camera->automatic_focus_mode);
                    if (status == TC_OK) camera->focus_mode = TC_FOCUS_MODE_AUTO;
                    return status;
                case TC_FOCUS_MODE_CONTINUOUS_AUTO:
                    status = request_u8(camera->request, ACAMERA_CONTROL_AF_TRIGGER, ACAMERA_CONTROL_AF_TRIGGER_IDLE);
                    if (status == TC_OK) status = request_u8(
                        camera->request, ACAMERA_CONTROL_AF_MODE, camera->continuous_focus_mode);
                    break;
                case TC_FOCUS_MODE_MANUAL: {
                    status = request_u8(camera->request, ACAMERA_CONTROL_AF_MODE, ACAMERA_CONTROL_AF_MODE_OFF);
                    if (status == TC_OK && camera->minimum_focus_distance > 0.0f) {
                        const float distance = static_cast<float>(
                            (1.0 - camera->focus_position) * camera->minimum_focus_distance);
                        status = request_float(camera->request, ACAMERA_LENS_FOCUS_DISTANCE, distance);
                    }
                    break;
                }
                case TC_FOCUS_MODE_LOCKED:
                    return TC_ERROR_NOT_SUPPORTED;
                default:
                    return TC_ERROR_INVALID_ARGUMENT;
            }
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->focus_mode = value->value.enum_value;
            return status;
        case TC_CONTROL_FOCUS_POSITION: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value < 0.0 ||
                value->value.double_value > 1.0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!(camera->focus_modes & thincam::enum_flag(TC_FOCUS_MODE_MANUAL)) ||
                camera->minimum_focus_distance <= 0.0f) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const float distance = static_cast<float>(
                (1.0 - value->value.double_value) * camera->minimum_focus_distance);
            status = request_u8(camera->request, ACAMERA_CONTROL_AF_MODE, ACAMERA_CONTROL_AF_MODE_OFF);
            if (status == TC_OK) status = request_float(
                camera->request, ACAMERA_LENS_FOCUS_DISTANCE, distance);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) {
                camera->focus_position = value->value.double_value;
                camera->focus_mode = TC_FOCUS_MODE_MANUAL;
            }
            return status;
        }
        case TC_CONTROL_ZOOM_FACTOR:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value <= 0.0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (camera->maximum_zoom_factor <= 1.0f || !valid_active_array(camera)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            status = apply_zoom_crop(camera, value->value.double_value);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->zoom_factor = value->value.double_value;
            return status;
        case TC_CONTROL_LIGHT_ENABLED:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_BOOL)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->light_available) return TC_ERROR_NOT_SUPPORTED;
            status = request_u8(
                camera->request, ACAMERA_FLASH_MODE,
                value->value.boolean_value != 0 ? ACAMERA_FLASH_MODE_TORCH : ACAMERA_FLASH_MODE_OFF);
            if (status == TC_OK) status = resubmit_request(camera);
            if (status == TC_OK) camera->light_enabled = value->value.boolean_value != 0;
            return status;
        case TC_CONTROL_LIGHT_LEVEL:
            return TC_ERROR_NOT_SUPPORTED;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

} // namespace

tc_status TC_CALL tc_get_permission_status(tc_permission_status* status) {
    if (!status) return TC_ERROR_INVALID_ARGUMENT;
    *status = TC_PERMISSION_HOST_ACTION_REQUIRED;
    return TC_OK;
}

tc_status TC_CALL tc_request_permission(tc_permission_callback callback, void* user_data) {
    if (callback) callback(TC_PERMISSION_HOST_ACTION_REQUIRED, user_data);
    return TC_OK;
}

tc_status TC_CALL tc_enumerate_devices(tc_device_callback callback, void* user_data) {
    if (!callback) return TC_ERROR_INVALID_ARGUMENT;

    ACameraManager* manager = nullptr;
    ACameraIdList* list = nullptr;
    const auto cleanup = [&]() noexcept {
        if (list) {
            ACameraManager_deleteCameraIdList(list);
            list = nullptr;
        }
        if (manager) {
            ACameraManager_delete(manager);
            manager = nullptr;
        }
    };

    try {
        manager = ACameraManager_create();
        if (!manager) return TC_ERROR_PLATFORM;

        const camera_status_t result = ACameraManager_getCameraIdList(manager, &list);
        if (result != ACAMERA_OK || !list) {
            cleanup();
            return normalized_camera_status(result);
        }

        std::string default_id;
        for (int index = 0; index < list->numCameras; ++index) {
            ACameraMetadata* metadata = nullptr;
            bool is_back = false;
            if (ACameraManager_getCameraCharacteristics(
                    manager,
                    list->cameraIds[index],
                    &metadata) == ACAMERA_OK && metadata) {
                uint8_t facing = ACAMERA_LENS_FACING_EXTERNAL;
                is_back = metadata_byte(metadata, ACAMERA_LENS_FACING, &facing) &&
                    facing == ACAMERA_LENS_FACING_BACK;
                ACameraMetadata_free(metadata);
            }
            if (is_back) {
                default_id = list->cameraIds[index];
                break;
            }
        }
        if (default_id.empty() && list->numCameras > 0) {
            default_id = list->cameraIds[0];
        }

        for (int index = 0; index < list->numCameras; ++index) {
            const std::string id = list->cameraIds[index];
            ACameraMetadata* metadata = nullptr;
            uint8_t facing = ACAMERA_LENS_FACING_EXTERNAL;
            if (ACameraManager_getCameraCharacteristics(
                    manager,
                    id.c_str(),
                    &metadata) == ACAMERA_OK && metadata) {
                metadata_byte(metadata, ACAMERA_LENS_FACING, &facing);
                ACameraMetadata_free(metadata);
            }

            const std::string name = "Android camera " + id;
            tc_device_info info{};
            info.struct_size = sizeof(tc_device_info);
            info.id = id.c_str();
            info.name = name.c_str();
            info.position = lens_position(facing);
            info.is_default = id == default_id ? 1 : 0;
            callback(&info, user_data);
        }

        cleanup();
        return TC_OK;
    } catch (...) {
        cleanup();
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_open(
    const char* device_id,
    const tc_open_options* options,
    tc_frame_callback frame_callback,
    tc_error_callback error_callback,
    void* user_data,
    tc_camera** camera) {

    if (!device_id || !frame_callback || !camera) return TC_ERROR_INVALID_ARGUMENT;
    *camera = nullptr;

    auto instance = new (std::nothrow) tc_camera();
    if (!instance) return TC_ERROR_PLATFORM;

    try {
        instance->device_id = device_id;
        instance->options = thincam::normalized_options(options);
        instance->frame_callback = frame_callback;
        instance->error_callback = error_callback;
        instance->user_data = user_data;
        instance->manager = ACameraManager_create();
    } catch (...) {
        delete instance;
        return TC_ERROR_PLATFORM;
    }

    if (!instance->manager) {
        delete instance;
        return TC_ERROR_PLATFORM;
    }
    if (!load_control_characteristics(instance)) {
        ACameraManager_delete(instance->manager);
        delete instance;
        return TC_ERROR_DEVICE_NOT_FOUND;
    }
    if (instance->options.pixel_format != TC_PIXEL_BGRA32) {
        ACameraManager_delete(instance->manager);
        delete instance;
        return TC_ERROR_FORMAT_NOT_SUPPORTED;
    }

    *camera = instance;
    return TC_OK;
}

tc_status TC_CALL tc_camera_start(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    try {
        std::lock_guard<std::mutex> lifecycle_lock(camera->lifecycle_mutex);
        if (camera->running.load(std::memory_order_acquire)) {
            return TC_ERROR_ALREADY_RUNNING;
        }

        camera->stopping.store(false, std::memory_order_release);
        if (!select_output_size(
                camera->manager,
                camera->device_id,
                camera->options.width,
                camera->options.height,
                &camera->width,
                &camera->height,
                &camera->sensor_orientation,
                &camera->mirrored)) {
            return TC_ERROR_FORMAT_NOT_SUPPORTED;
        }

        const size_t pixel_count =
            static_cast<size_t>(camera->width) * static_cast<size_t>(camera->height);
        if (camera->width <= 0 || camera->height <= 0 ||
            pixel_count > static_cast<size_t>(INT32_MAX) / 4) {
            return TC_ERROR_FORMAT_NOT_SUPPORTED;
        }

        media_status_t media_result = AImageReader_new(
            camera->width,
            camera->height,
            AIMAGE_FORMAT_YUV_420_888,
            3,
            &camera->image_reader);
        if (media_result != AMEDIA_OK) return TC_ERROR_PLATFORM;

        AImageReader_ImageListener image_listener{};
        image_listener.context = camera;
        image_listener.onImageAvailable = on_image_available;
        AImageReader_setImageListener(camera->image_reader, &image_listener);

        if (AImageReader_getWindow(camera->image_reader, &camera->window) != AMEDIA_OK ||
            !camera->window) {
            cleanup_session(camera);
            return TC_ERROR_PLATFORM;
        }

        ACameraDevice_StateCallbacks device_callbacks{};
        device_callbacks.context = camera;
        device_callbacks.onDisconnected = on_device_disconnected;
        device_callbacks.onError = on_device_error;

        camera_status_t result = ACameraManager_openCamera(
            camera->manager,
            camera->device_id.c_str(),
            &device_callbacks,
            &camera->device);
        if (result != ACAMERA_OK) {
            cleanup_session(camera);
            return normalized_camera_status(result);
        }

        result = ACameraDevice_createCaptureRequest(
            camera->device,
            TEMPLATE_PREVIEW,
            &camera->request);
        if (result == ACAMERA_OK) {
            result = ACameraOutputTarget_create(camera->window, &camera->output_target);
        }
        if (result == ACAMERA_OK) {
            result = ACaptureRequest_addTarget(camera->request, camera->output_target);
        }
        if (result == ACAMERA_OK) {
            const tc_status control_status = apply_initial_controls(camera);
            if (control_status != TC_OK) {
                cleanup_session(camera);
                return control_status;
            }
        }
        if (result == ACAMERA_OK) {
            result = ACaptureSessionOutput_create(camera->window, &camera->session_output);
        }
        if (result == ACAMERA_OK) {
            result = ACaptureSessionOutputContainer_create(&camera->output_container);
        }
        if (result == ACAMERA_OK) {
            result = ACaptureSessionOutputContainer_add(
                camera->output_container,
                camera->session_output);
        }

        if (result != ACAMERA_OK) {
            cleanup_session(camera);
            return normalized_camera_status(result);
        }

        ACameraCaptureSession_stateCallbacks session_callbacks{};
        session_callbacks.context = camera;
        session_callbacks.onClosed = on_session_closed;
        session_callbacks.onReady = on_session_ready;
        session_callbacks.onActive = on_session_active;

        {
            std::lock_guard<std::mutex> lock(camera->callback_mutex);
            camera->session_ready = false;
            camera->session_closed = false;
        }

        result = ACameraDevice_createCaptureSession(
            camera->device,
            camera->output_container,
            &session_callbacks,
            &camera->session);
        if (result != ACAMERA_OK) {
            cleanup_session(camera);
            return normalized_camera_status(result);
        }

        // Mark running before the repeating request starts so an immediately delivered
        // AImageReader callback drains and processes its image rather than filling the queue.
        camera->running.store(true, std::memory_order_release);

        int sequence_id = 0;
        result = ACameraCaptureSession_setRepeatingRequest(
            camera->session,
            nullptr,
            1,
            &camera->request,
            &sequence_id);
        if (result != ACAMERA_OK) {
            camera->running.store(false, std::memory_order_release);
            cleanup_session(camera);
            return normalized_camera_status(result);
        }

        std::unique_lock<std::mutex> callback_lock(camera->callback_mutex);
        const bool ready = camera->callback_condition.wait_for(
            callback_lock,
            std::chrono::seconds(5),
            [camera] { return camera->session_ready; });

        if (!ready) {
            callback_lock.unlock();
            camera->running.store(false, std::memory_order_release);
            cleanup_session(camera);
            return TC_ERROR_TIMEOUT;
        }

        return TC_OK;
    } catch (...) {
        camera->running.store(false, std::memory_order_release);
        camera->stopping.store(true, std::memory_order_release);
        try {
            cleanup_session(camera);
        } catch (...) {
            // Preserve the original platform failure.
        }
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_stop(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        camera->stopping.store(true, std::memory_order_release);
        camera->running.store(false, std::memory_order_release);
        cleanup_session(camera);
        return TC_OK;
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

void TC_CALL tc_camera_close(tc_camera* camera) {
    if (!camera) return;

    try {
        if (tc_camera_stop(camera) != TC_OK) {
            return;
        }
        if (camera->manager) {
            ACameraManager_delete(camera->manager);
            camera->manager = nullptr;
        }
        delete camera;
    } catch (...) {
        // Close must not unwind across the C ABI. If cleanup itself fails, leaking the
        // native handle is safer than freeing memory still reachable by platform callbacks.
    }
}


tc_status TC_CALL tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info) {

    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        return android_control_info(camera, id, info);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_get_control(
    tc_camera* camera,
    tc_control_id id,
    tc_control_value* value) {

    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        return android_get_control(camera, id, value);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_set_control(
    tc_camera* camera,
    const tc_control_value* value) {

    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        return android_set_control(camera, value);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}
