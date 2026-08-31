// SPDX-License-Identifier: GPL-3.0-or-later
#include <cstdlib>
#include <iostream>

#include "../LogiControl.SemanticIpc/SemanticClient.h"

int wmain() {
    logicontrol::ipc::SemanticClient client;
    const auto result = client.ConnectAndBind(LR"(\\?\hid#vid_046d&pid_c29a#semantic-client-smoke)");
    std::wcout << L"{\"semanticClientResult\":" << static_cast<std::int32_t>(result) << L"}\n";
    client.Close();
    return result == logicontrol::ipc::Result::Ok ? 0 : 1;
}
