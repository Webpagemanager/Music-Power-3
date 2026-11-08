#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProgressBarLayoutResult {
    pub track_thickness: f64,
    pub track_corner_radius: f64,
    pub fill_length: f64,
    pub thumb_offset_x: f64,
    pub thumb_offset_y: f64,
    pub thumb_core_size: f64,
    pub thumb_outer_size: f64,
    pub thumb_opacity: f64,
    pub is_thumb_visible: bool,
}

const REST_THICKNESS: f64 = 4.0;
const HOVER_THICKNESS: f64 = 6.0;
const THUMB_SIZE: f64 = 20.0;
const THUMB_CORE_REST: f64 = 12.0;
const THUMB_CORE_HOVER: f64 = 14.0;
const THUMB_CORE_PRESSED: f64 = 10.0;

pub fn calculate_progress_layout(
    value: f64,
    minimum: f64,
    maximum: f64,
    available_width: f64,
    available_height: f64,
    orientation: i32, // 0 = Horizontal, 1 = Vertical
    is_hovered: bool,
    is_scrubbing: bool,
    is_pressed: bool,
    is_thumb_always_visible: bool,
) -> ProgressBarLayoutResult {
    let range = maximum - minimum;
    let mut percentage = 0.0;
    if range > 0.0 {
        percentage = ((value - minimum) / range).clamp(0.0, 1.0);
    }

    let active = is_hovered || is_scrubbing;
    let thickness = if active { HOVER_THICKNESS } else { REST_THICKNESS };
    let track_corner_radius = thickness * 0.5;
    let thumb_outer_size = THUMB_SIZE;

    let thumb_core_size = if is_pressed || is_scrubbing {
        THUMB_CORE_PRESSED
    } else if is_hovered {
        THUMB_CORE_HOVER
    } else {
        THUMB_CORE_REST
    };

    let show_thumb = is_thumb_always_visible || is_hovered || is_scrubbing;
    let thumb_opacity = if show_thumb { 1.0 } else { 0.0 };

    let half = THUMB_SIZE * 0.5;

    let (fill_length, thumb_offset_x, thumb_offset_y) = if orientation == 0 {
        // Horizontal
        let width = available_width.max(0.0);
        let fill = percentage * width;
        let target_x = (percentage * width) - half;
        let min_x = -half;
        let max_x = if width > half { width - half } else { -half };
        (fill, target_x.clamp(min_x, max_x), 0.0)
    } else {
        // Vertical
        let height = available_height.max(0.0);
        let fill = percentage * height;
        let target_y = height - (percentage * height) - half;
        let min_y = -half;
        let max_y = if height > half { height - half } else { -half };
        (fill, 0.0, target_y.clamp(min_y, max_y))
    };

    ProgressBarLayoutResult {
        track_thickness: thickness,
        track_corner_radius,
        fill_length,
        thumb_offset_x,
        thumb_offset_y,
        thumb_core_size,
        thumb_outer_size,
        thumb_opacity,
        is_thumb_visible: show_thumb,
    }
}

pub fn calculate_value_from_pointer(
    pointer_x: f64,
    pointer_y: f64,
    available_width: f64,
    available_height: f64,
    orientation: i32,
    minimum: f64,
    maximum: f64,
    _step_frequency: f64,
) -> f64 {
    let range = maximum - minimum;
    if range <= 0.0 {
        return minimum;
    }

    let percentage = if orientation == 0 {
        if available_width > 0.0 {
            (pointer_x / available_width).clamp(0.0, 1.0)
        } else {
            0.0
        }
    } else if available_height > 0.0 {
        (1.0 - (pointer_y / available_height)).clamp(0.0, 1.0)
    } else {
        0.0
    };

    minimum + (percentage * range)
}

