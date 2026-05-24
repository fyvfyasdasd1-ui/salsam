#include "keyboard.h"

constexpr int KEYS_SIZE = 255;

struct KeyState {
    DWORD time;
    BOOL isWithAlt;
    BOOL wasDownBefore;
    BOOL isUpNow;
};

static KeyState g_keyStates[KEYS_SIZE]{};

void OnKeyboardMessage(DWORD key, WORD /*repeats*/, BYTE /*scanCode*/,
                       BOOL /*isExtended*/, BOOL isWithAlt, BOOL wasDownBefore,
                       BOOL isUpNow) {
    if (key < KEYS_SIZE) {
        g_keyStates[key] = { GetTickCount(), isWithAlt, wasDownBefore, isUpNow };
    }
}

bool IsKeyJustUp(DWORD key, bool exclusive) {
    if (key >= KEYS_SIZE) return false;
    const KeyState& s = g_keyStates[key];
    bool justUp = s.isUpNow && (GetTickCount() < s.time + 200);
    if (justUp && exclusive) ResetKeyState(key);
    return justUp;
}

void ResetKeyState(DWORD key) {
    if (key < KEYS_SIZE) g_keyStates[key] = {};
}
