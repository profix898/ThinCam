cmake_minimum_required(VERSION 3.20)
project(thincam_linux LANGUAGES CXX)

add_library(thincam SHARED
    thincam_linux.cpp
    ../common/thincam_common.cpp)

target_compile_features(thincam PRIVATE cxx_std_17)
target_compile_definitions(thincam PRIVATE THINCAM_BUILDING=1)
target_include_directories(thincam PRIVATE
    ../include
    ../common)
target_compile_options(thincam PRIVATE -Wall -Wextra -Wpedantic -Werror)
target_link_libraries(thincam PRIVATE pthread)
set_target_properties(thincam PROPERTIES
    OUTPUT_NAME thincam
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
