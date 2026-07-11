#include "thincam.h"
#include "thincam_common.hpp"

#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <poll.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#include <atomic>
#include <cmath>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

int xioctl(int fd, unsigned long request, void* argument) {
    int result;
    do {
        result = ioctl(fd, request, argument);
    } while (result == -1 && errno == EINTR);
    return result;
}

bool is_video_capture_device(const std::string& path, v4l2_capability* capability = nullptr) {
    const int fd = open(path.c_str(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) {
        return false;
    }

    v4l2_capability local{};
    const bool queried = xioctl(fd, VIDIOC_QUERYCAP, &local) == 0;
    close(fd);

    if (!queried) {
        return false;
    }

    const uint32_t caps = (local.capabilities & V4L2_CAP_DEVICE_CAPS)
        ? local.device_caps
        : local.capabilities;

    const bool supported =
        (caps & V4L2_CAP_VIDEO_CAPTURE) != 0 &&
        (caps & V4L2_CAP_STREAMING) != 0;

    if (supported && capability) {
        *capability = local;
    }

    return supported;
}

std::string errno_message(const char* operation) {
    return std::string(operation) + ": " + std::strerror(errno);
}

struct mapped_buffer {
    void* data = MAP_FAILED;
    size_t length = 0;
};

} // namespace

struct tc_camera {
    std::string device_id;
    tc_open_options options{};
    tc_frame_callback frame_callback = nullptr;
    tc_error_callback error_callback = nullptr;
    void* user_data = nullptr;

    int fd = -1;
    uint32_t v4l2_format = 0;
    int width = 0;
    int height = 0;
    int source_stride = 0;
    std::vector<mapped_buffer> buffers;
    std::vector<uint8_t> bgra;

    std::atomic<bool> running{false};
    std::atomic<bool> stop_requested{false};
    std::thread worker;
    std::mutex lifecycle_mutex;
    std::mutex control_mutex;
    std::atomic<int32_t> exposure_mode_state{TC_EXPOSURE_MODE_AUTO};
    std::atomic<int32_t> focus_mode_state{TC_FOCUS_MODE_MANUAL};
};

namespace {

void release_resources(tc_camera* camera) {
    if (!camera) return;

    for (mapped_buffer& buffer : camera->buffers) {
        if (buffer.data != MAP_FAILED) {
            munmap(buffer.data, buffer.length);
            buffer.data = MAP_FAILED;
        }
    }
    camera->buffers.clear();

    if (camera->fd >= 0) {
        close(camera->fd);
        camera->fd = -1;
    }
}

tc_status configure_format(tc_camera* camera) {
    const uint32_t candidates[] = {V4L2_PIX_FMT_YUYV, V4L2_PIX_FMT_UYVY};

    for (const uint32_t candidate : candidates) {
        v4l2_format format{};
        format.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        format.fmt.pix.width = static_cast<uint32_t>(std::max(2, camera->options.width & ~1));
        format.fmt.pix.height = static_cast<uint32_t>(std::max(2, camera->options.height));
        format.fmt.pix.pixelformat = candidate;
        format.fmt.pix.field = V4L2_FIELD_ANY;

        if (xioctl(camera->fd, VIDIOC_S_FMT, &format) != 0) {
            continue;
        }

        if (format.fmt.pix.pixelformat != candidate) {
            continue;
        }

        camera->v4l2_format = candidate;
        camera->width = static_cast<int>(format.fmt.pix.width);
        camera->height = static_cast<int>(format.fmt.pix.height);
        if (camera->width <= 0 || camera->height <= 0 || (camera->width & 1) != 0) {
            continue;
        }
        camera->source_stride = std::max(
            static_cast<int>(format.fmt.pix.bytesperline),
            camera->width * 2);

        try {
            camera->bgra.resize(
                static_cast<size_t>(camera->width) *
                static_cast<size_t>(camera->height) * 4);
        } catch (...) {
            return TC_ERROR_PLATFORM;
        }

        return TC_OK;
    }

    return TC_ERROR_FORMAT_NOT_SUPPORTED;
}

tc_status configure_frame_rate(tc_camera* camera) {
    v4l2_streamparm parameters{};
    parameters.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    parameters.parm.capture.timeperframe.numerator = 1;
    parameters.parm.capture.timeperframe.denominator =
        static_cast<uint32_t>(std::max(1, camera->options.frames_per_second));

    if (xioctl(camera->fd, VIDIOC_S_PARM, &parameters) != 0) {
        // Many drivers do not implement frame-rate selection. Capture can still proceed.
        return TC_OK;
    }

    return TC_OK;
}

tc_status allocate_buffers(tc_camera* camera) {
    v4l2_requestbuffers request{};
    request.count = 4;
    request.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    request.memory = V4L2_MEMORY_MMAP;

    if (xioctl(camera->fd, VIDIOC_REQBUFS, &request) != 0) {
        return errno == EACCES ? TC_ERROR_PERMISSION_DENIED : TC_ERROR_PLATFORM;
    }

    if (request.count < 2) {
        return TC_ERROR_PLATFORM;
    }

    camera->buffers.resize(request.count);

    for (uint32_t index = 0; index < request.count; ++index) {
        v4l2_buffer buffer{};
        buffer.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buffer.memory = V4L2_MEMORY_MMAP;
        buffer.index = index;

        if (xioctl(camera->fd, VIDIOC_QUERYBUF, &buffer) != 0) {
            return TC_ERROR_PLATFORM;
        }

        void* mapped = mmap(
            nullptr,
            buffer.length,
            PROT_READ | PROT_WRITE,
            MAP_SHARED,
            camera->fd,
            static_cast<off_t>(buffer.m.offset));

        if (mapped == MAP_FAILED) {
            return TC_ERROR_PLATFORM;
        }

        camera->buffers[index].data = mapped;
        camera->buffers[index].length = buffer.length;
    }

    return TC_OK;
}

tc_status queue_all_buffers(tc_camera* camera) {
    for (uint32_t index = 0; index < camera->buffers.size(); ++index) {
        v4l2_buffer buffer{};
        buffer.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buffer.memory = V4L2_MEMORY_MMAP;
        buffer.index = index;

        if (xioctl(camera->fd, VIDIOC_QBUF, &buffer) != 0) {
            return TC_ERROR_PLATFORM;
        }
    }

    return TC_OK;
}

void capture_loop(tc_camera* camera) {
    try {
        while (!camera->stop_requested.load(std::memory_order_acquire)) {
        pollfd descriptor{};
        descriptor.fd = camera->fd;
        descriptor.events = POLLIN | POLLPRI;

        const int poll_result = poll(&descriptor, 1, 250);
        if (poll_result < 0) {
            if (errno == EINTR) continue;
            thincam::report_error(
                camera->error_callback,
                camera->user_data,
                TC_ERROR_PLATFORM,
                errno_message("poll"),
                true);
            break;
        }

        if (poll_result == 0) {
            continue;
        }

        if ((descriptor.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            thincam::report_error(
                camera->error_callback,
                camera->user_data,
                TC_ERROR_PLATFORM,
                "The V4L2 device was disconnected or reported an error.",
                true);
            break;
        }

        v4l2_buffer buffer{};
        buffer.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buffer.memory = V4L2_MEMORY_MMAP;

        if (xioctl(camera->fd, VIDIOC_DQBUF, &buffer) != 0) {
            if (errno == EAGAIN) continue;
            thincam::report_error(
                camera->error_callback,
                camera->user_data,
                TC_ERROR_PLATFORM,
                errno_message("VIDIOC_DQBUF"),
                true);
            break;
        }

        if (buffer.index < camera->buffers.size()) {
            const mapped_buffer& mapped = camera->buffers[buffer.index];
            const size_t minimum_size =
                static_cast<size_t>(camera->source_stride) * camera->height;

            if (mapped.data != MAP_FAILED &&
                mapped.length >= minimum_size &&
                static_cast<size_t>(buffer.bytesused) >= minimum_size) {
                thincam::yuyv_to_bgra(
                    static_cast<const uint8_t*>(mapped.data),
                    camera->width,
                    camera->height,
                    camera->source_stride,
                    camera->bgra.data(),
                    camera->width * 4,
                    camera->v4l2_format == V4L2_PIX_FMT_UYVY);

                tc_frame frame{};
                frame.struct_size = sizeof(tc_frame);
                frame.data = camera->bgra.data();
                frame.data_length = camera->bgra.size();
                frame.width = camera->width;
                frame.height = camera->height;
                frame.stride = camera->width * 4;
                frame.pixel_format = TC_PIXEL_BGRA32;
                frame.rotation_degrees = 0;
                frame.mirrored = 0;
                frame.timestamp_microseconds = thincam::monotonic_microseconds();

                if (camera->frame_callback) {
                    camera->frame_callback(&frame, camera->user_data);
                }
            }
        }

            if (xioctl(camera->fd, VIDIOC_QBUF, &buffer) != 0) {
                thincam::report_error(
                    camera->error_callback,
                    camera->user_data,
                    TC_ERROR_PLATFORM,
                    errno_message("VIDIOC_QBUF"),
                    true);
                break;
            }
        }
    } catch (...) {
        if (camera->error_callback) {
            camera->error_callback(
                TC_ERROR_PLATFORM,
                "Unhandled error in the V4L2 capture loop.",
                1,
                camera->user_data);
        }
    }

    camera->running.store(false, std::memory_order_release);
}


bool query_control(int fd, uint32_t id, v4l2_query_ext_ctrl* query) {
    if (!query) return false;
    *query = {};
    query->id = id;
    if (xioctl(fd, VIDIOC_QUERY_EXT_CTRL, query) != 0) return false;
    return (query->flags & V4L2_CTRL_FLAG_DISABLED) == 0;
}

uint32_t mapped_flags(const v4l2_query_ext_ctrl& query) {
    uint32_t flags = TC_CONTROL_FLAG_READABLE;
    if ((query.flags & V4L2_CTRL_FLAG_READ_ONLY) == 0 &&
        (query.flags & V4L2_CTRL_FLAG_INACTIVE) == 0) {
        flags |= TC_CONTROL_FLAG_WRITABLE;
    }
    return flags;
}

bool menu_item_supported(int fd, uint32_t id, uint32_t index, int64_t* value = nullptr) {
    v4l2_querymenu menu{};
    menu.id = id;
    menu.index = index;
    if (xioctl(fd, VIDIOC_QUERYMENU, &menu) != 0) return false;
    if (value) {
        *value = menu.value;
    }
    return true;
}

tc_status get_integer_control(int fd, uint32_t id, int32_t* value) {
    if (!value) return TC_ERROR_INVALID_ARGUMENT;
    v4l2_control control{};
    control.id = id;
    if (xioctl(fd, VIDIOC_G_CTRL, &control) != 0) {
        return errno == EACCES || errno == EPERM
            ? TC_ERROR_PERMISSION_DENIED
            : TC_ERROR_PLATFORM;
    }
    *value = control.value;
    return TC_OK;
}

tc_status set_integer_control(int fd, uint32_t id, int32_t value) {
    v4l2_control control{};
    control.id = id;
    control.value = value;
    if (xioctl(fd, VIDIOC_S_CTRL, &control) != 0) {
        if (errno == ERANGE || errno == EINVAL) return TC_ERROR_INVALID_ARGUMENT;
        if (errno == EACCES || errno == EPERM) return TC_ERROR_PERMISSION_DENIED;
        if (errno == EBUSY) return TC_ERROR_DEVICE_BUSY;
        return TC_ERROR_PLATFORM;
    }
    return TC_OK;
}

double normalize_value(int32_t value, const v4l2_query_ext_ctrl& query, bool reverse = false) {
    if (query.maximum <= query.minimum) return 0.0;
    double result = static_cast<double>(value - query.minimum) /
        static_cast<double>(query.maximum - query.minimum);
    result = std::clamp(result, 0.0, 1.0);
    return reverse ? 1.0 - result : result;
}

int32_t denormalize_value(double value, const v4l2_query_ext_ctrl& query, bool reverse = false) {
    value = std::clamp(value, 0.0, 1.0);
    if (reverse) value = 1.0 - value;
    const double raw = static_cast<double>(query.minimum) +
        value * static_cast<double>(query.maximum - query.minimum);
    const int64_t step = std::max<int64_t>(1, query.step);
    const int64_t snapped = query.minimum +
        static_cast<int64_t>(std::llround((raw - query.minimum) / step)) * step;
    return static_cast<int32_t>(std::clamp<int64_t>(snapped, query.minimum, query.maximum));
}

bool raw_value_supported(int64_t value, const v4l2_query_ext_ctrl& query) {
    if (value < query.minimum || value > query.maximum) return false;
    const int64_t step = std::max<int64_t>(1, query.step);
    return (value - query.minimum) % step == 0;
}

bool exposure_menu_supported(int fd, const v4l2_query_ext_ctrl& query, uint32_t index) {
    return index >= static_cast<uint32_t>(query.minimum) &&
        index <= static_cast<uint32_t>(query.maximum) &&
        menu_item_supported(fd, V4L2_CID_EXPOSURE_AUTO, index);
}

int32_t preferred_auto_exposure_value(int fd, const v4l2_query_ext_ctrl& query) {
    if (exposure_menu_supported(fd, query, V4L2_EXPOSURE_AUTO)) {
        return V4L2_EXPOSURE_AUTO;
    }
    if (exposure_menu_supported(fd, query, V4L2_EXPOSURE_APERTURE_PRIORITY)) {
        return V4L2_EXPOSURE_APERTURE_PRIORITY;
    }
    if (exposure_menu_supported(fd, query, V4L2_EXPOSURE_SHUTTER_PRIORITY)) {
        return V4L2_EXPOSURE_SHUTTER_PRIORITY;
    }
    return -1;
}

uint64_t exposure_mode_mask(int fd, const v4l2_query_ext_ctrl& query) {
    uint64_t mask = 0;
    const auto supported = [&](uint32_t index) {
        return exposure_menu_supported(fd, query, index);
    };
    if (supported(V4L2_EXPOSURE_AUTO) ||
        supported(V4L2_EXPOSURE_SHUTTER_PRIORITY) ||
        supported(V4L2_EXPOSURE_APERTURE_PRIORITY)) {
        mask |= thincam::enum_flag(TC_EXPOSURE_MODE_AUTO);
    }
    if (supported(V4L2_EXPOSURE_MANUAL)) {
        mask |= thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL);
        v4l2_query_ext_ctrl duration{};
        if (query_control(fd, V4L2_CID_EXPOSURE_ABSOLUTE, &duration)) {
            mask |= thincam::enum_flag(TC_EXPOSURE_MODE_LOCKED);
        }
    }
    return mask;
}

uint64_t focus_mode_mask(int fd) {
    uint64_t mask = 0;
    v4l2_query_ext_ctrl automatic{};
    v4l2_query_ext_ctrl position{};
    const bool has_auto = query_control(fd, V4L2_CID_FOCUS_AUTO, &automatic);
    const bool has_position = query_control(fd, V4L2_CID_FOCUS_ABSOLUTE, &position);
    if (has_auto) mask |= thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO);
    if (has_position) mask |= thincam::enum_flag(TC_FOCUS_MODE_MANUAL);
    if (has_auto && has_position) mask |= thincam::enum_flag(TC_FOCUS_MODE_LOCKED);
    return mask;
}

bool exposure_bias_values(
    int fd,
    const v4l2_query_ext_ctrl& query,
    std::vector<std::pair<int32_t, double>>* values) {

    if (!values) return false;
    values->clear();
    if (query.type == V4L2_CTRL_TYPE_INTEGER_MENU) {
        for (int64_t index = query.minimum; index <= query.maximum; ++index) {
            int64_t menu_value = 0;
            if (menu_item_supported(fd, V4L2_CID_AUTO_EXPOSURE_BIAS,
                    static_cast<uint32_t>(index), &menu_value)) {
                values->push_back({static_cast<int32_t>(index), static_cast<double>(menu_value) / 1000.0});
            }
        }
    } else {
        for (int64_t raw = query.minimum; raw <= query.maximum; raw += std::max<int64_t>(1, query.step)) {
            values->push_back({static_cast<int32_t>(raw), static_cast<double>(raw) / 1000.0});
            if (values->size() > 10000) break;
        }
    }
    return !values->empty();
}

tc_status linux_control_info(tc_camera* camera, tc_control_id id, tc_control_info* info) {
    if (!camera || !info || camera->fd < 0) return TC_ERROR_INVALID_ARGUMENT;
    v4l2_query_ext_ctrl query{};

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, &query)) return TC_ERROR_NOT_SUPPORTED;
            const uint64_t modes = exposure_mode_mask(camera->fd, query);
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, mapped_flags(query), 0, 0, 0,
                query.default_value == V4L2_EXPOSURE_MANUAL
                    ? TC_EXPOSURE_MODE_MANUAL
                    : TC_EXPOSURE_MODE_AUTO,
                modes);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV: {
            if (!query_control(camera->fd, V4L2_CID_AUTO_EXPOSURE_BIAS, &query)) return TC_ERROR_NOT_SUPPORTED;
            std::vector<std::pair<int32_t, double>> values;
            if (!exposure_bias_values(camera->fd, query, &values)) return TC_ERROR_NOT_SUPPORTED;
            const auto [minimum, maximum] = std::minmax_element(
                values.begin(), values.end(),
                [](const auto& left, const auto& right) { return left.second < right.second; });
            double default_value = 0.0;
            for (const auto& item : values) {
                if (item.first == query.default_value) default_value = item.second;
            }
            double step = 0.0;
            if (values.size() > 1) {
                std::sort(values.begin(), values.end(), [](const auto& a, const auto& b) { return a.second < b.second; });
                const double candidate = values[1].second - values[0].second;
                const double tolerance = std::max(1e-9, std::abs(candidate) * 1e-6);
                bool uniform = true;
                for (std::size_t index = 2; index < values.size(); ++index) {
                    if (std::abs(values[index].second - values[index - 1].second - candidate) > tolerance) {
                        uniform = false;
                        break;
                    }
                }
                if (uniform) step = candidate;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, mapped_flags(query),
                minimum->second, maximum->second, step, default_value);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_DURATION_US:
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_INT64, mapped_flags(query),
                static_cast<double>(query.minimum) * 100.0,
                static_cast<double>(query.maximum) * 100.0,
                static_cast<double>(query.step) * 100.0,
                static_cast<double>(query.default_value) * 100.0);
            return TC_OK;
        case TC_CONTROL_EXPOSURE_ISO:
            if (!query_control(camera->fd, V4L2_CID_ISO_SENSITIVITY, &query)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, mapped_flags(query),
                static_cast<double>(query.minimum), static_cast<double>(query.maximum),
                static_cast<double>(query.step), static_cast<double>(query.default_value));
            return TC_OK;
        case TC_CONTROL_FOCUS_MODE: {
            const uint64_t modes = focus_mode_mask(camera->fd);
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            uint32_t flags = TC_CONTROL_FLAG_READABLE;
            v4l2_query_ext_ctrl auto_query{};
            v4l2_query_ext_ctrl position_query{};
            if ((query_control(camera->fd, V4L2_CID_FOCUS_AUTO, &auto_query) &&
                    (mapped_flags(auto_query) & TC_CONTROL_FLAG_WRITABLE)) ||
                (query_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &position_query) &&
                    (mapped_flags(position_query) & TC_CONTROL_FLAG_WRITABLE))) {
                flags |= TC_CONTROL_FLAG_WRITABLE;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, flags, 0, 0, 0,
                (modes & thincam::enum_flag(TC_FOCUS_MODE_CONTINUOUS_AUTO))
                    ? TC_FOCUS_MODE_CONTINUOUS_AUTO
                    : TC_FOCUS_MODE_MANUAL,
                modes);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION:
            if (!query_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, mapped_flags(query),
                0.0, 1.0,
                query.maximum > query.minimum
                    ? static_cast<double>(std::max<int64_t>(1, query.step)) /
                        static_cast<double>(query.maximum - query.minimum)
                    : 0.0,
                normalize_value(static_cast<int32_t>(query.default_value), query, true));
            return TC_OK;
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!query_control(camera->fd, V4L2_CID_ZOOM_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            const double base = query.default_value > 0
                ? static_cast<double>(query.default_value)
                : std::max(1.0, static_cast<double>(query.minimum));
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, mapped_flags(query),
                std::max(0.01, static_cast<double>(query.minimum) / base),
                std::max(0.01, static_cast<double>(query.maximum) / base),
                static_cast<double>(std::max<int64_t>(1, query.step)) / base,
                1.0);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            if (!query_control(camera->fd, V4L2_CID_FLASH_LED_MODE, &query) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_TORCH) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_NONE)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_BOOL, mapped_flags(query), 0, 1, 1, 0);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_LEVEL:
            if (!query_control(camera->fd, V4L2_CID_FLASH_TORCH_INTENSITY, &query)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, mapped_flags(query), 0.0, 1.0,
                query.maximum > query.minimum
                    ? static_cast<double>(std::max<int64_t>(1, query.step)) /
                        static_cast<double>(query.maximum - query.minimum)
                    : 0.0,
                normalize_value(static_cast<int32_t>(query.default_value), query));
            return TC_OK;
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status linux_get_control(tc_camera* camera, tc_control_id id, tc_control_value* value) {
    if (!camera || !value || camera->fd < 0) return TC_ERROR_INVALID_ARGUMENT;
    int32_t raw = 0;
    v4l2_query_ext_ctrl query{};

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, &query)) return TC_ERROR_NOT_SUPPORTED;
            const tc_status status = get_integer_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, &raw);
            if (status != TC_OK) return status;
            int32_t mode = raw == V4L2_EXPOSURE_MANUAL
                ? camera->exposure_mode_state.load(std::memory_order_acquire)
                : TC_EXPOSURE_MODE_AUTO;
            if (mode != TC_EXPOSURE_MODE_MANUAL && mode != TC_EXPOSURE_MODE_LOCKED) {
                mode = TC_EXPOSURE_MODE_MANUAL;
            }
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV: {
            if (!query_control(camera->fd, V4L2_CID_AUTO_EXPOSURE_BIAS, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_AUTO_EXPOSURE_BIAS, &raw);
            if (status != TC_OK) return status;
            std::vector<std::pair<int32_t, double>> values;
            if (!exposure_bias_values(camera->fd, query, &values)) return TC_ERROR_NOT_SUPPORTED;
            for (const auto& item : values) {
                if (item.first == raw) {
                    *value = thincam::double_control(id, item.second);
                    return TC_OK;
                }
            }
            return TC_ERROR_PLATFORM;
        }
        case TC_CONTROL_EXPOSURE_DURATION_US: {
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, &raw);
            if (status != TC_OK) return status;
            *value = thincam::int64_control(id, static_cast<int64_t>(raw) * 100);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_ISO: {
            if (!query_control(camera->fd, V4L2_CID_ISO_SENSITIVITY, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_ISO_SENSITIVITY, &raw);
            if (status != TC_OK) return status;
            *value = thincam::double_control(id, raw);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_MODE: {
            v4l2_query_ext_ctrl auto_query{};
            if (!query_control(camera->fd, V4L2_CID_FOCUS_AUTO, &auto_query)) {
                if (query_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &query)) {
                    *value = thincam::enum_control(id, TC_FOCUS_MODE_MANUAL);
                    return TC_OK;
                }
                return TC_ERROR_NOT_SUPPORTED;
            }
            tc_status status = get_integer_control(camera->fd, V4L2_CID_FOCUS_AUTO, &raw);
            if (status != TC_OK) return status;
            int32_t mode = raw != 0
                ? TC_FOCUS_MODE_CONTINUOUS_AUTO
                : camera->focus_mode_state.load(std::memory_order_acquire);
            if (mode != TC_FOCUS_MODE_MANUAL && mode != TC_FOCUS_MODE_LOCKED) {
                mode = TC_FOCUS_MODE_MANUAL;
            }
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION: {
            if (!query_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &raw);
            if (status != TC_OK) return status;
            *value = thincam::double_control(id, normalize_value(raw, query, true));
            return TC_OK;
        }
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!query_control(camera->fd, V4L2_CID_ZOOM_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_ZOOM_ABSOLUTE, &raw);
            if (status != TC_OK) return status;
            const double base = query.default_value > 0
                ? static_cast<double>(query.default_value)
                : std::max(1.0, static_cast<double>(query.minimum));
            *value = thincam::double_control(id, static_cast<double>(raw) / base);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            if (!query_control(camera->fd, V4L2_CID_FLASH_LED_MODE, &query) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_TORCH) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_NONE)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            tc_status status = get_integer_control(camera->fd, V4L2_CID_FLASH_LED_MODE, &raw);
            if (status != TC_OK) return status;
            *value = thincam::bool_control(id, raw == V4L2_FLASH_LED_MODE_TORCH);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_LEVEL: {
            if (!query_control(camera->fd, V4L2_CID_FLASH_TORCH_INTENSITY, &query)) return TC_ERROR_NOT_SUPPORTED;
            tc_status status = get_integer_control(camera->fd, V4L2_CID_FLASH_TORCH_INTENSITY, &raw);
            if (status != TC_OK) return status;
            *value = thincam::double_control(id, normalize_value(raw, query));
            return TC_OK;
        }
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status linux_set_control(tc_camera* camera, const tc_control_value* value) {
    if (!camera || !value || camera->fd < 0) return TC_ERROR_INVALID_ARGUMENT;
    v4l2_query_ext_ctrl query{};

    switch (value->id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, &query)) return TC_ERROR_NOT_SUPPORTED;
            const uint64_t modes = exposure_mode_mask(camera->fd, query);
            if (!(modes & thincam::enum_flag(value->value.enum_value))) return TC_ERROR_NOT_SUPPORTED;

            tc_status status = TC_OK;
            switch (value->value.enum_value) {
                case TC_EXPOSURE_MODE_AUTO: {
                    const int32_t automatic = preferred_auto_exposure_value(camera->fd, query);
                    if (automatic < 0) return TC_ERROR_NOT_SUPPORTED;
                    status = set_integer_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, automatic);
                    break;
                }
                case TC_EXPOSURE_MODE_MANUAL:
                    status = set_integer_control(camera->fd, V4L2_CID_EXPOSURE_AUTO, V4L2_EXPOSURE_MANUAL);
                    break;
                case TC_EXPOSURE_MODE_LOCKED: {
                    int32_t current = 0;
                    status = get_integer_control(camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, &current);
                    if (status == TC_OK) status = set_integer_control(
                        camera->fd, V4L2_CID_EXPOSURE_AUTO, V4L2_EXPOSURE_MANUAL);
                    if (status == TC_OK) status = set_integer_control(
                        camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, current);
                    break;
                }
                default:
                    return TC_ERROR_INVALID_ARGUMENT;
            }
            if (status == TC_OK) {
                camera->exposure_mode_state.store(value->value.enum_value, std::memory_order_release);
            }
            return status;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value)) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_AUTO_EXPOSURE_BIAS, &query)) return TC_ERROR_NOT_SUPPORTED;
            std::vector<std::pair<int32_t, double>> values;
            if (!exposure_bias_values(camera->fd, query, &values)) return TC_ERROR_NOT_SUPPORTED;
            auto closest = std::min_element(values.begin(), values.end(), [&](const auto& a, const auto& b) {
                return std::abs(a.second - value->value.double_value) <
                    std::abs(b.second - value->value.double_value);
            });
            if (closest == values.end()) return TC_ERROR_NOT_SUPPORTED;
            double smallest_step = 0.0;
            if (values.size() > 1) {
                std::sort(values.begin(), values.end(), [](const auto& a, const auto& b) { return a.second < b.second; });
                smallest_step = std::abs(values[1].second - values[0].second);
            }
            const double tolerance = std::max(1e-9, smallest_step * 1e-6);
            if (std::abs(closest->second - value->value.double_value) > tolerance) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            return set_integer_control(camera->fd, V4L2_CID_AUTO_EXPOSURE_BIAS, closest->first);
        }
        case TC_CONTROL_EXPOSURE_DURATION_US: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_INT64) ||
                value->value.integer_value <= 0) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            if (value->value.integer_value % 100 != 0) return TC_ERROR_INVALID_ARGUMENT;
            const int64_t raw = value->value.integer_value / 100;
            if (!raw_value_supported(raw, query)) return TC_ERROR_INVALID_ARGUMENT;
            const tc_status status = set_integer_control(
                camera->fd, V4L2_CID_EXPOSURE_ABSOLUTE, static_cast<int32_t>(raw));
            if (status == TC_OK) {
                camera->exposure_mode_state.store(TC_EXPOSURE_MODE_MANUAL, std::memory_order_release);
            }
            return status;
        }
        case TC_CONTROL_EXPOSURE_ISO: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value)) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_ISO_SENSITIVITY, &query)) return TC_ERROR_NOT_SUPPORTED;
            const int64_t raw = static_cast<int64_t>(std::llround(value->value.double_value));
            if (std::abs(value->value.double_value - static_cast<double>(raw)) > 1e-9 ||
                !raw_value_supported(raw, query)) return TC_ERROR_INVALID_ARGUMENT;
            return set_integer_control(camera->fd, V4L2_CID_ISO_SENSITIVITY, static_cast<int32_t>(raw));
        }
        case TC_CONTROL_FOCUS_MODE: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) return TC_ERROR_INVALID_ARGUMENT;
            const uint64_t modes = focus_mode_mask(camera->fd);
            if (!(modes & thincam::enum_flag(value->value.enum_value))) return TC_ERROR_NOT_SUPPORTED;

            tc_status status = TC_OK;
            switch (value->value.enum_value) {
                case TC_FOCUS_MODE_CONTINUOUS_AUTO:
                    status = set_integer_control(camera->fd, V4L2_CID_FOCUS_AUTO, 1);
                    break;
                case TC_FOCUS_MODE_MANUAL:
                    if (query_control(camera->fd, V4L2_CID_FOCUS_AUTO, &query)) {
                        status = set_integer_control(camera->fd, V4L2_CID_FOCUS_AUTO, 0);
                    }
                    break;
                case TC_FOCUS_MODE_LOCKED: {
                    int32_t current = 0;
                    status = get_integer_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &current);
                    if (status == TC_OK) status = set_integer_control(camera->fd, V4L2_CID_FOCUS_AUTO, 0);
                    if (status == TC_OK) status = set_integer_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, current);
                    break;
                }
                case TC_FOCUS_MODE_AUTO:
                    return TC_ERROR_NOT_SUPPORTED;
                default:
                    return TC_ERROR_INVALID_ARGUMENT;
            }
            if (status == TC_OK) {
                camera->focus_mode_state.store(value->value.enum_value, std::memory_order_release);
            }
            return status;
        }
        case TC_CONTROL_FOCUS_POSITION: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value < 0.0 ||
                value->value.double_value > 1.0) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_FOCUS_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            const tc_status status = set_integer_control(
                camera->fd, V4L2_CID_FOCUS_ABSOLUTE,
                denormalize_value(value->value.double_value, query, true));
            if (status == TC_OK) {
                camera->focus_mode_state.store(TC_FOCUS_MODE_MANUAL, std::memory_order_release);
            }
            return status;
        }
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value <= 0.0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!query_control(camera->fd, V4L2_CID_ZOOM_ABSOLUTE, &query)) return TC_ERROR_NOT_SUPPORTED;
            const double base = query.default_value > 0
                ? static_cast<double>(query.default_value)
                : std::max(1.0, static_cast<double>(query.minimum));
            const double raw_value = value->value.double_value * base;
            const int64_t raw = static_cast<int64_t>(std::llround(raw_value));
            if (std::abs(raw_value - static_cast<double>(raw)) > 1e-9 ||
                !raw_value_supported(raw, query)) return TC_ERROR_INVALID_ARGUMENT;
            return set_integer_control(camera->fd, V4L2_CID_ZOOM_ABSOLUTE, static_cast<int32_t>(raw));
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_BOOL)) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_FLASH_LED_MODE, &query) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_TORCH) ||
                !menu_item_supported(camera->fd, V4L2_CID_FLASH_LED_MODE, V4L2_FLASH_LED_MODE_NONE)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            return set_integer_control(
                camera->fd, V4L2_CID_FLASH_LED_MODE,
                value->value.boolean_value != 0
                    ? V4L2_FLASH_LED_MODE_TORCH
                    : V4L2_FLASH_LED_MODE_NONE);
        }
        case TC_CONTROL_LIGHT_LEVEL:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value < 0.0 ||
                value->value.double_value > 1.0) return TC_ERROR_INVALID_ARGUMENT;
            if (!query_control(camera->fd, V4L2_CID_FLASH_TORCH_INTENSITY, &query)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            return set_integer_control(
                camera->fd, V4L2_CID_FLASH_TORCH_INTENSITY,
                denormalize_value(value->value.double_value, query));
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

} // namespace

tc_status TC_CALL tc_get_permission_status(tc_permission_status* status) {
    if (!status) return TC_ERROR_INVALID_ARGUMENT;

    try {
        bool found_device = false;
        bool denied = false;

        for (int index = 0; index < 64; ++index) {
            const std::string path = "/dev/video" + std::to_string(index);
            struct stat info{};
            if (stat(path.c_str(), &info) != 0) continue;
            found_device = true;

            const int fd = open(path.c_str(), O_RDWR | O_NONBLOCK | O_CLOEXEC);
            if (fd >= 0) {
                close(fd);
                *status = TC_PERMISSION_GRANTED;
                return TC_OK;
            }
            if (errno == EACCES || errno == EPERM) denied = true;
        }

        *status = found_device && denied
            ? TC_PERMISSION_DENIED
            : TC_PERMISSION_GRANTED;
        return TC_OK;
    } catch (...) {
        *status = TC_PERMISSION_UNKNOWN;
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_request_permission(tc_permission_callback callback, void* user_data) {
    tc_permission_status status = TC_PERMISSION_UNKNOWN;
    const tc_status result = tc_get_permission_status(&status);
    if (callback) callback(status, user_data);
    return result;
}

tc_status TC_CALL tc_enumerate_devices(tc_device_callback callback, void* user_data) {
    if (!callback) return TC_ERROR_INVALID_ARGUMENT;

    try {
        int emitted = 0;
        for (int index = 0; index < 64; ++index) {
            const std::string path = "/dev/video" + std::to_string(index);
            v4l2_capability capability{};
            if (!is_video_capture_device(path, &capability)) continue;

            const std::string name(reinterpret_cast<const char*>(capability.card));
            tc_device_info device{};
            device.struct_size = sizeof(tc_device_info);
            device.id = path.c_str();
            device.name = name.empty() ? path.c_str() : name.c_str();
            device.position = TC_POSITION_EXTERNAL;
            device.is_default = emitted == 0 ? 1 : 0;
            callback(&device, user_data);
            ++emitted;
        }

        return TC_OK;
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

    if (!device_id || !frame_callback || !camera) {
        return TC_ERROR_INVALID_ARGUMENT;
    }

    *camera = nullptr;
    auto instance = new (std::nothrow) tc_camera();
    if (!instance) return TC_ERROR_PLATFORM;

    try {
        instance->device_id = device_id;
        instance->options = thincam::normalized_options(options);
        instance->frame_callback = frame_callback;
        instance->error_callback = error_callback;
        instance->user_data = user_data;
    } catch (...) {
        delete instance;
        return TC_ERROR_PLATFORM;
    }

    if (instance->options.pixel_format != TC_PIXEL_BGRA32) {
        delete instance;
        return TC_ERROR_FORMAT_NOT_SUPPORTED;
    }

    instance->fd = open(device_id, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (instance->fd < 0) {
        const tc_status status = (errno == EACCES || errno == EPERM)
            ? TC_ERROR_PERMISSION_DENIED
            : (errno == EBUSY ? TC_ERROR_DEVICE_BUSY : TC_ERROR_DEVICE_NOT_FOUND);
        delete instance;
        return status;
    }

    v4l2_capability capability{};
    if (xioctl(instance->fd, VIDIOC_QUERYCAP, &capability) != 0) {
        release_resources(instance);
        delete instance;
        return TC_ERROR_PLATFORM;
    }

    const uint32_t caps = (capability.capabilities & V4L2_CAP_DEVICE_CAPS)
        ? capability.device_caps
        : capability.capabilities;

    if ((caps & V4L2_CAP_VIDEO_CAPTURE) == 0 || (caps & V4L2_CAP_STREAMING) == 0) {
        release_resources(instance);
        delete instance;
        return TC_ERROR_NOT_SUPPORTED;
    }

    tc_status status = configure_format(instance);
    if (status == TC_OK) status = configure_frame_rate(instance);
    if (status == TC_OK) status = allocate_buffers(instance);

    if (status != TC_OK) {
        release_resources(instance);
        delete instance;
        return status;
    }

    int32_t raw_mode = 0;
    if (get_integer_control(instance->fd, V4L2_CID_EXPOSURE_AUTO, &raw_mode) == TC_OK) {
        instance->exposure_mode_state.store(
            raw_mode == V4L2_EXPOSURE_MANUAL ? TC_EXPOSURE_MODE_MANUAL : TC_EXPOSURE_MODE_AUTO,
            std::memory_order_release);
    }
    if (get_integer_control(instance->fd, V4L2_CID_FOCUS_AUTO, &raw_mode) == TC_OK) {
        instance->focus_mode_state.store(
            raw_mode != 0 ? TC_FOCUS_MODE_CONTINUOUS_AUTO : TC_FOCUS_MODE_MANUAL,
            std::memory_order_release);
    }

    *camera = instance;
    return TC_OK;
}

tc_status TC_CALL tc_camera_start(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
    if (camera->running.load(std::memory_order_acquire)) {
        return TC_ERROR_ALREADY_RUNNING;
    }
    if (camera->worker.joinable()) {
        camera->worker.join();
    }

    tc_status status = queue_all_buffers(camera);
    if (status != TC_OK) return status;

    v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    if (xioctl(camera->fd, VIDIOC_STREAMON, &type) != 0) {
        return errno == EBUSY ? TC_ERROR_DEVICE_BUSY : TC_ERROR_PLATFORM;
    }

    camera->stop_requested.store(false, std::memory_order_release);
    camera->running.store(true, std::memory_order_release);

    try {
        camera->worker = std::thread(capture_loop, camera);
    } catch (...) {
        camera->running.store(false, std::memory_order_release);
        xioctl(camera->fd, VIDIOC_STREAMOFF, &type);
        return TC_ERROR_PLATFORM;
    }

    return TC_OK;
}

tc_status TC_CALL tc_camera_stop(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
    camera->stop_requested.store(true, std::memory_order_release);

    if (camera->worker.joinable()) {
        camera->worker.join();
    }

    v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    if (camera->fd >= 0) {
        xioctl(camera->fd, VIDIOC_STREAMOFF, &type);
    }

    camera->running.store(false, std::memory_order_release);
    return TC_OK;
}

void TC_CALL tc_camera_close(tc_camera* camera) {
    if (!camera) return;
    tc_camera_stop(camera);
    release_resources(camera);
    delete camera;
}


tc_status TC_CALL tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info) {

    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->control_mutex);
        return linux_control_info(camera, id, info);
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
        std::lock_guard<std::mutex> lock(camera->control_mutex);
        return linux_get_control(camera, id, value);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}

tc_status TC_CALL tc_camera_set_control(
    tc_camera* camera,
    const tc_control_value* value) {

    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->control_mutex);
        return linux_set_control(camera, value);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}
