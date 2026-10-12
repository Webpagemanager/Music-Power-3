#pragma once
#include <windows.h>

#pragma pack(push, 8)
struct ProgressBarLayoutResult {
    double TrackThickness;       // 4.0 rest, 6.0 hover/scrub
    double TrackCornerRadius;    // Thickness / 2.0
    double FillLength;           // Active fill dimension (width or height)
    double ThumbOffsetX;         // Absolute X offset for thumb container
    double ThumbOffsetY;         // Absolute Y offset for thumb container
    double ThumbCoreSize;        // 12.0 rest, 14.0 hover, 10.0 pressed
    double ThumbOuterSize;       // 20.0
    double ThumbOpacity;         // 1.0 when hovered/scrubbing/forced, 0.0 otherwise
    bool   IsThumbVisible;       // true if ThumbOpacity > 0.0
};
#pragma pack(pop)

extern "C" {
    __declspec(dllexport) void NativeProgressBar_CalculateLayout(
        double value,
        double minimum,
        double maximum,
        double availableWidth,
        double availableHeight,
        int orientation, // 0 = Horizontal, 1 = Vertical
        bool isHovered,
        bool isScrubbing,
        bool isPressed,
        bool isThumbAlwaysVisible,
        ProgressBarLayoutResult* outResult
    );

    __declspec(dllexport) double NativeProgressBar_CalculateValueFromPointer(
        double pointerX,
        double pointerY,
        double availableWidth,
        double availableHeight,
        int orientation,
        double minimum,
        double maximum,
        double stepFrequency
    );
}
