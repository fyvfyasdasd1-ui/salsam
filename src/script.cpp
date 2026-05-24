#include "script.h"
#include "keyboard.h"

#include "..\\include\\inc\\natives.h"
#include "..\\include\\inc\\types.h"
#include "..\\include\\inc\\enums.h"
#include "..\\include\\inc\\main.h"

#include <Windows.h>
#include <cmath>
#include <cstdio>
#include <vector>
#include <algorithm>

namespace {

// Standard GTA V ped skeleton bone IDs.
// Source: alloc8or.re/gta5/nativedb + community modding references.
constexpr int BONE_HEAD       = 31086;
constexpr int BONE_NECK       = 39317;
constexpr int BONE_SPINE3     = 24818;
constexpr int BONE_SPINE0     = 57597;
constexpr int BONE_L_CLAVICLE = 64729;
constexpr int BONE_R_CLAVICLE = 10706;
constexpr int BONE_L_UPPERARM = 45509;
constexpr int BONE_R_UPPERARM = 40269;
constexpr int BONE_L_FOREARM  = 61163;
constexpr int BONE_R_FOREARM  = 28252;
constexpr int BONE_L_HAND     = 18905;
constexpr int BONE_R_HAND     = 57005;
constexpr int BONE_L_THIGH    = 58271;
constexpr int BONE_R_THIGH    = 51826;
constexpr int BONE_L_CALF     = 63931;
constexpr int BONE_R_CALF     = 36864;
constexpr int BONE_L_FOOT     = 14201;
constexpr int BONE_R_FOOT     = 52301;

struct BoneEdge {
    int a;
    int b;
};

const BoneEdge kSkeleton[] = {
    // Head + spine
    { BONE_HEAD,       BONE_NECK     },
    { BONE_NECK,       BONE_SPINE3   },
    { BONE_SPINE3,     BONE_SPINE0   },
    // Shoulders
    { BONE_NECK,       BONE_L_CLAVICLE },
    { BONE_NECK,       BONE_R_CLAVICLE },
    // Left arm
    { BONE_L_CLAVICLE, BONE_L_UPPERARM },
    { BONE_L_UPPERARM, BONE_L_FOREARM  },
    { BONE_L_FOREARM,  BONE_L_HAND     },
    // Right arm
    { BONE_R_CLAVICLE, BONE_R_UPPERARM },
    { BONE_R_UPPERARM, BONE_R_FOREARM  },
    { BONE_R_FOREARM,  BONE_R_HAND     },
    // Hips
    { BONE_SPINE0,     BONE_L_THIGH    },
    { BONE_SPINE0,     BONE_R_THIGH    },
    // Left leg
    { BONE_L_THIGH,    BONE_L_CALF     },
    { BONE_L_CALF,     BONE_L_FOOT     },
    // Right leg
    { BONE_R_THIGH,    BONE_R_CALF     },
    { BONE_R_CALF,     BONE_R_FOOT     },
};

struct Settings {
    bool  espEnabled       = true;
    bool  aimAssistEnabled = false;
    bool  showFriendly     = false;
    float maxDistance      = 150.0f;
    int   distanceStep     = 1; // 0 = 50m, 1 = 150m, 2 = 500m
} g_settings;

struct Color { int r, g, b, a; };

constexpr Color kHostile  { 240,  60,  60, 220 };
constexpr Color kNeutral  { 240, 220,  70, 200 };
constexpr Color kFriendly {  70, 220, 100, 200 };
constexpr Color kDead     { 140, 140, 140, 160 };

void Notify(const char* text) {
    UI::_SET_NOTIFICATION_TEXT_ENTRY("STRING");
    UI::_ADD_TEXT_COMPONENT_STRING(const_cast<char*>(text));
    UI::_DRAW_NOTIFICATION(false, false);
}

float Dist3(const Vector3& a, const Vector3& b) {
    float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrt(dx*dx + dy*dy + dz*dz);
}

Color ColorForPed(Ped ped, Ped player) {
    if (ENTITY::IS_ENTITY_DEAD(ped)) return kDead;
    int rel = PED::GET_RELATIONSHIP_BETWEEN_PEDS(ped, player);
    // 4 = neutral, 5 = dislike, 3 = like, 1 = respect, 2 = companion,
    // 255 = pedestrians; values >=4 generally treated as not-friendly.
    if (rel == 5 || rel == 6 || rel == 7) return kHostile;
    if (rel == 1 || rel == 2 || rel == 3) return kFriendly;
    return kNeutral;
}

void DrawText3D(const Vector3& world, const char* text, const Color& c) {
    float sx, sy;
    if (!GRAPHICS::_WORLD3D_TO_SCREEN2D(world.x, world.y, world.z, &sx, &sy)) {
        return;
    }
    UI::SET_TEXT_FONT(0);
    UI::SET_TEXT_SCALE(0.30f, 0.30f);
    UI::SET_TEXT_COLOUR(c.r, c.g, c.b, c.a);
    UI::SET_TEXT_OUTLINE();
    UI::SET_TEXT_CENTRE(true);
    UI::_SET_TEXT_ENTRY("STRING");
    UI::_ADD_TEXT_COMPONENT_STRING(const_cast<char*>(text));
    UI::_DRAW_TEXT(sx, sy);
}

void DrawHealthBar(const Vector3& headWorld, float pct, const Color& c) {
    float sx, sy;
    if (!GRAPHICS::_WORLD3D_TO_SCREEN2D(headWorld.x, headWorld.y, headWorld.z + 0.35f, &sx, &sy)) {
        return;
    }
    const float w = 0.04f;
    const float h = 0.005f;
    GRAPHICS::DRAW_RECT(sx, sy, w + 0.004f, h + 0.002f, 0, 0, 0, 200);
    GRAPHICS::DRAW_RECT(sx - (w * (1.0f - pct)) * 0.5f, sy,
                        w * pct, h, c.r, c.g, c.b, c.a);
}

void DrawPedSkeleton(Ped ped, const Color& c) {
    for (const BoneEdge& e : kSkeleton) {
        Vector3 a = PED::GET_PED_BONE_COORDS(ped, e.a, 0, 0, 0);
        Vector3 b = PED::GET_PED_BONE_COORDS(ped, e.b, 0, 0, 0);
        GRAPHICS::DRAW_LINE(a.x, a.y, a.z, b.x, b.y, b.z, c.r, c.g, c.b, c.a);
    }
}

bool PedShouldBeDrawn(Ped ped, Ped player, const Vector3& playerPos,
                      Vector3* outHead, float* outDist) {
    if (ped == player) return false;
    if (!ENTITY::DOES_ENTITY_EXIST(ped)) return false;
    if (!PED::IS_PED_HUMAN(ped)) return false;

    Vector3 head = PED::GET_PED_BONE_COORDS(ped, BONE_HEAD, 0, 0, 0);
    float d = Dist3(head, playerPos);
    if (d > g_settings.maxDistance) return false;

    *outHead = head;
    *outDist = d;
    return true;
}

// World-to-screen projection — `_WORLD3D_TO_SCREEN2D` returns coords in
// normalized [0,1]; we convert to a (-1..1) centered space for the FOV test.
bool CrosshairOffset(const Vector3& world, float* outDx, float* outDy) {
    float sx, sy;
    if (!GRAPHICS::_WORLD3D_TO_SCREEN2D(world.x, world.y, world.z, &sx, &sy)) {
        return false;
    }
    *outDx = (sx - 0.5f);
    *outDy = (sy - 0.5f);
    return true;
}

Ped PickAimTarget(Ped player, const Vector3& playerPos) {
    constexpr int kMaxPeds = 256;
    Ped peds[kMaxPeds];
    int n = worldGetAllPeds(peds, kMaxPeds);

    Ped best = 0;
    float bestScore = 0.04f * 0.04f; // ~4% of screen radius — tight FOV
    for (int i = 0; i < n; ++i) {
        Ped ped = peds[i];
        if (ped == player) continue;
        if (!ENTITY::DOES_ENTITY_EXIST(ped)) continue;
        if (ENTITY::IS_ENTITY_DEAD(ped)) continue;
        if (!PED::IS_PED_HUMAN(ped)) continue;

        Color c = ColorForPed(ped, player);
        // Only assist against hostiles — refuse to "lock" onto friendlies/civilians.
        if (!(c.r == kHostile.r && c.g == kHostile.g && c.b == kHostile.b)) continue;

        Vector3 head = PED::GET_PED_BONE_COORDS(ped, BONE_HEAD, 0, 0, 0);
        if (Dist3(head, playerPos) > g_settings.maxDistance) continue;

        // Require a clear line of sight so the assist doesn't hug peds through walls.
        if (!ENTITY::HAS_ENTITY_CLEAR_LOS_TO_ENTITY(player, ped, 17)) continue;

        float dx, dy;
        if (!CrosshairOffset(head, &dx, &dy)) continue;
        float score = dx*dx + dy*dy;
        if (score < bestScore) {
            bestScore = score;
            best = ped;
        }
    }
    return best;
}

float WrapHeading(float h) {
    while (h >  180.0f) h -= 360.0f;
    while (h < -180.0f) h += 360.0f;
    return h;
}

void TickAimAssist(Ped player, const Vector3& playerPos) {
    if (!g_settings.aimAssistEnabled) return;
    if (!PLAYER::IS_PLAYER_FREE_AIMING(PLAYER::PLAYER_ID())) return;

    Ped target = PickAimTarget(player, playerPos);
    if (target == 0) return;

    Vector3 head = PED::GET_PED_BONE_COORDS(target, BONE_HEAD, 0, 0, 0);
    Vector3 cam  = CAM::GET_GAMEPLAY_CAM_COORD();

    float dx = head.x - cam.x;
    float dy = head.y - cam.y;
    float dz = head.z - cam.z;

    float desiredHeading = std::atan2(-dx, dy) * (180.0f / 3.14159265f);
    float horizDist = std::sqrt(dx*dx + dy*dy);
    float desiredPitch = std::atan2(dz, horizDist) * (180.0f / 3.14159265f);

    float curHeading = CAM::GET_GAMEPLAY_CAM_RELATIVE_HEADING() +
                       ENTITY::GET_ENTITY_HEADING(player);
    float curPitch   = CAM::GET_GAMEPLAY_CAM_RELATIVE_PITCH();

    float dHeading = WrapHeading(desiredHeading - curHeading);
    float dPitch   = desiredPitch - curPitch;

    // Cap the per-frame adjustment so it acts as an assist (smooth nudge),
    // not a snap. ~6 degrees/frame at 60fps ≈ aim-assist tier.
    const float kMaxStep = 6.0f;
    if (dHeading >  kMaxStep) dHeading =  kMaxStep;
    if (dHeading < -kMaxStep) dHeading = -kMaxStep;
    if (dPitch   >  kMaxStep) dPitch   =  kMaxStep;
    if (dPitch   < -kMaxStep) dPitch   = -kMaxStep;

    float newHeading = curHeading + dHeading;
    float newPitch   = curPitch   + dPitch;

    CAM::SET_GAMEPLAY_CAM_RELATIVE_HEADING(newHeading - ENTITY::GET_ENTITY_HEADING(player));
    CAM::SET_GAMEPLAY_CAM_RELATIVE_PITCH(newPitch, 1.0f);
}

void TickEsp(Ped player, const Vector3& playerPos) {
    if (!g_settings.espEnabled) return;

    constexpr int kMaxPeds = 256;
    Ped peds[kMaxPeds];
    int n = worldGetAllPeds(peds, kMaxPeds);

    for (int i = 0; i < n; ++i) {
        Ped ped = peds[i];
        Vector3 head;
        float dist;
        if (!PedShouldBeDrawn(ped, player, playerPos, &head, &dist)) continue;

        Color c = ColorForPed(ped, player);
        bool isFriendly = (c.r == kFriendly.r && c.g == kFriendly.g && c.b == kFriendly.b);
        if (isFriendly && !g_settings.showFriendly) continue;

        DrawPedSkeleton(ped, c);

        int hp    = ENTITY::GET_ENTITY_HEALTH(ped);
        int hpMax = ENTITY::GET_ENTITY_MAX_HEALTH(ped);
        float pct = hpMax > 0 ? std::max(0.0f, std::min(1.0f, (float)hp / (float)hpMax))
                              : 0.0f;
        DrawHealthBar(head, pct, c);

        char buf[64];
        std::snprintf(buf, sizeof(buf), "%.0fm", dist);
        Vector3 label = head;
        label.z += 0.55f;
        DrawText3D(label, buf, c);
    }
}

void HandleInput() {
    if (IsKeyJustUp(VK_F5)) {
        g_settings.espEnabled = !g_settings.espEnabled;
        Notify(g_settings.espEnabled ? "BoneESP: ESP ON" : "BoneESP: ESP OFF");
    }
    if (IsKeyJustUp(VK_F6)) {
        g_settings.aimAssistEnabled = !g_settings.aimAssistEnabled;
        Notify(g_settings.aimAssistEnabled
               ? "BoneESP: aim assist ON (hostiles only, SP)"
               : "BoneESP: aim assist OFF");
    }
    if (IsKeyJustUp(VK_F7)) {
        g_settings.distanceStep = (g_settings.distanceStep + 1) % 3;
        switch (g_settings.distanceStep) {
            case 0: g_settings.maxDistance =  50.0f; Notify("BoneESP: range 50m");  break;
            case 1: g_settings.maxDistance = 150.0f; Notify("BoneESP: range 150m"); break;
            case 2: g_settings.maxDistance = 500.0f; Notify("BoneESP: range 500m"); break;
        }
    }
    if (IsKeyJustUp(VK_F8)) {
        g_settings.showFriendly = !g_settings.showFriendly;
        Notify(g_settings.showFriendly
               ? "BoneESP: showing friendlies"
               : "BoneESP: hiding friendlies");
    }
}

bool OnlineGuard() {
    // ScriptHookV refuses to load when GTA Online is active, but we add a
    // belt-and-braces check in case the game state changes mid-session.
    if (NETWORK::NETWORK_IS_SESSION_STARTED()) {
        static bool warned = false;
        if (!warned) {
            Notify("BoneESP disabled: online session detected.");
            warned = true;
        }
        return false;
    }
    return true;
}

} // namespace

void ScriptMain() {
    Notify("BoneESP loaded — F5 ESP, F6 aim assist, F7 range, F8 friendlies.");

    for (;;) {
        WAIT(0);

        if (!OnlineGuard()) continue;

        HandleInput();

        Player playerId = PLAYER::PLAYER_ID();
        if (!PLAYER::IS_PLAYER_CONTROL_ON(playerId)) continue;

        Ped player = PLAYER::PLAYER_PED_ID();
        if (!ENTITY::DOES_ENTITY_EXIST(player)) continue;
        if (ENTITY::IS_ENTITY_DEAD(player)) continue;

        Vector3 playerPos = ENTITY::GET_ENTITY_COORDS(player, TRUE);

        TickEsp(player, playerPos);
        TickAimAssist(player, playerPos);
    }
}
