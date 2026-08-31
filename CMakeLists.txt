# SPDX-License-Identifier: GPL-3.0-or-later
cmake_minimum_required(VERSION 3.25)

project(LogiControlNative VERSION 0.1.0 LANGUAGES CXX)
enable_testing()

if(NOT WIN32)
    message(FATAL_ERROR "LogiControl native components are Windows-only.")
endif()

add_subdirectory(native)
