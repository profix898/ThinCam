cmake_minimum_required(VERSION 3.20)
project(thincam_pixel_tests LANGUAGES CXX)

add_executable(pixel_convert_tests pixel_convert_tests.cpp)
target_compile_features(pixel_convert_tests PRIVATE cxx_std_17)
target_include_directories(pixel_convert_tests PRIVATE ../../include ../../common)
target_compile_options(pixel_convert_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
