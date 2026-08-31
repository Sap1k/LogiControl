// SPDX-License-Identifier: GPL-3.0-or-later

#include <windows.h>
#include <dinput.h>

#include <array>
#include <chrono>
#include <iostream>
#include <string_view>
#include <thread>

namespace {

constexpr DWORD kDfgtVidPid = static_cast<DWORD>(MAKELONG(0x046D, 0xC29A));
constexpr wchar_t kWindowClassName[] = L"LogiControl.DirectInputHarness.Window";

class HarnessWindow final {
public:
    HarnessWindow() {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = DefWindowProcW;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpszClassName = kWindowClassName;
        atom_ = RegisterClassExW(&windowClass);
        if (atom_ == 0) return;

        handle_ = CreateWindowExW(
            0,
            kWindowClassName,
            L"LogiControl DirectInput Harness",
            WS_OVERLAPPED,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            320,
            200,
            nullptr,
            nullptr,
            windowClass.hInstance,
            nullptr);
    }

    ~HarnessWindow() {
        if (handle_ != nullptr) DestroyWindow(handle_);
        if (atom_ != 0) UnregisterClassW(kWindowClassName, GetModuleHandleW(nullptr));
    }

    HarnessWindow(const HarnessWindow&) = delete;
    HarnessWindow& operator=(const HarnessWindow&) = delete;

    [[nodiscard]] HWND Get() const noexcept { return handle_; }

private:
    ATOM atom_{};
    HWND handle_{};
};

struct DeviceSelection {
    GUID instanceGuid{};
    wchar_t instanceName[MAX_PATH]{};
    wchar_t productName[MAX_PATH]{};
    bool found{};
};

struct ForceAxisSelection {
    DWORD objectId{};
    bool found{};
};

enum class TestEffect {
    Constant,
    Periodic,
    Spring,
    Damper,
};

bool TryParseEffect(std::wstring_view value, TestEffect& effect) {
    if (value == L"constant") effect = TestEffect::Constant;
    else if (value == L"periodic") effect = TestEffect::Periodic;
    else if (value == L"spring") effect = TestEffect::Spring;
    else if (value == L"damper") effect = TestEffect::Damper;
    else return false;
    return true;
}

const wchar_t* EffectName(TestEffect effect) {
    switch (effect) {
    case TestEffect::Constant: return L"constant";
    case TestEffect::Periodic: return L"sine-periodic";
    case TestEffect::Spring: return L"spring";
    case TestEffect::Damper: return L"damper";
    }
    return L"unknown";
}

const wchar_t* EffectPrompt(TestEffect effect) {
    switch (effect) {
    case TestEffect::Constant:
        return L"This will apply 30% constant force for 1 second.";
    case TestEffect::Periodic:
        return L"This will apply a 30%, 5 Hz sine force for 1 second.";
    case TestEffect::Spring:
        return L"This will apply a 30% centered spring for 1 second; begin off-center.";
    case TestEffect::Damper:
        return L"This will apply a 30% damper for 1 second; move the wheel during the test.";
    }
    return L"Unknown effect.";
}

BOOL CALLBACK SelectDfgt(const DIDEVICEINSTANCEW* instance, void* context) {
    auto& selection = *static_cast<DeviceSelection*>(context);
    if (instance == nullptr || instance->guidProduct.Data1 != kDfgtVidPid) {
        return DIENUM_CONTINUE;
    }
    selection.instanceGuid = instance->guidInstance;
    wcscpy_s(selection.instanceName, instance->tszInstanceName);
    wcscpy_s(selection.productName, instance->tszProductName);
    selection.found = true;
    return DIENUM_STOP;
}

BOOL CALLBACK PrintEffect(const DIEFFECTINFOW* effect, void*) {
    if (effect != nullptr) {
        std::wcout << L"effect: " << effect->tszName
                   << L" type=0x" << std::hex << effect->dwEffType
                   << std::dec << L"\n";
    }
    return DIENUM_CONTINUE;
}

BOOL CALLBACK InspectAxis(const DIDEVICEOBJECTINSTANCEW* object, void* context) {
    if (object == nullptr) return DIENUM_CONTINUE;
    auto& selection = *static_cast<ForceAxisSelection*>(context);
    const bool forceActuator = (object->dwFlags & DIDOI_FFACTUATOR) != 0;
    std::wcout << L"axis: " << object->tszName
               << L" offset=" << object->dwOfs
               << L" type=0x" << std::hex << object->dwType
               << L" flags=0x" << object->dwFlags
               << std::dec << L" forceActuator=" << forceActuator << L"\n";
    if (forceActuator && !selection.found) {
        selection.objectId = DIDFT_GETTYPE(object->dwType) |
            DIDFT_MAKEINSTANCE(DIDFT_GETINSTANCE(object->dwType));
        selection.found = true;
    }
    return DIENUM_CONTINUE;
}

void PrintResult(const wchar_t* operation, HRESULT result) {
    std::wcerr << operation << L" failed: 0x"
               << std::hex << static_cast<unsigned long>(result)
               << std::dec << L"\n";
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    const std::wstring_view command = argc >= 2 ? argv[1] : L"";
    TestEffect testEffect = TestEffect::Constant;
    bool hardwareTest = false;
    bool confirmed = false;
    bool allEffects = false;
    bool validArguments = false;
    if (command == L"enumerate") {
        validArguments = argc == 2;
    } else if (command == L"validate") {
        validArguments = argc == 2;
        if (argc == 3) {
            allEffects = wcscmp(argv[2], L"all") == 0;
            validArguments = allEffects || TryParseEffect(argv[2], testEffect);
        }
    } else if (command == L"pulse") {
        bool invalidOption = false;
        bool targetSeen = false;
        for (int index = 2; index < argc; ++index) {
            const std::wstring_view argument = argv[index];
            if (argument == L"--hardware-test" && !hardwareTest) {
                hardwareTest = true;
            } else if (argument == L"--confirm" && !confirmed) {
                confirmed = true;
            } else if (!targetSeen && argument == L"all") {
                allEffects = true;
                targetSeen = true;
            } else if (!targetSeen && TryParseEffect(argument, testEffect)) {
                targetSeen = true;
            } else {
                invalidOption = true;
            }
        }
        validArguments = hardwareTest && !invalidOption;
    }
    if (!validArguments) {
        std::wcerr << L"usage: LogiControl.DirectInputHarness "
                      L"enumerate\n"
                      L"       LogiControl.DirectInputHarness "
                      L"validate [constant|periodic|spring|damper|all]\n"
                      L"       LogiControl.DirectInputHarness "
                      L"pulse [constant|periodic|spring|damper|all] "
                      L"--hardware-test [--confirm]\n";
        return 64;
    }
    if (command == L"pulse" && !hardwareTest) {
        std::wcerr << L"pulse requires --hardware-test\n";
        return 65;
    }

    IDirectInput8W* directInput = nullptr;
    auto result = DirectInput8Create(
        GetModuleHandleW(nullptr),
        DIRECTINPUT_VERSION,
        IID_IDirectInput8W,
        reinterpret_cast<void**>(&directInput),
        nullptr);
    if (FAILED(result)) {
        PrintResult(L"DirectInput8Create", result);
        return 2;
    }

    DeviceSelection selection{};
    result = directInput->EnumDevices(
        DI8DEVCLASS_GAMECTRL,
        SelectDfgt,
        &selection,
        DIEDFL_ATTACHEDONLY);
    if (FAILED(result) || !selection.found) {
        if (FAILED(result)) PrintResult(L"EnumDevices", result);
        else std::wcerr << L"046D:C29A was not found by DirectInput.\n";
        directInput->Release();
        return 3;
    }

    std::wcout << L"device: " << selection.productName
               << L" / " << selection.instanceName << L" (046D:C29A)\n";

    IDirectInputDevice8W* device = nullptr;
    result = directInput->CreateDevice(selection.instanceGuid, &device, nullptr);
    if (FAILED(result)) {
        PrintResult(L"CreateDevice", result);
        directInput->Release();
        return 4;
    }

    result = device->SetDataFormat(&c_dfDIJoystick2);
    if (FAILED(result)) {
        PrintResult(L"SetDataFormat", result);
        device->Release();
        directInput->Release();
        return 4;
    }

    DIDEVCAPS capabilities{};
    capabilities.dwSize = sizeof(capabilities);
    result = device->GetCapabilities(&capabilities);
    if (FAILED(result)) {
        PrintResult(L"GetCapabilities", result);
    } else {
        std::wcout << L"axes=" << capabilities.dwAxes
                   << L" buttons=" << capabilities.dwButtons
                   << L" povs=" << capabilities.dwPOVs
                   << L" forceFeedback="
                   << ((capabilities.dwFlags & DIDC_FORCEFEEDBACK) != 0)
                   << L"\n";
    }
    ForceAxisSelection forceAxis{};
    result = device->EnumObjects(InspectAxis, &forceAxis, DIDFT_AXIS);
    if (FAILED(result)) PrintResult(L"EnumObjects(DIDFT_AXIS)", result);
    device->EnumEffects(PrintEffect, nullptr, DIEFT_ALL);

    if (command == L"enumerate") {
        device->Release();
        directInput->Release();
        return 0;
    }

    if (!forceAxis.found) {
        std::wcerr << L"No DirectInput axis is registered as a force actuator.\n";
        device->Release();
        directInput->Release();
        return 67;
    }

    if (command == L"pulse" && !confirmed) {
        if (allEffects) {
            std::wcout << L"This will run constant, sine-periodic, spring, and damper "
                          L"at a 30% ceiling for 1 second each. Type YES once: "
                       << std::flush;
        } else {
            std::wcout << EffectPrompt(testEffect) << L" Type YES: "
                       << std::flush;
        }
        std::wstring confirmation;
        std::getline(std::wcin, confirmation);
        if (confirmation != L"YES") {
            std::wcerr << L"Cancelled.\n";
            device->Release();
            directInput->Release();
            return 66;
        }
    }

    HarnessWindow window;
    if (window.Get() == nullptr) {
        std::wcerr << L"CreateWindowExW failed: " << GetLastError() << L"\n";
        device->Release();
        directInput->Release();
        return 5;
    }

    auto runStep = [&result](const wchar_t* operation, auto&& action) {
        if (FAILED(result)) return;
        result = action();
        if (FAILED(result)) PrintResult(operation, result);
    };

    runStep(L"SetCooperativeLevel", [&] {
        return device->SetCooperativeLevel(
            window.Get(),
            DISCL_EXCLUSIVE | DISCL_BACKGROUND);
    });
    runStep(L"Acquire", [&] { return device->Acquire(); });
    runStep(L"SendForceFeedbackCommand(DISFFC_RESET)", [&] {
        return device->SendForceFeedbackCommand(DISFFC_RESET);
    });

    std::array<TestEffect, 4> effectsToRun{
        testEffect, TestEffect::Constant, TestEffect::Constant, TestEffect::Constant};
    std::size_t effectCount = 1;
    if (allEffects) {
        effectsToRun = {
            TestEffect::Constant,
            TestEffect::Periodic,
            TestEffect::Spring,
            TestEffect::Damper};
        effectCount = effectsToRun.size();
    }

    for (std::size_t index = 0; index < effectCount && SUCCEEDED(result); ++index) {
        const auto currentEffect = effectsToRun[index];
        if (command == L"pulse" && allEffects) {
            std::wcout << L"Next: " << EffectPrompt(currentEffect)
                       << L" Starting in 2 seconds.\n" << std::flush;
            std::this_thread::sleep_for(std::chrono::seconds(2));
        }

        DWORD axis = forceAxis.objectId;
        LONG direction = 10000;
        DICONSTANTFORCE constantForce{3000};
        DIPERIODIC periodicForce{3000, 0, 0, 200000};
        DICONDITION conditionForce{0, 3000, 3000, 3000, 3000, 0};
        const GUID* effectGuid = &GUID_ConstantForce;
        DWORD parameterSize = sizeof(constantForce);
        void* parameters = &constantForce;
        switch (currentEffect) {
        case TestEffect::Constant:
            break;
        case TestEffect::Periodic:
            effectGuid = &GUID_Sine;
            parameterSize = sizeof(periodicForce);
            parameters = &periodicForce;
            break;
        case TestEffect::Spring:
            effectGuid = &GUID_Spring;
            parameterSize = sizeof(conditionForce);
            parameters = &conditionForce;
            break;
        case TestEffect::Damper:
            effectGuid = &GUID_Damper;
            parameterSize = sizeof(conditionForce);
            parameters = &conditionForce;
            break;
        }

        DIEFFECT definition{};
        definition.dwSize = sizeof(definition);
        definition.dwFlags = DIEFF_CARTESIAN | DIEFF_OBJECTIDS;
        definition.dwDuration = 1000000;
        definition.dwGain = DI_FFNOMINALMAX;
        definition.dwTriggerButton = DIEB_NOTRIGGER;
        definition.cAxes = 1;
        definition.rgdwAxes = &axis;
        definition.rglDirection = &direction;
        definition.cbTypeSpecificParams = parameterSize;
        definition.lpvTypeSpecificParams = parameters;

        IDirectInputEffect* effect = nullptr;
        runStep(L"CreateEffect", [&] {
            return device->CreateEffect(*effectGuid, &definition, &effect, nullptr);
        });
        if (command == L"validate") {
            if (SUCCEEDED(result)) {
                std::wcout << EffectName(currentEffect)
                           << L" effect creation succeeded; effect was not started.\n";
            }
        } else {
            runStep(L"Start", [&] { return effect->Start(1, 0); });
            if (SUCCEEDED(result)) {
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
        }

        if (effect != nullptr) {
            effect->Stop();
            effect->Unload();
            effect->Release();
        }
        device->SendForceFeedbackCommand(DISFFC_STOPALL);
    }
    device->Unacquire();
    device->Release();
    directInput->Release();
    return SUCCEEDED(result) ? 0 : 5;
}
