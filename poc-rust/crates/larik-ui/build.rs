use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    println!("cargo:rerun-if-changed=assets/AppIcon.ico");

    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    // Windows defaults to a 1 MB main-thread stack (vs 8 MB on macOS/Linux).
    // Unoptimized GPUI debug render frames (deep Div/StyleRefinement chains such as
    // Results -> Preview Pane or Settings -> Snippets Editor) can exceed 1 MB on
    // the main thread. Reserve a 16 MB virtual stack (committed lazily on demand).
    const WINDOWS_STACK_RESERVE_BYTES: usize = 16 * 1024 * 1024;
    match env::var("CARGO_CFG_TARGET_ENV").as_deref() {
        Ok("msvc") => {
            println!("cargo:rustc-link-arg-bin=larik-ui=/STACK:{WINDOWS_STACK_RESERVE_BYTES}");
        }
        Ok("gnu") => {
            println!(
                "cargo:rustc-link-arg-bin=larik-ui=-Wl,--stack,{WINDOWS_STACK_RESERVE_BYTES}"
            );
        }
        _ => {}
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let ico_path = manifest_dir.join("assets").join("AppIcon.ico");
    if !ico_path.exists() {
        return;
    }

    let rc_file = out_dir.join("larik_icon.rc");
    let res_file = out_dir.join("larik_icon.res");

    // Resource ID 1 matches gpui-pre-windows `LoadImageW(..., PCWSTR(1), IMAGE_ICON, ...)`
    // and is also the primary icon Windows Explorer uses for the executable.
    let escaped_ico = ico_path.to_string_lossy().replace('\\', "\\\\");
    let rc_contents = format!("1 ICON \"{escaped_ico}\"\n");
    if fs::write(&rc_file, rc_contents).is_err() {
        return;
    }

    let Some(rc_exe) = find_rc_exe() else {
        println!("cargo:warning=Could not locate rc.exe; skipping Windows icon embedding.");
        return;
    };

    let status = Command::new(&rc_exe)
        .arg("/nologo")
        .arg("/fo")
        .arg(&res_file)
        .arg(&rc_file)
        .status();

    match status {
        Ok(s) if s.success() && res_file.exists() => {
            println!("cargo:rustc-link-arg-bin=larik-ui={}", res_file.display());
        }
        other => {
            println!("cargo:warning=rc.exe failed to compile icon resource: {other:?}");
        }
    }
}

fn find_rc_exe() -> Option<PathBuf> {
    if Command::new("rc.exe").arg("/?").output().is_ok() {
        return Some(PathBuf::from("rc.exe"));
    }

    let arch = match env::var("CARGO_CFG_TARGET_ARCH").as_deref() {
        Ok("aarch64") => "arm64",
        Ok("x86") => "x86",
        _ => "x64",
    };

    let kits_bin = Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin");
    let mut versions: Vec<PathBuf> = fs::read_dir(kits_bin)
        .ok()?
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.is_dir())
        .collect();
    versions.sort();

    for ver in versions.into_iter().rev() {
        let candidate = ver.join(arch).join("rc.exe");
        if candidate.exists() {
            return Some(candidate);
        }
    }
    None
}
