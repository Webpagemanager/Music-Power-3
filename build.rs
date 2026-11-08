fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default() == "windows" {
        let mut res = winres::WindowsResource::new();
        res.set_icon("Assets/icon.ico");
        res.set_manifest_file("app.manifest");
        res.set("ProductName", "Music Power 3");
        res.set("FileDescription", "Music Power 3 - Native WinUI 3 Music Player");
        res.set("LegalCopyright", "Copyright (c) 2026");
        let _ = res.compile();
    }
}

