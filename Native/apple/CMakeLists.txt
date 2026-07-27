cmake_minimum_required(VERSION 3.20)
project(thincam_apple LANGUAGES CXX OBJCXX)

if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    add_library(thincam STATIC thincam_apple.mm ../common/thincam_common.cpp)
else()
    add_library(thincam SHARED thincam_apple.mm ../common/thincam_common.cpp)
endif()

target_compile_features(thincam PRIVATE cxx_std_17)
target_compile_definitions(thincam PRIVATE THINCAM_BUILDING=1)
target_include_directories(thincam PRIVATE ../include ../common)
target_compile_options(thincam PRIVATE -fobjc-arc -Wall -Wextra)
target_link_libraries(thincam PRIVATE
    "-framework AVFoundation"
    "-framework CoreMedia"
    "-framework CoreVideo"
    "-framework Foundation")
set_target_properties(thincam PROPERTIES
    OUTPUT_NAME thincam
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
