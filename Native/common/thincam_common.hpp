#ifndef THINCAM_COMMON_HPP
#define THINCAM_COMMON_HPP

#include "thincam.h"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace thincam {

static_assert(sizeof(void*) == 8, "ThinCam native backends currently support 64-bit targets only.");
static_assert(sizeof(tc_device_info) == 32, "Unexpected tc_device_info ABI layout.");
static_assert(sizeof(tc_open_options) == 20, "Unexpected tc_open_options ABI layout.");
static_assert(sizeof(tc_frame) == 56, "Unexpected tc_frame ABI layout.");
static_assert(sizeof(tc_control_info) == 56, "Unexpected tc_control_info ABI layout.");
static_assert(sizeof(tc_control_value) == 24, "Unexpected tc_control_value ABI layout.");

inline int64_t monotonic_microseconds() {
    using namespace std::chrono;
    return duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
}

inline uint8_t clamp_byte(int value) {
    return static_cast<uint8_t>(std::clamp(value, 0, 255));
}

inline void yuv_to_bgra(uint8_t y, uint8_t u, uint8_t v, uint8_t* output) {
    const int c = static_cast<int>(y) - 16;
    const int d = static_cast<int>(u) - 128;
    const int e = static_cast<int>(v) - 128;

    const int r = (298 * c + 409 * e + 128) >> 8;
    const int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
    const int b = (298 * c + 516 * d + 128) >> 8;

    output[0] = clamp_byte(b);
    output[1] = clamp_byte(g);
    output[2] = clamp_byte(r);
    output[3] = 255;
}

inline void yuyv_to_bgra(
    const uint8_t* source,
    int width,
    int height,
    int source_stride,
    uint8_t* destination,
    int destination_stride,
    bool uyvy = false) {

    for (int y = 0; y < height; ++y) {
        const uint8_t* input = source + static_cast<size_t>(y) * source_stride;
        uint8_t* output = destination + static_cast<size_t>(y) * destination_stride;

        for (int x = 0; x < width; x += 2) {
            uint8_t y0;
            uint8_t u;
            uint8_t y1;
            uint8_t v;

            if (uyvy) {
                u = input[0];
                y0 = input[1];
                v = input[2];
                y1 = input[3];
            } else {
                y0 = input[0];
                u = input[1];
                y1 = input[2];
                v = input[3];
            }

            yuv_to_bgra(y0, u, v, output);
            if (x + 1 < width) {
                yuv_to_bgra(y1, u, v, output + 4);
            }

            input += 4;
            output += 8;
        }
    }
}

inline void yuv420_888_to_bgra(
    const uint8_t* y_plane,
    int y_length,
    int y_row_stride,
    int y_pixel_stride,
    const uint8_t* u_plane,
    int u_length,
    int u_row_stride,
    int u_pixel_stride,
    const uint8_t* v_plane,
    int v_length,
    int v_row_stride,
    int v_pixel_stride,
    int width,
    int height,
    uint8_t* destination,
    int destination_stride) {

    if (!y_plane || !u_plane || !v_plane || !destination) {
        return;
    }

    for (int row = 0; row < height; ++row) {
        uint8_t* output = destination + static_cast<size_t>(row) * destination_stride;
        const int chroma_row = row / 2;

        for (int column = 0; column < width; ++column) {
            const int chroma_column = column / 2;
            const int y_index = row * y_row_stride + column * y_pixel_stride;
            const int u_index = chroma_row * u_row_stride + chroma_column * u_pixel_stride;
            const int v_index = chroma_row * v_row_stride + chroma_column * v_pixel_stride;

            const uint8_t y_value = y_index >= 0 && y_index < y_length ? y_plane[y_index] : 16;
            const uint8_t u_value = u_index >= 0 && u_index < u_length ? u_plane[u_index] : 128;
            const uint8_t v_value = v_index >= 0 && v_index < v_length ? v_plane[v_index] : 128;

            yuv_to_bgra(y_value, u_value, v_value, output + static_cast<size_t>(column) * 4);
        }
    }
}


inline tc_control_info control_info(
    tc_control_id id,
    tc_control_value_type value_type,
    uint32_t flags,
    double minimum = 0.0,
    double maximum = 0.0,
    double step = 0.0,
    double default_value = 0.0,
    uint64_t supported_enum_values = 0) {

    tc_control_info info{};
    info.struct_size = sizeof(tc_control_info);
    info.id = id;
    info.value_type = value_type;
    info.flags = flags;
    info.minimum = minimum;
    info.maximum = maximum;
    info.step = step;
    info.default_value = default_value;
    info.supported_enum_values = supported_enum_values;
    return info;
}

inline tc_control_value bool_control(tc_control_id id, bool value) {
    tc_control_value result{};
    result.struct_size = sizeof(tc_control_value);
    result.id = id;
    result.value_type = TC_CONTROL_VALUE_BOOL;
    result.value.boolean_value = value ? 1 : 0;
    return result;
}

inline tc_control_value enum_control(tc_control_id id, int32_t value) {
    tc_control_value result{};
    result.struct_size = sizeof(tc_control_value);
    result.id = id;
    result.value_type = TC_CONTROL_VALUE_ENUM;
    result.value.enum_value = value;
    return result;
}

inline tc_control_value int64_control(tc_control_id id, int64_t value) {
    tc_control_value result{};
    result.struct_size = sizeof(tc_control_value);
    result.id = id;
    result.value_type = TC_CONTROL_VALUE_INT64;
    result.value.integer_value = value;
    return result;
}

inline tc_control_value double_control(tc_control_id id, double value) {
    tc_control_value result{};
    result.struct_size = sizeof(tc_control_value);
    result.id = id;
    result.value_type = TC_CONTROL_VALUE_DOUBLE;
    result.value.double_value = value;
    return result;
}

inline bool control_value_matches(const tc_control_value* value, tc_control_value_type expected) {
    return value &&
        value->struct_size >= sizeof(tc_control_value) &&
        value->value_type == expected;
}

inline uint64_t enum_flag(int32_t value) {
    return value > 0 && value < 64 ? (uint64_t{1} << static_cast<uint32_t>(value)) : 0;
}

inline tc_open_options normalized_options(const tc_open_options* options) {
    tc_open_options result{};
    result.struct_size = sizeof(tc_open_options);
    result.width = 640;
    result.height = 480;
    result.frames_per_second = 30;
    result.pixel_format = TC_PIXEL_BGRA32;

    if (options) {
        if (options->width > 0) result.width = options->width;
        if (options->height > 0) result.height = options->height;
        if (options->frames_per_second > 0) result.frames_per_second = options->frames_per_second;
        if (options->pixel_format != 0) result.pixel_format = options->pixel_format;
    }

    return result;
}

inline void report_error(
    tc_error_callback callback,
    void* user_data,
    tc_status status,
    const std::string& message,
    bool fatal) {

    if (callback) {
        callback(status, message.c_str(), fatal ? 1 : 0, user_data);
    }
}

} // namespace thincam

#endif
