#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dshow.h>
#include <ks.h>
#include <ksmedia.h>
#include <mfcaptureengine.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include "thincam.h"
#include "thincam_common.hpp"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cmath>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <utility>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

// The Media Foundation stream selectors are declared as signed enums but the
// IMFSourceReader methods take DWORD parameters, so cast them once here.
constexpr DWORD kAllStreams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD kFirstVideoStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
constexpr DWORD kCaptureEngineMediaSource = static_cast<DWORD>(MF_CAPTURE_ENGINE_MEDIASOURCE);

class com_scope {
public:
    com_scope() {
        result_ = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        should_uninitialize_ = result_ == S_OK || result_ == S_FALSE;
    }

    ~com_scope() {
        if (should_uninitialize_) CoUninitialize();
    }

    bool usable() const {
        return SUCCEEDED(result_) || result_ == RPC_E_CHANGED_MODE;
    }

private:
    HRESULT result_ = E_FAIL;
    bool should_uninitialize_ = false;
};

class mf_runtime {
public:
    mf_runtime() : result_(MFStartup(MF_VERSION, MFSTARTUP_LITE)) {}
    ~mf_runtime() { if (SUCCEEDED(result_)) MFShutdown(); }
    HRESULT result() const { return result_; }

private:
    HRESULT result_;
};

mf_runtime& media_foundation() {
    static mf_runtime runtime;
    return runtime;
}

std::string utf8(const wchar_t* value) {
    if (!value) return {};
    const int length = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1) return {};
    std::string result(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), length, nullptr, nullptr);
    result.pop_back();
    return result;
}

std::wstring wide(const std::string& value) {
    if (value.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, nullptr, 0);
    if (length <= 1) return {};
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, result.data(), length);
    result.pop_back();
    return result;
}

std::string hresult_message(HRESULT result, const char* operation) {
    wchar_t* buffer = nullptr;
    FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        static_cast<DWORD>(result),
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<wchar_t*>(&buffer),
        0,
        nullptr);

    std::string message = operation;
    message += " failed";
    if (buffer) {
        message += ": ";
        message += utf8(buffer);
        LocalFree(buffer);
    }
    return message;
}

tc_status status_from_hresult(HRESULT result) {
    if (result == E_ACCESSDENIED) return TC_ERROR_PERMISSION_DENIED;
    if (result == MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED) return TC_ERROR_DEVICE_NOT_FOUND;
    if (result == MF_E_INVALIDMEDIATYPE || result == MF_E_TOPO_CODEC_NOT_FOUND) return TC_ERROR_FORMAT_NOT_SUPPORTED;
    if (result == HRESULT_FROM_WIN32(ERROR_BUSY) || result == MF_E_HW_MFT_FAILED_START_STREAMING) return TC_ERROR_DEVICE_BUSY;
    return TC_ERROR_PLATFORM;
}

HRESULT create_video_source_attributes(IMFAttributes** attributes) {
    if (!attributes) return E_POINTER;
    *attributes = nullptr;

    ComPtr<IMFAttributes> value;
    HRESULT result = MFCreateAttributes(&value, 1);
    if (SUCCEEDED(result)) {
        result = value->SetGUID(
            MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
            MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    }
    if (SUCCEEDED(result)) *attributes = value.Detach();
    return result;
}

HRESULT find_activation(const std::wstring& symbolic_link, IMFActivate** activation) {
    if (!activation) return E_POINTER;
    *activation = nullptr;

    ComPtr<IMFAttributes> attributes;
    HRESULT result = create_video_source_attributes(&attributes);
    if (FAILED(result)) return result;

    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    result = MFEnumDeviceSources(attributes.Get(), &devices, &count);
    if (FAILED(result)) return result;

    for (UINT32 index = 0; index < count; ++index) {
        wchar_t* current = nullptr;
        UINT32 current_length = 0;
        if (SUCCEEDED(devices[index]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                &current,
                &current_length))) {
            if (current && symbolic_link == current) {
                *activation = devices[index];
                (*activation)->AddRef();
            }
            CoTaskMemFree(current);
        }
        devices[index]->Release();
    }
    CoTaskMemFree(devices);

    return *activation ? S_OK : HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}

HRESULT configure_reader(
    IMFSourceReader* reader,
    const tc_open_options& options,
    int* width,
    int* height,
    int* stride) {

    if (!reader || !width || !height || !stride) return E_POINTER;

    reader->SetStreamSelection(kAllStreams, FALSE);
    HRESULT result = reader->SetStreamSelection(kFirstVideoStream, TRUE);
    if (FAILED(result)) return result;

    ComPtr<IMFMediaType> output_type;
    result = MFCreateMediaType(&output_type);
    if (FAILED(result)) return result;

    result = output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(result)) result = output_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    if (SUCCEEDED(result)) result = output_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(result)) result = MFSetAttributeSize(output_type.Get(), MF_MT_FRAME_SIZE, options.width, options.height);
    if (SUCCEEDED(result)) result = MFSetAttributeRatio(output_type.Get(), MF_MT_FRAME_RATE, options.frames_per_second, 1);

    if (SUCCEEDED(result)) {
        result = reader->SetCurrentMediaType(
            kFirstVideoStream,
            nullptr,
            output_type.Get());
    }

    if (FAILED(result)) {
        // Retry with only a subtype request; the source reader then selects a native size.
        output_type.Reset();
        result = MFCreateMediaType(&output_type);
        if (SUCCEEDED(result)) result = output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(result)) result = output_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        if (SUCCEEDED(result)) {
            result = reader->SetCurrentMediaType(
                kFirstVideoStream,
                nullptr,
                output_type.Get());
        }
    }

    if (FAILED(result)) return result;

    ComPtr<IMFMediaType> actual;
    result = reader->GetCurrentMediaType(kFirstVideoStream, &actual);
    if (FAILED(result)) return result;

    UINT32 actual_width = 0;
    UINT32 actual_height = 0;
    result = MFGetAttributeSize(actual.Get(), MF_MT_FRAME_SIZE, &actual_width, &actual_height);
    if (FAILED(result)) return result;

    UINT32 raw_stride = 0;
    if (FAILED(actual->GetUINT32(MF_MT_DEFAULT_STRIDE, &raw_stride))) {
        raw_stride = actual_width * 4;
    }

    *width = static_cast<int>(actual_width);
    *height = static_cast<int>(actual_height);
    *stride = static_cast<int32_t>(raw_stride);
    if (*stride == 0) *stride = *width * 4;
    return S_OK;
}

} // namespace

struct tc_camera {
    std::string device_id;
    tc_open_options options{};
    tc_frame_callback frame_callback = nullptr;
    tc_error_callback error_callback = nullptr;
    void* user_data = nullptr;

    std::atomic<bool> running{false};
    std::atomic<bool> stop_requested{false};
    std::thread worker;

    std::mutex lifecycle_mutex;
    std::condition_variable startup_condition;
    std::condition_variable stop_condition;
    std::condition_variable flush_condition;
    bool startup_complete = false;
    bool flush_complete = false;
    tc_status startup_status = TC_ERROR_PLATFORM;
    std::string startup_error;

    int width = 0;
    int height = 0;
    int source_stride = 0;
    std::vector<uint8_t> frame_buffer;

    std::mutex control_mutex;
    ComPtr<IAMCameraControl> camera_control;
    ComPtr<IMFExtendedCameraController> extended_controller;
    std::atomic<int32_t> exposure_mode_state{TC_EXPOSURE_MODE_AUTO};
    std::atomic<int32_t> focus_mode_state{TC_FOCUS_MODE_AUTO};
};

namespace {

void signal_startup(tc_camera* camera, tc_status status, std::string error = {}) {
    {
        std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
        camera->startup_status = status;
        camera->startup_error = std::move(error);
        camera->startup_complete = true;
    }
    camera->startup_condition.notify_all();
}

void copy_bgrx_row(uint8_t* destination, const BYTE* source, int width) {
    const size_t length = static_cast<size_t>(width) * 4;
    std::memcpy(destination, source, length);
    for (int column = 0; column < width; ++column) {
        destination[static_cast<size_t>(column) * 4 + 3] = 255;
    }
}

void copy_media_buffer(
    tc_camera* camera,
    IMFMediaBuffer* buffer,
    int width,
    int height,
    int source_stride,
    int64_t timestamp,
    std::vector<uint8_t>& destination) {

    if (!camera || !buffer || width <= 0 || height <= 0) return;

    const int output_stride = width * 4;
    destination.resize(static_cast<size_t>(output_stride) * static_cast<size_t>(height));

    ComPtr<IMF2DBuffer> buffer_2d;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&buffer_2d)))) {
        BYTE* scanline_zero = nullptr;
        LONG pitch = 0;
        if (FAILED(buffer_2d->Lock2D(&scanline_zero, &pitch))) return;

        const auto pitch_magnitude = static_cast<size_t>(pitch < 0 ? -static_cast<int64_t>(pitch) : pitch);
        if (!scanline_zero || pitch_magnitude < static_cast<size_t>(output_stride)) {
            buffer_2d->Unlock2D();
            return;
        }

        for (int row = 0; row < height; ++row) {
            const BYTE* source_row = scanline_zero + static_cast<ptrdiff_t>(row) * pitch;
            copy_bgrx_row(
                destination.data() + static_cast<size_t>(row) * output_stride,
                source_row,
                width);
        }
        buffer_2d->Unlock2D();
    } else {
        BYTE* source = nullptr;
        DWORD maximum_length = 0;
        DWORD current_length = 0;
        if (FAILED(buffer->Lock(&source, &maximum_length, &current_length))) return;

        const int effective_stride = source_stride == 0 ? output_stride : source_stride;
        const auto stride_magnitude = static_cast<size_t>(
            effective_stride < 0 ? -static_cast<int64_t>(effective_stride) : effective_stride);
        const size_t required = stride_magnitude * static_cast<size_t>(height - 1) +
            static_cast<size_t>(output_stride);

        if (!source || stride_magnitude < static_cast<size_t>(output_stride) || current_length < required) {
            buffer->Unlock();
            return;
        }

        const BYTE* scanline_zero = effective_stride < 0
            ? source + stride_magnitude * static_cast<size_t>(height - 1)
            : source;

        for (int row = 0; row < height; ++row) {
            const BYTE* source_row = scanline_zero + static_cast<ptrdiff_t>(row) * effective_stride;
            copy_bgrx_row(
                destination.data() + static_cast<size_t>(row) * output_stride,
                source_row,
                width);
        }
        buffer->Unlock();
    }

    tc_frame frame{};
    frame.struct_size = sizeof(tc_frame);
    frame.data = destination.data();
    frame.data_length = destination.size();
    frame.width = width;
    frame.height = height;
    frame.stride = output_stride;
    frame.pixel_format = TC_PIXEL_BGRA32;
    frame.rotation_degrees = 0;
    frame.mirrored = 0;
    frame.timestamp_microseconds = timestamp / 10; // Media Foundation timestamps are 100 ns.

    if (camera->frame_callback) camera->frame_callback(&frame, camera->user_data);
}

void request_stop(tc_camera* camera) {
    camera->stop_requested.store(true, std::memory_order_release);
    camera->stop_condition.notify_all();
}

HRESULT refresh_actual_format(IMFSourceReader* reader, tc_camera* camera) {
    if (!reader || !camera) return E_POINTER;

    ComPtr<IMFMediaType> actual;
    HRESULT result = reader->GetCurrentMediaType(kFirstVideoStream, &actual);
    if (FAILED(result)) return result;

    UINT32 width = 0;
    UINT32 height = 0;
    result = MFGetAttributeSize(actual.Get(), MF_MT_FRAME_SIZE, &width, &height);
    if (FAILED(result)) return result;

    UINT32 raw_stride = 0;
    const int stride = SUCCEEDED(actual->GetUINT32(MF_MT_DEFAULT_STRIDE, &raw_stride))
        ? static_cast<int32_t>(raw_stride)
        : static_cast<int>(width) * 4;

    camera->width = static_cast<int>(width);
    camera->height = static_cast<int>(height);
    camera->source_stride = stride == 0 ? camera->width * 4 : stride;
    return S_OK;
}

class source_reader_callback final : public IMFSourceReaderCallback {
public:
    explicit source_reader_callback(tc_camera* camera) : camera_(camera) {}

    void set_reader(IMFSourceReader* reader) noexcept {
        reader_.store(reader, std::memory_order_release);
    }

    STDMETHODIMP QueryInterface(REFIID interface_id, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;

        if (interface_id == IID_IUnknown || interface_id == __uuidof(IMFSourceReaderCallback)) {
            *object = static_cast<IMFSourceReaderCallback*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override {
        return references_.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    STDMETHODIMP_(ULONG) Release() override {
        const ULONG remaining = references_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (remaining == 0) delete this;
        return remaining;
    }

    STDMETHODIMP OnReadSample(
        HRESULT status,
        DWORD,
        DWORD flags,
        LONGLONG timestamp,
        IMFSample* sample) override {
        try {

        tc_camera* camera = camera_;
        if (!camera || camera->stop_requested.load(std::memory_order_acquire)) return S_OK;

        if (FAILED(status)) {
            thincam::report_error(
                camera->error_callback,
                camera->user_data,
                status_from_hresult(status),
                hresult_message(status, "Reading camera frame"),
                true);
            request_stop(camera);
            return S_OK;
        }

        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
            thincam::report_error(
                camera->error_callback,
                camera->user_data,
                TC_ERROR_DEVICE_NOT_FOUND,
                "The camera stream ended.",
                true);
            request_stop(camera);
            return S_OK;
        }

        IMFSourceReader* reader = reader_.load(std::memory_order_acquire);

        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) {
            const HRESULT format_result = refresh_actual_format(reader, camera);
            if (FAILED(format_result)) {
                thincam::report_error(
                    camera->error_callback,
                    camera->user_data,
                    status_from_hresult(format_result),
                    hresult_message(format_result, "Refreshing camera format"),
                    true);
                request_stop(camera);
                return S_OK;
            }
        }

        if (sample) {
            ComPtr<IMFMediaBuffer> buffer;
            if (SUCCEEDED(sample->ConvertToContiguousBuffer(&buffer))) {
                copy_media_buffer(
                    camera,
                    buffer.Get(),
                    camera->width,
                    camera->height,
                    camera->source_stride,
                    timestamp,
                    camera->frame_buffer);
            }
        }

        if (!camera->stop_requested.load(std::memory_order_acquire) && reader) {
            const HRESULT next_result = reader->ReadSample(
                kFirstVideoStream,
                0,
                nullptr,
                nullptr,
                nullptr,
                nullptr);

            if (FAILED(next_result) && next_result != MF_E_NOTACCEPTING) {
                thincam::report_error(
                    camera->error_callback,
                    camera->user_data,
                    status_from_hresult(next_result),
                    hresult_message(next_result, "Requesting camera frame"),
                    true);
                request_stop(camera);
            }
        }

        return S_OK;
    
        } catch (...) {
            tc_camera* camera = camera_;
            if (camera) {
                if (camera->error_callback) {
                    camera->error_callback(
                        TC_ERROR_PLATFORM,
                        "Unhandled error in the Media Foundation frame callback.",
                        1,
                        camera->user_data);
                }
                request_stop(camera);
            }
            return S_OK;
        }
    }
    STDMETHODIMP OnFlush(DWORD) override {
        try {
            tc_camera* camera = camera_;
            if (!camera) return S_OK;
            {
                std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
                camera->flush_complete = true;
            }
            camera->flush_condition.notify_all();
        } catch (...) {
            // COM callbacks must not throw across the ABI boundary.
        }
        return S_OK;
    }

    STDMETHODIMP OnEvent(DWORD, IMFMediaEvent*) override {
        return S_OK;
    }

private:
    virtual ~source_reader_callback() = default;

    std::atomic<ULONG> references_{1};
    tc_camera* camera_ = nullptr;
    // The worker owns the reader through flush completion. Atomic access avoids a data
    // race with the final clear after OnFlush.
    std::atomic<IMFSourceReader*> reader_{nullptr};
};

void capture_worker_impl(tc_camera* camera) {
    com_scope com;
    if (!com.usable()) {
        signal_startup(camera, TC_ERROR_PLATFORM, "COM initialization failed.");
        return;
    }

    if (FAILED(media_foundation().result())) {
        signal_startup(camera, TC_ERROR_PLATFORM, "Media Foundation initialization failed.");
        return;
    }

    ComPtr<IMFActivate> activation;
    HRESULT result = find_activation(wide(camera->device_id), &activation);
    if (FAILED(result)) {
        signal_startup(camera, status_from_hresult(result), hresult_message(result, "Camera lookup"));
        return;
    }

    ComPtr<IMFMediaSource> source;
    result = activation->ActivateObject(IID_PPV_ARGS(&source));
    if (FAILED(result)) {
        signal_startup(camera, status_from_hresult(result), hresult_message(result, "Camera activation"));
        return;
    }

    {
        std::lock_guard<std::mutex> control_lock(camera->control_mutex);
        source.As(&camera->camera_control);

        ComPtr<IMFGetService> get_service;
        if (SUCCEEDED(source.As(&get_service))) {
            get_service->GetService(GUID_NULL, IID_PPV_ARGS(&camera->extended_controller));
        }
    }

    ComPtr<source_reader_callback> callback;
    callback.Attach(new (std::nothrow) source_reader_callback(camera));
    if (!callback) {
        source->Shutdown();
        signal_startup(camera, TC_ERROR_PLATFORM, "Source reader callback allocation failed.");
        return;
    }

    ComPtr<IMFAttributes> reader_attributes;
    result = MFCreateAttributes(&reader_attributes, 3);
    if (SUCCEEDED(result)) result = reader_attributes->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, callback.Get());
    if (SUCCEEDED(result)) result = reader_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    if (SUCCEEDED(result)) result = reader_attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);

    ComPtr<IMFSourceReader> reader;
    if (SUCCEEDED(result)) {
        result = MFCreateSourceReaderFromMediaSource(source.Get(), reader_attributes.Get(), &reader);
    }
    if (FAILED(result)) {
        source->Shutdown();
        signal_startup(camera, status_from_hresult(result), hresult_message(result, "Source reader creation"));
        return;
    }

    callback->set_reader(reader.Get());
    result = configure_reader(
        reader.Get(),
        camera->options,
        &camera->width,
        &camera->height,
        &camera->source_stride);
    if (FAILED(result)) {
        callback->set_reader(nullptr);
        source->Shutdown();
        signal_startup(camera, status_from_hresult(result), hresult_message(result, "Camera format selection"));
        return;
    }

    result = reader->ReadSample(
        kFirstVideoStream,
        0,
        nullptr,
        nullptr,
        nullptr,
        nullptr);
    if (FAILED(result)) {
        callback->set_reader(nullptr);
        source->Shutdown();
        signal_startup(camera, status_from_hresult(result), hresult_message(result, "Starting camera reads"));
        return;
    }

    camera->running.store(true, std::memory_order_release);
    signal_startup(camera, TC_OK);

    {
        std::unique_lock<std::mutex> lock(camera->lifecycle_mutex);
        camera->stop_condition.wait(
            lock,
            [camera] { return camera->stop_requested.load(std::memory_order_acquire); });
        camera->flush_complete = false;
    }

    camera->running.store(false, std::memory_order_release);
    result = reader->Flush(kFirstVideoStream);
    if (SUCCEEDED(result)) {
        std::unique_lock<std::mutex> lock(camera->lifecycle_mutex);
        camera->flush_condition.wait(lock, [camera] { return camera->flush_complete; });
    }

    callback->set_reader(nullptr);
    {
        std::lock_guard<std::mutex> control_lock(camera->control_mutex);
        camera->camera_control.Reset();
        camera->extended_controller.Reset();
    }
    source->Shutdown();
}

void capture_worker(tc_camera* camera) {
    try {
        capture_worker_impl(camera);
    } catch (...) {
        bool startup_complete = false;
        try {
            {
                std::lock_guard<std::mutex> lock(camera->lifecycle_mutex);
                startup_complete = camera->startup_complete;
                if (!startup_complete) {
                    camera->startup_status = TC_ERROR_PLATFORM;
                    camera->startup_error = "Unhandled error while starting Media Foundation capture.";
                    camera->startup_complete = true;
                }
            }
            if (!startup_complete) {
                camera->startup_condition.notify_all();
            } else {
                if (camera->error_callback) {
                    camera->error_callback(
                        TC_ERROR_PLATFORM,
                        "Unhandled error in the Media Foundation capture worker.",
                        1,
                        camera->user_data);
                }
                request_stop(camera);
            }
        } catch (...) {
            request_stop(camera);
        }
    }
}

tc_status control_hresult(HRESULT result) {
    if (SUCCEEDED(result)) return TC_OK;
    if (result == E_NOINTERFACE || result == E_NOTIMPL || result == E_PROP_ID_UNSUPPORTED ||
        result == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)) {
        return TC_ERROR_NOT_SUPPORTED;
    }
    return status_from_hresult(result);
}

bool camera_control_range(
    tc_camera* camera,
    CameraControlProperty property,
    long* minimum,
    long* maximum,
    long* step,
    long* default_value,
    long* flags) {

    return camera && camera->camera_control &&
        SUCCEEDED(camera->camera_control->GetRange(
            property, minimum, maximum, step, default_value, flags));
}

double exposure_value_to_microseconds(long value) {
    return std::pow(2.0, static_cast<double>(value)) * 1000000.0;
}

long snap_camera_value(double raw, long minimum, long maximum, long step) {
    const long effective_step = std::max(1L, step);
    const double units = (raw - minimum) / effective_step;
    const long snapped = minimum + static_cast<long>(std::llround(units)) * effective_step;
    return std::clamp(snapped, minimum, maximum);
}

double normalized_camera_value(long value, long minimum, long maximum) {
    if (maximum <= minimum) return 0.0;
    return std::clamp(
        static_cast<double>(value - minimum) / static_cast<double>(maximum - minimum),
        0.0,
        1.0);
}

tc_status get_extended_light_control(
    tc_camera* camera,
    IMFExtendedCameraControl** control) {

    if (!camera || !control) return TC_ERROR_INVALID_ARGUMENT;
    *control = nullptr;
    if (!camera->extended_controller) return TC_ERROR_NOT_SUPPORTED;
    const HRESULT result = camera->extended_controller->GetExtendedCameraControl(
        kCaptureEngineMediaSource,
        KSPROPERTY_CAMERACONTROL_EXTENDED_TORCHMODE,
        control);
    return control_hresult(result);
}

class extended_payload_lock {
public:
    explicit extended_payload_lock(IMFExtendedCameraControl* control)
        : control_(control) {
        if (control_) result_ = control_->LockPayload(&payload_, &size_);
    }

    ~extended_payload_lock() {
        if (control_ && SUCCEEDED(result_)) control_->UnlockPayload();
    }

    HRESULT result() const noexcept { return result_; }
    BYTE* data() const noexcept { return payload_; }
    ULONG size() const noexcept { return size_; }

private:
    IMFExtendedCameraControl* control_ = nullptr;
    HRESULT result_ = E_POINTER;
    BYTE* payload_ = nullptr;
    ULONG size_ = 0;
};

bool light_supports_enabled(ULONGLONG capabilities) {
    const bool supports_on = (capabilities & (KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON |
        KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER)) != 0;
    const bool supports_off = (capabilities & KSCAMERA_EXTENDEDPROP_VIDEOTORCH_OFF) != 0;
    return supports_on && supports_off;
}

bool light_supports_level(ULONGLONG capabilities) {
    return light_supports_enabled(capabilities) &&
        (capabilities & KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER) != 0;
}

tc_status read_extended_light_level(
    IMFExtendedCameraControl* control,
    double* level) {

    if (!control || !level) return TC_ERROR_INVALID_ARGUMENT;
    if (!light_supports_level(control->GetCapabilities())) return TC_ERROR_NOT_SUPPORTED;

    extended_payload_lock payload(control);
    if (FAILED(payload.result())) return control_hresult(payload.result());
    if (!payload.data() || payload.size() < sizeof(KSCAMERA_EXTENDEDPROP_VALUE)) {
        return TC_ERROR_PLATFORM;
    }

    const auto* value = reinterpret_cast<const KSCAMERA_EXTENDEDPROP_VALUE*>(payload.data());
    *level = std::clamp(static_cast<double>(value->Value.ull) / 100.0, 0.0, 1.0);
    return TC_OK;
}

tc_status write_extended_light_level(
    IMFExtendedCameraControl* control,
    double level) {

    if (!control || !std::isfinite(level) || level < 0.0 || level > 1.0) {
        return TC_ERROR_INVALID_ARGUMENT;
    }
    if (!light_supports_level(control->GetCapabilities())) return TC_ERROR_NOT_SUPPORTED;

    {
        extended_payload_lock payload(control);
        if (FAILED(payload.result())) return control_hresult(payload.result());
        if (!payload.data() || payload.size() < sizeof(KSCAMERA_EXTENDEDPROP_VALUE)) {
            return TC_ERROR_PLATFORM;
        }

        auto* value = reinterpret_cast<KSCAMERA_EXTENDEDPROP_VALUE*>(payload.data());
        value->Value.ull = static_cast<ULONGLONG>(std::llround(level * 100.0));
    }

    HRESULT result = control->SetFlags(KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER);
    if (SUCCEEDED(result)) result = control->CommitSettings();
    return control_hresult(result);
}

tc_status windows_control_info(tc_camera* camera, tc_control_id id, tc_control_info* info) {
    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    long minimum = 0;
    long maximum = 0;
    long step = 0;
    long default_value = 0;
    long flags = 0;
    const uint32_t rw = TC_CONTROL_FLAG_READABLE | TC_CONTROL_FLAG_WRITABLE;

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            if (!camera_control_range(camera, CameraControl_Exposure,
                    &minimum, &maximum, &step, &default_value, &flags)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            uint64_t modes = 0;
            if (flags & CameraControl_Flags_Auto) modes |= thincam::enum_flag(TC_EXPOSURE_MODE_AUTO);
            if (flags & CameraControl_Flags_Manual) {
                modes |= thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL);
                modes |= thincam::enum_flag(TC_EXPOSURE_MODE_LOCKED);
            }
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, rw, 0, 0, 0,
                (modes & thincam::enum_flag(TC_EXPOSURE_MODE_AUTO))
                    ? TC_EXPOSURE_MODE_AUTO
                    : TC_EXPOSURE_MODE_MANUAL,
                modes);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_DURATION_US:
            if (!camera_control_range(camera, CameraControl_Exposure,
                    &minimum, &maximum, &step, &default_value, &flags) ||
                !(flags & CameraControl_Flags_Manual)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_INT64, rw,
                exposure_value_to_microseconds(minimum),
                exposure_value_to_microseconds(maximum),
                0.0,
                exposure_value_to_microseconds(default_value));
            return TC_OK;
        case TC_CONTROL_FOCUS_MODE: {
            if (!camera_control_range(camera, CameraControl_Focus,
                    &minimum, &maximum, &step, &default_value, &flags)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            uint64_t modes = 0;
            if (flags & CameraControl_Flags_Auto) modes |= thincam::enum_flag(TC_FOCUS_MODE_AUTO);
            if (flags & CameraControl_Flags_Manual) {
                modes |= thincam::enum_flag(TC_FOCUS_MODE_MANUAL);
                modes |= thincam::enum_flag(TC_FOCUS_MODE_LOCKED);
            }
            if (modes == 0) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_ENUM, rw, 0, 0, 0,
                (modes & thincam::enum_flag(TC_FOCUS_MODE_AUTO))
                    ? TC_FOCUS_MODE_AUTO
                    : TC_FOCUS_MODE_MANUAL,
                modes);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION:
            if (!camera_control_range(camera, CameraControl_Focus,
                    &minimum, &maximum, &step, &default_value, &flags) ||
                !(flags & CameraControl_Flags_Manual) || maximum <= minimum) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, rw, 0.0, 1.0,
                static_cast<double>(std::max(1L, step)) /
                    static_cast<double>(maximum - minimum),
                normalized_camera_value(default_value, minimum, maximum));
            return TC_OK;
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!camera_control_range(camera, CameraControl_Zoom,
                    &minimum, &maximum, &step, &default_value, &flags) || maximum <= minimum) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const double base = default_value > 0
                ? static_cast<double>(default_value)
                : std::max(1.0, static_cast<double>(minimum));
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, rw,
                std::max(0.01, minimum / base),
                std::max(0.01, maximum / base),
                std::max(1L, step) / base,
                1.0);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            const ULONGLONG capabilities = control->GetCapabilities();
            if (!light_supports_enabled(capabilities)) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_BOOL, rw, 0.0, 1.0, 1.0, 0.0);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_LEVEL: {
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            if (!light_supports_level(control->GetCapabilities())) return TC_ERROR_NOT_SUPPORTED;
            *info = thincam::control_info(
                id, TC_CONTROL_VALUE_DOUBLE, rw, 0.0, 1.0, 0.01, 0.5);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_COMPENSATION_EV:
        case TC_CONTROL_EXPOSURE_ISO:
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status windows_get_control(tc_camera* camera, tc_control_id id, tc_control_value* value) {
    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    long raw = 0;
    long flags = 0;
    long minimum = 0;
    long maximum = 0;
    long step = 0;
    long default_value = 0;

    switch (id) {
        case TC_CONTROL_EXPOSURE_MODE: {
            if (!camera->camera_control) return TC_ERROR_NOT_SUPPORTED;
            const HRESULT result = camera->camera_control->Get(CameraControl_Exposure, &raw, &flags);
            if (FAILED(result)) return control_hresult(result);
            const int32_t mode = flags & CameraControl_Flags_Auto
                ? TC_EXPOSURE_MODE_AUTO
                : camera->exposure_mode_state.load(std::memory_order_acquire);
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_EXPOSURE_DURATION_US: {
            if (!camera->camera_control) return TC_ERROR_NOT_SUPPORTED;
            const HRESULT result = camera->camera_control->Get(CameraControl_Exposure, &raw, &flags);
            if (FAILED(result)) return control_hresult(result);
            *value = thincam::int64_control(
                id, static_cast<int64_t>(std::llround(exposure_value_to_microseconds(raw))));
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_MODE: {
            if (!camera->camera_control) return TC_ERROR_NOT_SUPPORTED;
            const HRESULT result = camera->camera_control->Get(CameraControl_Focus, &raw, &flags);
            if (FAILED(result)) return control_hresult(result);
            const int32_t mode = flags & CameraControl_Flags_Auto
                ? TC_FOCUS_MODE_AUTO
                : camera->focus_mode_state.load(std::memory_order_acquire);
            *value = thincam::enum_control(id, mode);
            return TC_OK;
        }
        case TC_CONTROL_FOCUS_POSITION:
            if (!camera_control_range(camera, CameraControl_Focus,
                    &minimum, &maximum, &step, &default_value, &flags) || !camera->camera_control) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            if (FAILED(camera->camera_control->Get(CameraControl_Focus, &raw, &flags))) {
                return TC_ERROR_PLATFORM;
            }
            *value = thincam::double_control(
                id, normalized_camera_value(raw, minimum, maximum));
            return TC_OK;
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!camera_control_range(camera, CameraControl_Zoom,
                    &minimum, &maximum, &step, &default_value, &flags) || !camera->camera_control) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            if (FAILED(camera->camera_control->Get(CameraControl_Zoom, &raw, &flags))) {
                return TC_ERROR_PLATFORM;
            }
            const double base = default_value > 0
                ? static_cast<double>(default_value)
                : std::max(1.0, static_cast<double>(minimum));
            *value = thincam::double_control(id, raw / base);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            const ULONGLONG capabilities = control->GetCapabilities();
            if (!light_supports_enabled(capabilities)) return TC_ERROR_NOT_SUPPORTED;
            const ULONGLONG torch_flags = control->GetFlags();
            *value = thincam::bool_control(
                id, (torch_flags & (KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON |
                    KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER)) != 0);
            return TC_OK;
        }
        case TC_CONTROL_LIGHT_LEVEL: {
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            double level = 0.0;
            const tc_status read_status = read_extended_light_level(control.Get(), &level);
            if (read_status != TC_OK) return read_status;
            *value = thincam::double_control(id, level);
            return TC_OK;
        }
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}

tc_status windows_set_control(tc_camera* camera, const tc_control_value* value) {
    if (!camera || !value) return TC_ERROR_INVALID_ARGUMENT;
    long raw = 0;
    long current_flags = 0;
    long minimum = 0;
    long maximum = 0;
    long step = 0;
    long default_value = 0;
    long supported_flags = 0;

    switch (value->id) {
        case TC_CONTROL_EXPOSURE_MODE:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->camera_control) return TC_ERROR_NOT_SUPPORTED;
            if (!camera_control_range(camera, CameraControl_Exposure,
                    &minimum, &maximum, &step, &default_value, &supported_flags)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            if (FAILED(camera->camera_control->Get(CameraControl_Exposure, &raw, &current_flags))) raw = default_value;
            if (value->value.enum_value == TC_EXPOSURE_MODE_AUTO) {
                if (!(supported_flags & CameraControl_Flags_Auto)) return TC_ERROR_NOT_SUPPORTED;
                const HRESULT result = camera->camera_control->Set(
                    CameraControl_Exposure, raw, CameraControl_Flags_Auto);
                if (SUCCEEDED(result)) camera->exposure_mode_state.store(
                    TC_EXPOSURE_MODE_AUTO, std::memory_order_release);
                return control_hresult(result);
            }
            if (value->value.enum_value == TC_EXPOSURE_MODE_MANUAL ||
                value->value.enum_value == TC_EXPOSURE_MODE_LOCKED) {
                if (!(supported_flags & CameraControl_Flags_Manual)) return TC_ERROR_NOT_SUPPORTED;
                const HRESULT result = camera->camera_control->Set(
                    CameraControl_Exposure, raw, CameraControl_Flags_Manual);
                if (SUCCEEDED(result)) camera->exposure_mode_state.store(
                    value->value.enum_value, std::memory_order_release);
                return control_hresult(result);
            }
            return TC_ERROR_INVALID_ARGUMENT;
        case TC_CONTROL_EXPOSURE_DURATION_US: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_INT64) ||
                value->value.integer_value <= 0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->camera_control) return TC_ERROR_NOT_SUPPORTED;
            if (!camera_control_range(camera, CameraControl_Exposure,
                    &minimum, &maximum, &step, &default_value, &supported_flags) ||
                !(supported_flags & CameraControl_Flags_Manual)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const double seconds = value->value.integer_value / 1000000.0;
            if (!std::isfinite(seconds) || seconds <= 0) return TC_ERROR_INVALID_ARGUMENT;
            const double logarithmic_value = std::log2(seconds);
            if (!std::isfinite(logarithmic_value) || logarithmic_value < minimum ||
                logarithmic_value > maximum) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            raw = snap_camera_value(logarithmic_value, minimum, maximum, step);
            const HRESULT result = camera->camera_control->Set(
                CameraControl_Exposure, raw, CameraControl_Flags_Manual);
            if (SUCCEEDED(result)) camera->exposure_mode_state.store(
                TC_EXPOSURE_MODE_MANUAL, std::memory_order_release);
            return control_hresult(result);
        }
        case TC_CONTROL_FOCUS_MODE:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_ENUM)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->camera_control || !camera_control_range(camera, CameraControl_Focus,
                    &minimum, &maximum, &step, &default_value, &supported_flags)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            if (FAILED(camera->camera_control->Get(CameraControl_Focus, &raw, &current_flags))) raw = default_value;
            if (value->value.enum_value == TC_FOCUS_MODE_AUTO) {
                if (!(supported_flags & CameraControl_Flags_Auto)) return TC_ERROR_NOT_SUPPORTED;
                const HRESULT result = camera->camera_control->Set(
                    CameraControl_Focus, raw, CameraControl_Flags_Auto);
                if (SUCCEEDED(result)) camera->focus_mode_state.store(
                    TC_FOCUS_MODE_AUTO, std::memory_order_release);
                return control_hresult(result);
            }
            if (value->value.enum_value == TC_FOCUS_MODE_CONTINUOUS_AUTO) {
                // IAMCameraControl exposes one generic automatic-focus flag and
                // cannot promise continuous autofocus semantics.
                return TC_ERROR_NOT_SUPPORTED;
            }
            if (value->value.enum_value == TC_FOCUS_MODE_MANUAL ||
                value->value.enum_value == TC_FOCUS_MODE_LOCKED) {
                if (!(supported_flags & CameraControl_Flags_Manual)) return TC_ERROR_NOT_SUPPORTED;
                const HRESULT result = camera->camera_control->Set(
                    CameraControl_Focus, raw, CameraControl_Flags_Manual);
                if (SUCCEEDED(result)) camera->focus_mode_state.store(
                    value->value.enum_value, std::memory_order_release);
                return control_hresult(result);
            }
            return TC_ERROR_INVALID_ARGUMENT;
        case TC_CONTROL_FOCUS_POSITION:
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value < 0.0 ||
                value->value.double_value > 1.0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->camera_control || !camera_control_range(camera, CameraControl_Focus,
                    &minimum, &maximum, &step, &default_value, &supported_flags) ||
                !(supported_flags & CameraControl_Flags_Manual)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            raw = snap_camera_value(
                minimum + value->value.double_value * (maximum - minimum),
                minimum, maximum, step);
            {
                const HRESULT result = camera->camera_control->Set(
                    CameraControl_Focus, raw, CameraControl_Flags_Manual);
                if (SUCCEEDED(result)) camera->focus_mode_state.store(
                    TC_FOCUS_MODE_MANUAL, std::memory_order_release);
                return control_hresult(result);
            }
        case TC_CONTROL_ZOOM_FACTOR: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE) ||
                !std::isfinite(value->value.double_value) || value->value.double_value <= 0) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            if (!camera->camera_control || !camera_control_range(camera, CameraControl_Zoom,
                    &minimum, &maximum, &step, &default_value, &supported_flags)) {
                return TC_ERROR_NOT_SUPPORTED;
            }
            const double base = default_value > 0
                ? static_cast<double>(default_value)
                : std::max(1.0, static_cast<double>(minimum));
            raw = snap_camera_value(value->value.double_value * base,
                minimum, maximum, step);
            return control_hresult(camera->camera_control->Set(
                CameraControl_Zoom, raw, CameraControl_Flags_Manual));
        }
        case TC_CONTROL_LIGHT_ENABLED: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_BOOL)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            const ULONGLONG capabilities = control->GetCapabilities();
            ULONGLONG requested = KSCAMERA_EXTENDEDPROP_VIDEOTORCH_OFF;
            if (value->value.boolean_value != 0) {
                if (capabilities & KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON) {
                    requested = KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON;
                } else if (capabilities & KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER) {
                    requested = KSCAMERA_EXTENDEDPROP_VIDEOTORCH_ON_ADJUSTABLEPOWER;
                } else {
                    return TC_ERROR_NOT_SUPPORTED;
                }
            }
            if ((capabilities & requested) == 0) return TC_ERROR_NOT_SUPPORTED;
            HRESULT result = control->SetFlags(requested);
            if (SUCCEEDED(result)) result = control->CommitSettings();
            return control_hresult(result);
        }
        case TC_CONTROL_LIGHT_LEVEL: {
            if (!thincam::control_value_matches(value, TC_CONTROL_VALUE_DOUBLE)) {
                return TC_ERROR_INVALID_ARGUMENT;
            }
            ComPtr<IMFExtendedCameraControl> control;
            const tc_status status = get_extended_light_control(camera, &control);
            if (status != TC_OK) return status;
            return write_extended_light_level(control.Get(), value->value.double_value);
        }
        default:
            return TC_ERROR_NOT_SUPPORTED;
    }
}


} // namespace

tc_status TC_CALL tc_get_permission_status(tc_permission_status* status) {
    if (!status) return TC_ERROR_INVALID_ARGUMENT;
    *status = TC_PERMISSION_GRANTED;
    return TC_OK;
}

tc_status TC_CALL tc_request_permission(tc_permission_callback callback, void* user_data) {
    if (callback) callback(TC_PERMISSION_GRANTED, user_data);
    return TC_OK;
}

tc_status TC_CALL tc_enumerate_devices(tc_device_callback callback, void* user_data) {
    if (!callback) return TC_ERROR_INVALID_ARGUMENT;

    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    const auto cleanup = [&]() noexcept {
        if (!devices) return;
        for (UINT32 index = 0; index < count; ++index) {
            if (devices[index]) {
                devices[index]->Release();
                devices[index] = nullptr;
            }
        }
        CoTaskMemFree(devices);
        devices = nullptr;
    };

    try {
        com_scope com;
        if (!com.usable() || FAILED(media_foundation().result())) {
            return TC_ERROR_PLATFORM;
        }

        ComPtr<IMFAttributes> attributes;
        HRESULT result = create_video_source_attributes(&attributes);
        if (FAILED(result)) return status_from_hresult(result);

        result = MFEnumDeviceSources(attributes.Get(), &devices, &count);
        if (FAILED(result)) return status_from_hresult(result);

        for (UINT32 index = 0; index < count; ++index) {
            wchar_t* friendly_name = nullptr;
            wchar_t* symbolic_link = nullptr;
            UINT32 ignored = 0;

            devices[index]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                &friendly_name,
                &ignored);
            devices[index]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                &symbolic_link,
                &ignored);

            try {
                const std::string id = utf8(symbolic_link);
                const std::string name = utf8(friendly_name);

                if (!id.empty()) {
                    tc_device_info device{};
                    device.struct_size = sizeof(tc_device_info);
                    device.id = id.c_str();
                    device.name = name.empty() ? id.c_str() : name.c_str();
                    device.position = TC_POSITION_EXTERNAL;
                    device.is_default = index == 0 ? 1 : 0;
                    callback(&device, user_data);
                }
            } catch (...) {
                CoTaskMemFree(friendly_name);
                CoTaskMemFree(symbolic_link);
                cleanup();
                return TC_ERROR_PLATFORM;
            }

            CoTaskMemFree(friendly_name);
            CoTaskMemFree(symbolic_link);
            devices[index]->Release();
            devices[index] = nullptr;
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
    } catch (...) {
        delete instance;
        return TC_ERROR_PLATFORM;
    }

    if (instance->options.pixel_format != TC_PIXEL_BGRA32) {
        delete instance;
        return TC_ERROR_FORMAT_NOT_SUPPORTED;
    }

    *camera = instance;
    return TC_OK;
}

tc_status TC_CALL tc_camera_start(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    std::unique_lock<std::mutex> lock(camera->lifecycle_mutex);
    if (camera->running.load(std::memory_order_acquire) || camera->worker.joinable()) {
        return TC_ERROR_ALREADY_RUNNING;
    }

    camera->stop_requested.store(false, std::memory_order_release);
    camera->startup_complete = false;
    camera->startup_status = TC_ERROR_PLATFORM;
    camera->startup_error.clear();

    try {
        camera->worker = std::thread(capture_worker, camera);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }

    const bool signaled = camera->startup_condition.wait_for(
        lock,
        std::chrono::seconds(10),
        [camera] { return camera->startup_complete; });

    if (!signaled) {
        request_stop(camera);
        lock.unlock();
        if (camera->worker.joinable()) camera->worker.join();
        return TC_ERROR_TIMEOUT;
    }

    const tc_status status = camera->startup_status;
    lock.unlock();

    if (status != TC_OK && camera->worker.joinable()) {
        camera->worker.join();
    }

    return status;
}

tc_status TC_CALL tc_camera_stop(tc_camera* camera) {
    if (!camera) return TC_ERROR_INVALID_ARGUMENT;

    request_stop(camera);
    if (camera->worker.joinable()) camera->worker.join();
    camera->running.store(false, std::memory_order_release);
    return TC_OK;
}

void TC_CALL tc_camera_close(tc_camera* camera) {
    if (!camera) return;
    tc_camera_stop(camera);
    delete camera;
}


tc_status TC_CALL tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info) {

    if (!camera || !info) return TC_ERROR_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(camera->control_mutex);
        return windows_control_info(camera, id, info);
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
        return windows_get_control(camera, id, value);
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
        return windows_set_control(camera, value);
    } catch (...) {
        return TC_ERROR_PLATFORM;
    }
}
