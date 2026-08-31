# SPDX-License-Identifier: GPL-3.0-or-later

add_library(LogiControl.Ffb SHARED
    src/LogiControlFfb.cpp
    src/LogiControlFfb.def)

target_compile_features(LogiControl.Ffb PRIVATE cxx_std_20)
target_compile_definitions(LogiControl.Ffb PRIVATE
    UNICODE _UNICODE DIRECTINPUT_VERSION=0x0800 WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(LogiControl.Ffb PRIVATE /W4 /WX /permissive- /guard:cf)
target_link_options(LogiControl.Ffb PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
target_link_libraries(LogiControl.Ffb PRIVATE dxguid)

set_target_properties(LogiControl.Ffb PROPERTIES
    OUTPUT_NAME "LogiControl.Ffb"
    PREFIX ""
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
