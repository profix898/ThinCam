#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <TargetConditionals.h>

#include "thincam.h"
#include "thincam_common.hpp"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <mutex>
#include <new>
#include <string>

// AVCaptureDevice exposes the exposure/lens/zoom controls only on the embedded
// platforms. macOS builds compile the same file, so those controls are compiled
// out there and reported as TC_ERROR_NOT_SUPPORTED.
#if TARGET_OS_IPHONE
  #define THINCAM_HAS_DEVICE_LENS_CONTROLS 1
#else
  #define THINCAM_HAS_DEVICE_LENS_CONTROLS 0
#endif

struct tc_camera;

@interface TCFrameDelegate : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate> {
@public
    tc_camera* owner;
}
@end

struct tc_camera {
    std::string device_id;
    tc_open_options options{};
    tc_frame_callback frame_callback = nullptr;
    tc_error_callback error_callback = nullptr;
    void* user_data = nullptr;

    AVCaptureSession* session = nil;
    AVCaptureDevice* device = nil;
    AVCaptureVideoDataOutput* output = nil;
    TCFrameDelegate* delegate = nil;
    dispatch_queue_t callback_queue = nil;

    std::atomic<bool> running{false};
    std::mutex lifecycle_mutex;
    int mirrored = 0;
    std::atomic<int32_t> focus_mode_state{TC_FOCUS_MODE_LOCKED};
};

namespace {

const void* callback_queue_key = &callback_queue_key;

std::string utf8(NSString* value) {
    return value ? std::string(value.UTF8String ?: "") : std::string();
}

NSString* ns_string(const std::string& value) {
    return [NSString stringWithUTF8String:value.c_str()];
}

tc_permission_status map_permission(AVAuthorizationStatus status) {
    switch (status) {
        case AVAuthorizationStatusNotDetermined: return TC_PERMISSION_NOT_DETERMINED;
        case AVAuthorizationStatusAuthorized: return TC_PERMISSION_GRANTED;
        case AVAuthorizationStatusDenied: return TC_PERMISSION_DENIED;
        case AVAuthorizationStatusRestricted: return TC_PERMISSION_RESTRICTED;
        default: return TC_PERMISSION_UNKNOWN;
    }
}

tc_camera_position map_position(AVCaptureDevicePosition position) {
    switch (position) {
        case AVCaptureDevicePositionFront: return TC_POSITION_FRONT;
        case AVCaptureDevicePositionBack: return TC_POSITION_BACK;
        case AVCaptureDevicePositionUnspecified: return TC_POSITION_EXTERNAL;
        default: return TC_POSITION_UNSPECIFIED;
    }
}

AVCaptureDevice* find_device(const std::string& id) {
    NSString* requested = ns_string(id);
    for (AVCaptureDevice* device in [AVCaptureDevice devicesWithMediaType:AVMediaTypeVideo]) {
        if ([device.uniqueID isEqualToString:requested]) return device;
    }
    return nil;
}

void select_best_format(AVCaptureDevice* device, const tc_open_options& options) {
    AVCaptureDeviceFormat* best = nil;
    double best_score = HUGE_VAL;

    for (AVCaptureDeviceFormat* format in device.formats) {
        CMVideoDimensions dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription);
        const double size_score =
            std::abs(static_cast<double>(dimensions.width - options.width)) +
            std::abs(static_cast<double>(dimensions.height - options.height));

        bool supports_rate = false;
        for (AVFrameRateRange* range in format.videoSupportedFrameRateRanges) {
            if (options.frames_per_second >= range.minFrameRate &&
                options.frames_per_second <= range.maxFrameRate) {
                supports_rate = true;
                break;
            }
        }

        const double score = size_score + (supports_rate ? 0.0 : 1000000.0);
        if (score < best_score) {
            best_score = score;
            best = format;
        }
    }

    if (!best) return;

    NSError* error = nil;
    if (![device lockForConfiguration:&error]) return;

    @try {
        device.activeFormat = best;
        CMTime duration = CMTimeMake(1, std::max(1, options.frames_per_second));
        device.activeVideoMinFrameDuration = duration;
        device.activeVideoMaxFrameDuration = duration;
    } @catch (NSException*) {
        // Some cameras expose a format but reject an exact duration. Keep the selected format.
    }

    [device unlockForConfiguration];
}


uint32_t apple_flags() {
    return TC_CONTROL_FLAG_READABLE | TC_CONTROL_FLAG_WRITABLE;
}

tc_status lock_device(AVCaptureDevice* device, NSError** error) {
    if (!device) return TC_ERROR_DEVICE_NOT_FOUND;
    if (![device lockForConfiguration:error]) {
        if (*error && (*error).code == AVErrorApplicationIsNotAuthorizedToUseDevice) {
            return TC_ERROR_PERMISSION_DENIED;
        }
        return TC_ERROR_PLATFORM;
    }
    return TC_OK;
}

bool apple_supports_custom_exposure(AVCaptureDevice* device) {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
    return [device isExposureModeSupported:AVCaptureExposureModeCustom] != NO;
#else
    (void)device;
    return false;
#endif
}

bool apple_supports_custom_lens_position(AVCaptureDevice* device) {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
    return device.isLockingFocusWithCustomLensPositionSupported != NO;
#else
    (void)device;
    return false;
#endif
}

uint64_t apple_exposure_modes(AVCaptureDevice* device) {
    uint64_t modes = 0;
    if ([device isExposureModeSupported:AVCaptureExposureModeContinuousAutoExposure] ||
        [device isExposureModeSupported:AVCaptureExposureModeAutoExpose]) {
        modes |= thincam::enum_flag(TC_EXPOSURE_MODE_AUTO);
    }
    if (apple_supports_custom_exposure(device)) {
        modes |= thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL);
    }
    if ([device isExposureModeSupported:AVCaptureExposureModeLocked]) {
        modes |= thincam::enum_flag(TC_EXPOSURE_MODE_LOCKED);
    }
    return modes;
}

uint64_t apple_focus_modes(AVCaptureDevice* device) {
    uint64_t modes = 0;
    if ([device isFocusModeSupported:AVCaptureFocusModeAutoFocus]) {
        modes |= thincam::enum_flag(TC_FOCUS_MODE_AUTO);
    }
    if ([device isFocusModeSupported:AVCaptureFocusModeContinuousAutoFocus]) {
        modes |= thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO);
    }
    if (apple_supports_custom_lens_position(device)) {
        modes |= thincam::enum_flag(TC_FOCUS_MODE_MANUAL);
    }
    if ([device isFocusModeSupported:AVCaptureFocusModeLocked]) {
        modes |= thincam::enum_flag(TC_FOCUS_MODE_LOCKED);
    }
    return modes;
}

tc_status apple_control_info(tc_camera* camera, tc_control_id id, tc_control_info* info) {
    if (!camera || !camera->device || !info) return TC_ERROR_INVALID_ARGUMENT;
    AVCaptureDevice* device = camera->device;

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            const uint64_t modes = apple_exposure_modes(device);
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, apple_flags(), 0, 0, 0,
                (modes & thincam::enum_flag(TC_EXPOSURE_MODE_AUTO))
                    ? TC_EXPOSURE_MODE_AUTO
                    : ((modes & thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL))
                        ? TC_EXPOSURE_MODE_MANUAL
                        : TC_EXPOSURE_MODE_LOCKED),
                modes);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (device.maxExposureTargetBias <= device.minExposureTargetBias) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, apple_flags(),
                device.minExposureTargetBias, device.maxExposureTargetBias, 0.0, 0.0);
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_EXPOSURE_DURATION_US: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_exposure(device)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const double minimum = CMTimeGetSeconds(device.activeFormat.minExposureDuration) * 1000000.0;
            const double maximum = CMTimeGetSeconds(device.activeFormat.maxExposureDuration) * 1000000.0;
            const double current = CMTimeGetSeconds(device.exposureDuration) * 1000000.0;
            if (!std::isfinite(minimum) || !std::isfinite(maximum) || minimum <= 0 || maximum < minimum) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_INT64, apple_flags(), minimum, maximum, 1.0,
                std::clamp(current, minimum, maximum));
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        }
        case TC_CONTROL_EXPOSURE_ISO:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_exposure(device) ||
                device.activeFormat.maxISO <= device.activeFormat.minISO) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, apple_flags(),
                device.activeFormat.minISO, device.activeFormat.maxISO, 0.0,
                std::clamp(static_cast<double>(device.ISO),
                    static_cast<double>(device.activeFormat.minISO),
                    static_cast<double>(device.activeFormat.maxISO)));
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_FOCUS_MODE: {
            const uint64_t modes = apple_focus_modes(device);
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            int32_t default_mode = TC_FOCUS_MODE_LOCKED;
            if (modes & thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO)) {
                default_mode = TC_FOCUS_MODE_CONTINUOUS_AUTO;
            } else if (modes & thincam::enum_flag(TC_FOCUS_MODE_AUTO)) {
                default_mode = TC_FOCUS_MODE_AUTO;
            } else if (modes & thincam::enum_flag(TC_FOCUS_MODE_MANUAL)) {
                default_mode = TC_FOCUS_MODE_MANUAL;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, apple_flags(), 0, 0, 0, default_mode, modes);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_lens_position(device)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, apple_flags(), 0.0, 1.0, 0.0,
                static_cast<double>(device.lensPosition));
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_ZOOM_FACTOR: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            const double minimum = std::max(1.0, static_cast<double>(device.minAvailableVideoZoomFactor));
            const double maximum = static_cast<double>(device.maxAvailableVideoZoomFactor);
            if (!std::isfinite(maximum) || maximum <= minimum) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, apple_flags(), minimum, maximum, 0.0, minimum);
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        }
        case TC_CONTROL_LIGHT_ENABLED:
            if (!device.hasTorch ||
                ![device isTorchModeSupported:AVCaptureTorchModeOn] ||
                ![device isTorchModeSupported:AVCaptureTorchModeOff]) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_BOOL, apple_flags(), 0.0, 1.0, 1.0, 0.0);
            return TC_OK;
        case TC_CONTROL_LIGHT_LEVEL:
            if (!device.hasTorch ||
                ![device isTorchModeSupported:AVCaptureTorchModeOn] ||
                ![device isTorchModeSupported:AVCaptureTorchModeOff]) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, apple_flags(), 0.0,
                static_cast<double>(AVCaptureMaxAvailableTorchLevel), 0.0,
                std::min(0.5, static_cast<double>(AVCaptureMaxAvailableTorchLevel)));
            return TC_OK;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status apple_get_control(tc_camera* camera, tc_control_id id, tc_control_value* value) {
    if (!camera || !camera->device || !value) return TC_ERROR_INVALID_ARGUMENT;
    AVCaptureDevice* device = camera->device;

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            int32_t mode = TC_EXPOSURE_MODE_LOCKED;
            if (device.exposureMode == AVCaptureExposureModeContinuousAutoExposure ||
                device.exposureMode == AVCaptureExposureModeAutoExpose) {
                mode = TC_EXPOSURE_MODE_AUTO;
            }
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            else if (device.exposureMode == AVCaptureExposureModeCustom) {
                mode = TC_EXPOSURE_MODE_MANUAL;
            }
#endif
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (device.maxExposureTargetBias <= device.minExposureTargetBias) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::double_control(id, device.exposureTargetBias);
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_EXPOSURE_DURATION_US: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_exposure(device)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const double microseconds = CMTimeGetSeconds(device.exposureDuration) * 1000000.0;
            if (!std::isfinite(microseconds) || microseconds <= 0) return TC_ERROR_PLATFORM;
            *value = thincam::int64_control(id, static_cast<int64_t>(std::llround(microseconds)));
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        }
        case TC_CONTROL_EXPOSURE_ISO:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_exposure(device) ||
                device.activeFormat.maxISO <= device.activeFormat.minISO) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::double_control(id, device.ISO);
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_FOCUS_MODE: {
            int32_t mode = camera->focus_mode_state.load(std::memory_order_acquire);
            if (device.focusMode == AVCaptureFocusModeAutoFocus) mode = TC_FOCUS_MODE_AUTO;
            else if (device.focusMode == AVCaptureFocusModeContinuousAutoFocus) mode = TC_FOCUS_MODE_CONTINUOUS_AUTO;
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            if (!apple_supports_custom_lens_position(device)) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::double_control(id, static_cast<double>(device.lensPosition));
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        case TC_CONTROL_ZOOM_FACTOR: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
            const double minimum = std::max(1.0, static_cast<double>(device.minAvailableVideoZoomFactor));
            const double maximum = static_cast<double>(device.maxAvailableVideoZoomFactor);
            if (!std::isfinite(maximum) || maximum <= minimum) return TC_ERROR_NOT_SUPPORTED;
            *value = thincam::double_control(id, device.videoZoomFactor);
            return TC_OK;
#else
            return TC_ERROR_NOT_SUPPORTED;
#endif
        }
        case TC_CONTROL_LIGHT_ENABLED:
            if (!device.hasTorch ||
                ![device isTorchModeSupported:AVCaptureTorchModeOn] ||
                ![device isTorchModeSupported:AVCaptureTorchModeOff]) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::bool_control(id, device.torchMode == AVCaptureTorchModeOn);
            return TC_OK;
        case TC_CONTROL_LIGHT_LEVEL:
            if (!device.hasTorch ||
                ![device isTorchModeSupported:AVCaptureTorchModeOn] ||
                ![device isTorchModeSupported:AVCaptureTorchModeOff]) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *value = thincam::double_control(
                id, device.torchMode == AVCaptureTorchModeOn ? device.torchLevel : 0.0);
            return TC_OK;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status apple_set_control(tc_camera* camera, const tc_control_value* value) {
    if (!camera || !camera->device || !value) return TC_ERROR_INVALID_ARGUMENT;
    AVCaptureDevice* device = camera->device;
    NSError* error = nil;
    const tc_status lock_status = lock_device(device, &error);
    if (lock_status != TC_OK) return lock_status;

    tc_status result = TC_OK;
    @try {
        switch (value->id) {
            case TC_CONTROL_EXPOSURE_MODE:
                if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                    break;
                }
                switch (value->value.enum_value) {
                    case TC_EXPOSURE_MODE_AUTO:
                        if ([device isExposureModeSupported:AVCaptureExposureModeContinuousAutoExposure]) {
                            device.exposureMode = AVCaptureExposureModeContinuousAutoExposure;
                        } else if ([device isExposureModeSupported:AVCaptureExposureModeAutoExpose]) {
                            device.exposureMode = AVCaptureExposureModeAutoExpose;
                        } else result = TC_ERROR_NOT_SUPPORTED;
                        break;
                    case TC_EXPOSURE_MODE_MANUAL:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                        if (!apple_supports_custom_exposure(device)) {
                            result = TC_ERROR_NOT_SUPPORTED;
                        } else {
                            [device setExposureModeCustomWithDuration:device.exposureDuration
                                                                  ISO:device.ISO
                                                    completionHandler:nil];
                        }
#else
                        result = TC_ERROR_NOT_SUPPORTED;
#endif
                        break;
                    case TC_EXPOSURE_MODE_LOCKED:
                        if (![device isExposureModeSupported:AVCaptureExposureModeLocked]) {
                            result = TC_ERROR_NOT_SUPPORTED;
                        } else device.exposureMode = AVCaptureExposureModeLocked;
                        break;
                    default:
                        result = TC_ERROR_INVALID_ARGUMENT;
                        break;
                }
                break;
            case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                if (device.maxExposureTargetBias <= device.minExposureTargetBias) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                    !std::isfinite(value->value.double_value) ||
                    value->value.double_value < device.minExposureTargetBias ||
                    value->value.double_value > device.maxExposureTargetBias) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else {
                    [device setExposureTargetBias:static_cast<float>(value->value.double_value)
                               completionHandler:nil];
                }
#else
                result = TC_ERROR_NOT_SUPPORTED;
#endif
                break;
            case TC_CONTROL_EXPOSURE_DURATION_US: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                if (!apple_supports_custom_exposure(device)) {
                    result = TC_ERROR_NOT_SUPPORTED;
                    break;
                }
                if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_INT64) ||
                    value->value.integer_value <= 0) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                    break;
                }
                const CMTime duration = CMTimeMake(value->value.integer_value, 1000000);
                if (CMTimeCompare(duration, device.activeFormat.minExposureDuration) < 0 ||
                    CMTimeCompare(duration, device.activeFormat.maxExposureDuration) > 0) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else {
                    [device setExposureModeCustomWithDuration:duration
                                                          ISO:device.ISO
                                            completionHandler:nil];
                }
#else
                result = TC_ERROR_NOT_SUPPORTED;
#endif
                break;
            }
            case TC_CONTROL_EXPOSURE_ISO:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                if (!apple_supports_custom_exposure(device) ||
                    device.activeFormat.maxISO <= device.activeFormat.minISO) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                    !std::isfinite(value->value.double_value) ||
                    value->value.double_value < device.activeFormat.minISO ||
                    value->value.double_value > device.activeFormat.maxISO) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else {
                    [device setExposureModeCustomWithDuration:device.exposureDuration
                                                          ISO:static_cast<float>(value->value.double_value)
                                            completionHandler:nil];
                }
#else
                result = TC_ERROR_NOT_SUPPORTED;
#endif
                break;
            case TC_CONTROL_FOCUS_MODE:
                if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                    break;
                }
                switch (value->value.enum_value) {
                    case TC_FOCUS_MODE_AUTO:
                        if (![device isFocusModeSupported:AVCaptureFocusModeAutoFocus]) result = TC_ERROR_NOT_SUPPORTED;
                        else {
                            device.focusMode = AVCaptureFocusModeAutoFocus;
                            camera->focus_mode_state.store(TC_FOCUS_MODE_AUTO, std::memory_order_release);
                        }
                        break;
                    case TC_FOCUS_MODE_CONTINUOUS_AUTO:
                        if (![device isFocusModeSupported:AVCaptureFocusModeContinuousAutoFocus]) result = TC_ERROR_NOT_SUPPORTED;
                        else {
                            device.focusMode = AVCaptureFocusModeContinuousAutoFocus;
                            camera->focus_mode_state.store(TC_FOCUS_MODE_CONTINUOUS_AUTO, std::memory_order_release);
                        }
                        break;
                    case TC_FOCUS_MODE_MANUAL:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                        if (!apple_supports_custom_lens_position(device)) result = TC_ERROR_NOT_SUPPORTED;
                        else {
                            [device setFocusModeLockedWithLensPosition:device.lensPosition completionHandler:nil];
                            camera->focus_mode_state.store(TC_FOCUS_MODE_MANUAL, std::memory_order_release);
                        }
#else
                        result = TC_ERROR_NOT_SUPPORTED;
#endif
                        break;
                    case TC_FOCUS_MODE_LOCKED:
                        if (![device isFocusModeSupported:AVCaptureFocusModeLocked]) result = TC_ERROR_NOT_SUPPORTED;
                        else {
                            device.focusMode = AVCaptureFocusModeLocked;
                            camera->focus_mode_state.store(TC_FOCUS_MODE_LOCKED, std::memory_order_release);
                        }
                        break;
                    default:
                        result = TC_ERROR_INVALID_ARGUMENT;
                        break;
                }
                break;
            case TC_CONTROL_FOCUS_POSITION:
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                if (!apple_supports_custom_lens_position(device)) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                    !std::isfinite(value->value.double_value) ||
                    value->value.double_value < 0.0 || value->value.double_value > 1.0) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else {
                    [device setFocusModeLockedWithLensPosition:
                        static_cast<float>(value->value.double_value)
                                               completionHandler:nil];
                    camera->focus_mode_state.store(TC_FOCUS_MODE_MANUAL, std::memory_order_release);
                }
#else
                result = TC_ERROR_NOT_SUPPORTED;
#endif
                break;
            case TC_CONTROL_ZOOM_FACTOR: {
#if THINCAM_HAS_DEVICE_LENS_CONTROLS
                const double minimum = std::max(1.0, static_cast<double>(device.minAvailableVideoZoomFactor));
                const double maximum = static_cast<double>(device.maxAvailableVideoZoomFactor);
                if (!std::isfinite(maximum) || maximum <= minimum) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                    !std::isfinite(value->value.double_value) ||
                    value->value.double_value < minimum || value->value.double_value > maximum) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else device.videoZoomFactor = static_cast<CGFloat>(value->value.double_value);
#else
                result = TC_ERROR_NOT_SUPPORTED;
#endif
                break;
            }
            case TC_CONTROL_LIGHT_ENABLED:
                if (!device.hasTorch || ![device isTorchModeSupported:AVCaptureTorchModeOn]) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_BOOL)) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else if (value->value.boolean_value != 0) {
                    if (![device setTorchModeOnWithLevel:
                            std::min(0.5f, AVCaptureMaxAvailableTorchLevel) error:&error]) {
                        result = TC_ERROR_PLATFORM;
                    }
                } else if ([device isTorchModeSupported:AVCaptureTorchModeOff]) {
                    device.torchMode = AVCaptureTorchModeOff;
                } else result = TC_ERROR_NOT_SUPPORTED;
                break;
            case TC_CONTROL_LIGHT_LEVEL:
                if (!device.hasTorch || ![device isTorchModeSupported:AVCaptureTorchModeOn]) {
                    result = TC_ERROR_NOT_SUPPORTED;
                } else if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                    !std::isfinite(value->value.double_value) || value->value.double_value < 0.0 ||
                    value->value.double_value > AVCaptureMaxAvailableTorchLevel) {
                    result = TC_ERROR_INVALID_ARGUMENT;
                } else if (value->value.double_value == 0.0) {
                    device.torchMode = AVCaptureTorchModeOff;
                } else if (![device setTorchModeOnWithLevel:
                        static_cast<float>(value->value.double_value) error:&error]) {
                    result = TC_ERROR_PLATFORM;
                }
                break;
            default:
                result = TC_ERROR_NOT_SUPPORTED;
                break;
        }
    } @catch (NSException*) {
        result = TC_ERROR_PLATFORM;
    }

    [device unlockForConfiguration];
    return result;
}

} // namespace

@implementation TCFrameDelegate

- (void)captureOutput:(AVCaptureOutput*)output
 didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer
        fromConnection:(AVCaptureConnection*)connection {
    (void)output;
    (void)connection;

    tc_camera* camera = owner;
    if (!camera || !camera->running.load(std::memory_order_acquire)) return;

    CVImageBufferRef image_buffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (!image_buffer || CFGetTypeID(image_buffer) != CVPixelBufferGetTypeID()) return;

    CVPixelBufferRef pixel_buffer = (CVPixelBufferRef)image_buffer;
    if (CVPixelBufferLockBaseAddress(pixel_buffer, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) {
        return;
    }

    void* base_address = CVPixelBufferGetBaseAddress(pixel_buffer);
    const size_t width = CVPixelBufferGetWidth(pixel_buffer);
    const size_t height = CVPixelBufferGetHeight(pixel_buffer);
    const size_t stride = CVPixelBufferGetBytesPerRow(pixel_buffer);

    if (base_address && width > 0 && height > 0 && stride >= width * 4) {
        const CMTime presentation_time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
        const double seconds = CMTimeGetSeconds(presentation_time);

        tc_frame frame{};
        frame.struct_size = sizeof(tc_frame);
        frame.data = static_cast<const uint8_t*>(base_address);
        frame.data_length = stride * height;
        frame.width = static_cast<int32_t>(width);
        frame.height = static_cast<int32_t>(height);
        frame.stride = static_cast<int32_t>(stride);
        frame.pixel_format = TC_PIXEL_BGRA32;
        frame.rotation_degrees = 0;
        frame.mirrored = camera->mirrored;
        frame.timestamp_microseconds = std::isfinite(seconds)
            ? static_cast<int64_t>(seconds * 1000000.0)
            : thincam::monotonic_microseconds();

        if (camera->frame_callback) camera->frame_callback(&frame, camera->user_data);
    }

    CVPixelBufferUnlockBaseAddress(pixel_buffer, kCVPixelBufferLock_ReadOnly);
}

- (void)captureOutput:(AVCaptureOutput*)output
 didDropSampleBuffer:(CMSampleBufferRef)sampleBuffer
        fromConnection:(AVCaptureConnection*)connection {
    (void)output;
    (void)sampleBuffer;
    (void)connection;
}

@end

tc_status TC_CALL tc_get_permission_status(tc_permission_status* status) {
    if (!status) return TC_ERROR_INVALID_ARGUMENT;

    try {
        @autoreleasepool {
            @try {
                *status = map_permission(
                    [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo]);
                return TC_OK;
            } @catch (NSException* exception) {
                (void)exception;
                *status = TC_PERMISSION_UNKNOWN;
                return TC_ERROR_PLATFORM;
            }
        }
    } catch (...) {
        *status = TC_PERMISSION_UNKNOWN;
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_request_permission(tc_permission_callback callback, void* user_data) {
    try {
        @autoreleasepool {
            @try {
                AVAuthorizationStatus current =
                    [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo];
                if (current != AVAuthorizationStatusNotDetermined) {
                    if (callback) callback(map_permission(current), user_data);
                    return TC_OK;
                }

                [AVCaptureDevice requestAccessForMediaType:AVMediaTypeVideo
                                         completionHandler:^(BOOL granted) {
                    if (callback) {
                        callback(
                            granted ? TC_PERMISSION_GRANTED : TC_PERMISSION_DENIED,
                            user_data);
                    }
                }];
                return TC_OK;
            } @catch (NSException* exception) {
                (void)exception;
                return TC_ERROR_PLATFORM;
            }
        }
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_enumerate_devices(tc_device_callback callback, void* user_data) {
    if (!callback) return TC_ERROR_INVALID_ARGUMENT;

    try {
        @autoreleasepool {
            @try {
                AVCaptureDevice* default_device =
                    [AVCaptureDevice defaultDeviceWithMediaType:AVMediaTypeVideo];

                for (AVCaptureDevice* device in
                        [AVCaptureDevice devicesWithMediaType:AVMediaTypeVideo]) {
                    const std::string id = utf8(device.uniqueID);
                    const std::string name = utf8(device.localizedName);
                    if (id.empty()) continue;

                    tc_device_info info{};
                    info.struct_size = sizeof(tc_device_info);
                    info.id = id.c_str();
                    info.name = name.empty() ? id.c_str() : name.c_str();
                    info.position = map_position(device.position);
                    info.is_default = default_device &&
                        [device.uniqueID isEqualToString:default_device.uniqueID] ? 1 : 0;
                    callback(&info, user_data);
                }

                return TC_OK;
            } @catch (NSException* exception) {
                (void)exception;
                return TC_ERROR_PLATFORM;
            }
        }
    } catch (...) {
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

        if (instance->options.pixel_format != TC_PIXEL_BGRA32) {
            delete instance;
            return TC_ERROR_FORMAT_NOT_SUPPORTED;
        }

        @autoreleasepool {
            @try {
                if ([AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo] !=
                    AVAuthorizationStatusAuthorized) {
                    delete instance;
                    return TC_ERROR_PERMISSION_DENIED;
                }

                AVCaptureDevice* device = find_device(instance->device_id);
                if (!device) {
                    delete instance;
                    return TC_ERROR_DEVICE_NOT_FOUND;
                }

                NSError* error = nil;
                AVCaptureDeviceInput* input =
                    [AVCaptureDeviceInput deviceInputWithDevice:device error:&error];
                if (!input) {
                    const tc_status result =
                        error.code == AVErrorApplicationIsNotAuthorizedToUseDevice
                            ? TC_ERROR_PERMISSION_DENIED
                            : TC_ERROR_DEVICE_BUSY;
                    delete instance;
                    return result;
                }

                select_best_format(device, instance->options);

                AVCaptureSession* session = [[AVCaptureSession alloc] init];
                AVCaptureVideoDataOutput* output = [[AVCaptureVideoDataOutput alloc] init];
                output.alwaysDiscardsLateVideoFrames = YES;
                output.videoSettings = @{
                    (NSString*)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)
                };

                [session beginConfiguration];
                if (![session canAddInput:input] || ![session canAddOutput:output]) {
                    [session commitConfiguration];
                    delete instance;
                    return TC_ERROR_FORMAT_NOT_SUPPORTED;
                }

                [session addInput:input];
                [session addOutput:output];
                [session commitConfiguration];

                dispatch_queue_t queue =
                    dispatch_queue_create("dev.thincam.frames", DISPATCH_QUEUE_SERIAL);
                if (!queue) {
                    delete instance;
                    return TC_ERROR_PLATFORM;
                }
                dispatch_queue_set_specific(queue, callback_queue_key, instance, nullptr);

                TCFrameDelegate* delegate = [[TCFrameDelegate alloc] init];
                delegate->owner = instance;
                [output setSampleBufferDelegate:delegate queue:queue];

                instance->session = session;
                instance->device = device;
                instance->output = output;
                instance->delegate = delegate;
                instance->callback_queue = queue;
                instance->mirrored = device.position == AVCaptureDevicePositionFront ? 1 : 0;
            } @catch (NSException* exception) {
                (void)exception;
                delete instance;
                return TC_ERROR_PLATFORM;
            }
        }
    } catch (...) {
        delete instance;
        return TC_ERROR_PLATFORM;
    }

    *camera = instance;
    return TC_OK;
}

tc_status TC_CALL tc_camera_start(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        if (camera->running.load(std::memory_order_acquire)) {
            return TC_ERROR_ALREADY_RUNNING;
        }
        if (!camera->session || !camera->output || !camera->delegate || !camera->callback_queue) {
            return TC_ERROR_PLATFORM;
        }

        @try {
            [camera->output setSampleBufferDelegate:camera->delegate queue:camera->callback_queue];
            camera->running.store(true, std::memory_order_release);
            [camera->session startRunning];
            if (!camera->session.isRunning) {
                camera->running.store(false, std::memory_order_release);
                [camera->output setSampleBufferDelegate:nil queue:nullptr];
                return TC_ERROR_DEVICE_BUSY;
            }
            return TC_OK;
        } @catch (NSException* exception) {
            (void)exception;
            camera->running.store(false, std::memory_order_release);
            return TC_ERROR_PLATFORM;
        }
    } catch (...) {
        camera->running.store(false, std::memory_order_release);
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_stop(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        camera->running.store(false, std::memory_order_release);

        @try {
            if (camera->output) {
                [camera->output setSampleBufferDelegate:nil queue:nullptr];
            }
            if (camera->session.isRunning) {
                [camera->session stopRunning];
            }

            if (camera->callback_queue && dispatch_get_specific(callback_queue_key) != camera) {
                dispatch_sync(camera->callback_queue, ^{});
            }
            return TC_OK;
        } @catch (NSException* exception) {
            (void)exception;
            return TC_ERROR_PLATFORM;
        }
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

void TC_CALL tc_camera_close(tc_camera* camera) {
    if (!camera) return;

    try {
        (void)tc_camera_stop(camera);
        @autoreleasepool {
            @try {
                if (camera->delegate) camera->delegate->owner = nullptr;
                camera->delegate = nil;
                camera->output = nil;
                camera->device = nil;
                camera->session = nil;
                camera->callback_queue = nil;
            } @catch (NSException* exception) {
                (void)exception;
            }
        }
        delete camera;
    } catch (...) {
        // A close function must never throw across the C ABI boundary.
    }
}


tc_status TC_CALL tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info) {

    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        @autoreleasepool {
            @try {
                return apple_control_info(camera, id, info);
            } @catch (NSException*) {
                return TC_ERROR_PLATFORM;
            }
        }
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
        @autoreleasepool {
            @try {
                return apple_get_control(camera, id, value);
            } @catch (NSException*) {
                return TC_ERROR_PLATFORM;
            }
        }
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
        @autoreleasepool {
            @try {
                return apple_set_control(camera, value);
            } @catch (NSException*) {
                return TC_ERROR_PLATFORM;
            }
        }
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}
