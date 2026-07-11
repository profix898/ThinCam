#include "thincam.h"

uint32_t TC_CALL tc_get_abi_version(void) {
    return TC_ABI_VERSION;
}

const char* TC_CALL tc_status_message(tc_status status) {
    switch (status) {
        case TC_OK: return "Success";
        case TC_ERROR_INVALID_ARGUMENT: return "Invalid argument";
        case TC_ERROR_NOT_SUPPORTED: return "Not supported";
        case TC_ERROR_PERMISSION_DENIED: return "Camera permission denied";
        case TC_ERROR_DEVICE_NOT_FOUND: return "Camera device not found";
        case TC_ERROR_DEVICE_BUSY: return "Camera device is busy";
        case TC_ERROR_FORMAT_NOT_SUPPORTED: return "Camera format not supported";
        case TC_ERROR_NOT_RUNNING: return "Camera is not running";
        case TC_ERROR_ALREADY_RUNNING: return "Camera is already running";
        case TC_ERROR_PLATFORM: return "Platform camera error";
        case TC_ERROR_TIMEOUT: return "Camera operation timed out";
        case TC_ERROR_CANCELLED: return "Camera operation cancelled";
        default: return "Unknown ThinCam error";
    }
}
