# SPDX-License-Identifier: GPL-3.0-or-later

function(logicontrol_native_test target source)
    add_executable(${target} ${source})
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_definitions(${target} PRIVATE
        UNICODE _UNICODE DIRECTINPUT_VERSION=0x0800 WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(${target} PRIVATE /W4 /WX /permissive- /guard:cf)
    target_link_options(${target} PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
    set_target_properties(${target} PROPERTIES
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endfunction()

logicontrol_native_test(LogiControl.ProtocolTests ProtocolTests.cpp)
logicontrol_native_test(LogiControl.ComAbiSmoke ComAbiSmoke.cpp)
logicontrol_native_test(LogiControl.BrokerRangeSmoke BrokerRangeSmoke.cpp)
logicontrol_native_test(LogiControl.ControlSmoke ControlSmoke.cpp)
logicontrol_native_test(LogiControl.DirectInputHarness DirectInputHarness.cpp)

target_link_libraries(LogiControl.ComAbiSmoke PRIVATE dxguid)
target_link_libraries(LogiControl.DirectInputHarness PRIVATE dinput8 dxguid user32)

add_test(NAME protocol-vectors COMMAND LogiControl.ProtocolTests)
add_test(
    NAME provider-com-abi
    COMMAND LogiControl.ComAbiSmoke $<TARGET_FILE:LogiControl.Ffb>)
add_test(
    NAME provider-lazy-device-open
    COMMAND LogiControl.ComAbiSmoke
        $<TARGET_FILE:LogiControl.Ffb>
        [[\\?\hid#vid_046d&pid_c29a#lazy-open-test]]
        --open-only)
