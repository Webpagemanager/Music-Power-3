use std::path::PathBuf;
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::UI::Shell::{FileOpenDialog, IFileOpenDialog, FOS_PICKFOLDERS, SIGDN_FILESYSPATH};

pub fn pick_folder() -> Option<PathBuf> {
    unsafe {
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER).ok()?;
        dialog.SetOptions(FOS_PICKFOLDERS).ok()?;
        dialog.Show(None).ok()?;
        let item = dialog.GetResult().ok()?;
        let display_name = item.GetDisplayName(SIGDN_FILESYSPATH).ok()?;
        let path_str = display_name.to_string().ok()?;
        Some(PathBuf::from(path_str))
    }
}
