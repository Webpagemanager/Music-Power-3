#include "NativeProgressBar.h"
#include <cmath>
#include <algorithm>

constexpr double RestThickness = 4.0;
constexpr double HoverThickness = 6.0;
constexpr double ThumbSize = 20.0;
constexpr double ThumbCoreRest = 12.0;
constexpr double ThumbCoreHover = 14.0;
constexpr double ThumbCorePressed = 10.0;

extern "C" {

void NativeProgressBar_CalculateLayout(
    double value,
    double minimum,
    double maximum,
    double availableWidth,
    double availableHeight,
    int orientation,
    bool isHovered,
    bool isScrubbing,
    bool isPressed,
    bool isThumbAlwaysVisible,
    ProgressBarLayoutResult* outResult)
{
    if (!outResult) return;

    double range = maximum - minimum;
    double percentage = 0.0;
    if (range > 0.0) {
        percentage = (value - minimum) / range;
        if (percentage < 0.0) percentage = 0.0;
        if (percentage > 1.0) percentage = 1.0;
    }

    bool active = isHovered || isScrubbing;
    double thickness = active ? HoverThickness : RestThickness;
    outResult->TrackThickness = thickness;
    outResult->TrackCornerRadius = thickness * 0.5;
    outResult->ThumbOuterSize = ThumbSize;

    double core = isPressed || isScrubbing ? ThumbCorePressed : (isHovered ? ThumbCoreHover : ThumbCoreRest);
    outResult->ThumbCoreSize = core;

    bool showThumb = isThumbAlwaysVisible || isHovered || isScrubbing;
    outResult->ThumbOpacity = showThumb ? 1.0 : 0.0;
    outResult->IsThumbVisible = showThumb;

    double half = ThumbSize * 0.5;

    if (orientation == 0) // Horizontal
    {
        double width = (availableWidth > 0.0) ? availableWidth : 0.0;
        outResult->FillLength = percentage * width;

        double targetX = (percentage * width) - half;
        double minX = -half;
        double maxX = (width > half) ? (width - half) : -half;
        outResult->ThumbOffsetX = std::clamp(targetX, minX, maxX);
        outResult->ThumbOffsetY = 0.0;
    }
    else // Vertical
    {
        double height = (availableHeight > 0.0) ? availableHeight : 0.0;
        outResult->FillLength = percentage * height;

        double targetY = height - (percentage * height) - half;
        double minY = -half;
        double maxY = (height > half) ? (height - half) : -half;
        outResult->ThumbOffsetY = std::clamp(targetY, minY, maxY);
        outResult->ThumbOffsetX = 0.0;
    }
}

double NativeProgressBar_CalculateValueFromPointer(
    double pointerX,
    double pointerY,
    double availableWidth,
    double availableHeight,
    int orientation,
    double minimum,
    double maximum,
    double stepFrequency)
{
    double range = maximum - minimum;
    if (range <= 0.0) return minimum;

    double percentage = 0.0;
    if (orientation == 0) // Horizontal
    {
        if (availableWidth > 0.0) {
            percentage = pointerX / availableWidth;
        }
    }
    else // Vertical
    {
        if (availableHeight > 0.0) {
            percentage = 1.0 - (pointerY / availableHeight);
        }
    }

    percentage = std::clamp(percentage, 0.0, 1.0);
    double rawValue = minimum + (percentage * range);

    if (stepFrequency > 0.0) {
        rawValue = std::round((rawValue - minimum) / stepFrequency) * stepFrequency + minimum;
    }

    return std::clamp(rawValue, minimum, maximum);
}

}
