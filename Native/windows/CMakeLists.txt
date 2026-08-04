cmake_minimum_required(VERSION 3.20)
project(thincam_windows LANGUAGES CXX)

add_library(thincam SHARED
    thincam_windows.cpp
    ../common/thincam_common.cpp)

target_compile_features(thincam PRIVATE cxx_std_17)
target_compile_definitions(thincam PRIVATE THINCAM_BUILDING=1 UNICODE _UNICODE)
target_include_directories(thincam PRIVATE ../include ../common)
target_compile_options(thincam PRIVATE /W4 /WX /permissive- /EHsc)
target_link_libraries(thincam PRIVATE mf mfplat mfreadwrite mfuuid ole32)
set_target_properties(thincam PROPERTIES OUTPUT_NAME thincam)
