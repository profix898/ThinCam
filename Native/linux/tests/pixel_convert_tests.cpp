#include "thincam_common.hpp"

#include <cassert>
#include <cstddef>
#include <cstdint>
#include <type_traits>
#include <vector>

int main() {
    {
        const uint8_t yuyv[] = {16, 128, 235, 128};
        uint8_t output[8]{};
        thincam::yuyv_to_bgra(yuyv, 2, 1, 4, output, 8);

        assert(output[0] <= 2 && output[1] <= 2 && output[2] <= 2 && output[3] == 255);
        assert(output[4] >= 250 && output[5] >= 250 && output[6] >= 250 && output[7] == 255);
    }

    {
        const uint8_t y[] = {16, 235, 81, 145};
        const uint8_t u[] = {128};
        const uint8_t v[] = {128};
        uint8_t output[16]{};

        thincam::yuv420_888_to_bgra(
            y, 4, 2, 1,
            u, 1, 1, 1,
            v, 1, 1, 1,
            2, 2,
            output, 8);

        assert(output[3] == 255);
        assert(output[7] == 255);
        assert(output[11] == 255);
        assert(output[15] == 255);
    }

    {
        static_assert(std::is_standard_layout_v<tc_control_info>);
        static_assert(std::is_standard_layout_v<tc_control_value>);
        static_assert(offsetof(tc_control_info, struct_size) == 0);
        static_assert(offsetof(tc_control_value, struct_size) == 0);

        const tc_control_info info = thincam::control_info(
            TC_CONTROL_ZOOM_FACTOR,
            TC_CONTROL_VALUE_DOUBLE,
            TC_CONTROL_FLAG_READABLE | TC_CONTROL_FLAG_WRITABLE,
            1.0, 4.0, 0.1, 1.0);
        if (info.struct_size != sizeof(tc_control_info) ||
            info.id != TC_CONTROL_ZOOM_FACTOR ||
            info.value_type != TC_CONTROL_VALUE_DOUBLE ||
            info.minimum != 1.0 || info.maximum != 4.0) return 1;

        const tc_control_value light = thincam::bool_control(TC_CONTROL_LIGHT_ENABLED, true);
        if (light.struct_size != sizeof(tc_control_value) ||
            light.id != TC_CONTROL_LIGHT_ENABLED ||
            light.value_type != TC_CONTROL_VALUE_BOOL ||
            light.value.boolean_value != 1 ||
            !thincam::control_value_matches(&light, TC_CONTROL_VALUE_BOOL) ||
            thincam::control_value_matches(&light, TC_CONTROL_VALUE_DOUBLE)) return 2;

        const uint64_t modes =
            thincam::enum_flag(TC_EXPOSURE_MODE_AUTO) |
            thincam::enum_flag(TC_EXPOSURE_MODE_MANUAL);
        if ((modes & thincam::enum_flag(TC_EXPOSURE_MODE_AUTO)) == 0 ||
            (modes & thincam::enum_flag(TC_EXPOSURE_MODE_LOCKED)) != 0) return 3;
    }

    return 0;
}
