#![windows_subsystem = "windows"]

use music_power_3::platform::{set_app_user_model_id, SingleInstanceGuard};
use music_power_3::ui::MainApp;
use windows_reactor::App;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Single-instance guard
    let _guard = SingleInstanceGuard::try_acquire("MusicPower3.Main");

    // Explicit Windows 11 AUMID for SMTC and Taskbar identity
    set_app_user_model_id();

    // Launch WinUI 3 message loop with declarative MainApp component
    App::run_component::<MainApp>(())?;

    Ok(())
}
