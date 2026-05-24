#pragma once

#include <Windows.h>

void OnKeyboardMessage(DWORD key, WORD repeats, BYTE scanCode, BOOL isExtended,
                       BOOL isWithAlt, BOOL wasDownBefore, BOOL isUpNow);

bool IsKeyJustUp(DWORD key, bool exclusive = true);
void ResetKeyState(DWORD key);
