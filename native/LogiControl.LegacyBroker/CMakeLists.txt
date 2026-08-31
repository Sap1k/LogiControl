# SPDX-License-Identifier: GPL-3.0-or-later

add_executable(LogiControl.LegacyBroker src/main.cpp)

target_compile_features(LogiControl.LegacyBroker PRIVATE cxx_std_20)
target_compile_definitions(LogiControl.LegacyBroker PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(LogiControl.LegacyBroker PRIVATE /W4 /WX /permissive- /guard:cf)
target_link_options(LogiControl.LegacyBroker PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
target_link_libraries(LogiControl.LegacyBroker PRIVATE hid advapi32)

set_target_properties(LogiControl.LegacyBroker PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
