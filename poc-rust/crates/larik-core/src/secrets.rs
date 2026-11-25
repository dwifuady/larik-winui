use std::collections::HashMap;
use std::sync::Mutex;

/// Stable target key used to store one workspace's provider password in the secret store.
/// Isolated under `Larik.SqlStudio.PoC` so it never collides with the WinUI app's credentials.
pub fn secret_target(space_id: &str, key: &str) -> String {
    format!("Larik.SqlStudio.PoC/space/{space_id}/{key}")
}

pub trait SecretBackend: Send + Sync {
    fn get(&self, target: &str) -> anyhow::Result<Option<String>>;
    fn set(&self, target: &str, value: &str) -> anyhow::Result<()>;
    fn delete(&self, target: &str) -> anyhow::Result<()>;
}

/// Windows Credential Manager backend (`CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`).
#[derive(Debug, Default, Clone, Copy)]
pub struct WindowsCredentialBackend;

#[cfg(windows)]
mod win_impl {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr;

    use anyhow::Context;
    use windows_sys::Win32::Foundation::{ERROR_NOT_FOUND, GetLastError};
    use windows_sys::Win32::Security::Credentials::{
        CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredDeleteW, CredFree,
        CredReadW, CredWriteW,
    };

    fn to_wide_null(s: &str) -> Vec<u16> {
        OsStr::new(s).encode_wide().chain(Some(0)).collect()
    }

    pub fn read_secret(target: &str) -> anyhow::Result<Option<String>> {
        let target_w = to_wide_null(target);
        let mut cred_ptr: *mut CREDENTIALW = ptr::null_mut();

        // SAFETY: `target_w` is null-terminated UTF-16 and `cred_ptr` receives a Windows-allocated
        // `CREDENTIALW` pointer that we immediately copy from and free with `CredFree`.
        let ok = unsafe { CredReadW(target_w.as_ptr(), CRED_TYPE_GENERIC, 0, &mut cred_ptr) };
        if ok == 0 {
            let err = unsafe { GetLastError() };
            if err == ERROR_NOT_FOUND {
                return Ok(None);
            }
            return Err(std::io::Error::from_raw_os_error(err as i32))
                .with_context(|| format!("CredReadW failed for {target}"));
        }

        if cred_ptr.is_null() {
            return Ok(None);
        }

        // SAFETY: `cred_ptr` is valid until `CredFree`.
        let result = unsafe {
            let cred = &*cred_ptr;
            let out = if cred.CredentialBlobSize == 0 || cred.CredentialBlob.is_null() {
                Ok(Some(String::new()))
            } else {
                let bytes = std::slice::from_raw_parts(
                    cred.CredentialBlob,
                    cred.CredentialBlobSize as usize,
                );
                String::from_utf8(bytes.to_vec())
                    .map(Some)
                    .with_context(|| format!("Credential blob for {target} is not valid UTF-8"))
            };
            CredFree(cred_ptr as *const core::ffi::c_void);
            out
        };

        result
    }

    pub fn write_secret(target: &str, value: &str) -> anyhow::Result<()> {
        let mut target_w = to_wide_null(target);
        let mut user_w = to_wide_null("larik");
        let mut blob = value.as_bytes().to_vec();
        let blob_len = u32::try_from(blob.len())
            .with_context(|| format!("Secret value for {target} exceeds u32 length"))?;

        let cred = CREDENTIALW {
            Flags: 0,
            Type: CRED_TYPE_GENERIC,
            TargetName: target_w.as_mut_ptr(),
            Comment: ptr::null_mut(),
            LastWritten: unsafe { core::mem::zeroed() },
            CredentialBlobSize: blob_len,
            CredentialBlob: if blob.is_empty() {
                ptr::null_mut()
            } else {
                blob.as_mut_ptr()
            },
            Persist: CRED_PERSIST_LOCAL_MACHINE,
            AttributeCount: 0,
            Attributes: ptr::null_mut(),
            TargetAlias: ptr::null_mut(),
            UserName: user_w.as_mut_ptr(),
        };

        // SAFETY: All pointers in `cred` remain alive across `CredWriteW`.
        let ok = unsafe { CredWriteW(&cred, 0) };
        if ok == 0 {
            let err = unsafe { GetLastError() };
            return Err(std::io::Error::from_raw_os_error(err as i32))
                .with_context(|| format!("CredWriteW failed for {target}"));
        }

        Ok(())
    }

    pub fn delete_secret(target: &str) -> anyhow::Result<()> {
        let target_w = to_wide_null(target);
        // SAFETY: `target_w` is null-terminated UTF-16.
        let ok = unsafe { CredDeleteW(target_w.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok == 0 {
            let err = unsafe { GetLastError() };
            if err == ERROR_NOT_FOUND {
                return Ok(());
            }
            return Err(std::io::Error::from_raw_os_error(err as i32))
                .with_context(|| format!("CredDeleteW failed for {target}"));
        }
        Ok(())
    }
}

impl SecretBackend for WindowsCredentialBackend {
    fn get(&self, target: &str) -> anyhow::Result<Option<String>> {
        #[cfg(windows)]
        {
            win_impl::read_secret(target)
        }
        #[cfg(not(windows))]
        {
            let _ = target;
            Ok(None)
        }
    }

    fn set(&self, target: &str, value: &str) -> anyhow::Result<()> {
        #[cfg(windows)]
        {
            win_impl::write_secret(target, value)
        }
        #[cfg(not(windows))]
        {
            let _ = (target, value);
            Ok(())
        }
    }

    fn delete(&self, target: &str) -> anyhow::Result<()> {
        #[cfg(windows)]
        {
            win_impl::delete_secret(target)
        }
        #[cfg(not(windows))]
        {
            let _ = target;
            Ok(())
        }
    }
}

/// Deterministic in-memory secret backend for unit tests.
#[derive(Debug, Default)]
pub struct InMemorySecretBackend {
    entries: Mutex<HashMap<String, String>>,
}

impl InMemorySecretBackend {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn len(&self) -> usize {
        self.entries.lock().map(|g| g.len()).unwrap_or(0)
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl SecretBackend for InMemorySecretBackend {
    fn get(&self, target: &str) -> anyhow::Result<Option<String>> {
        let guard = self
            .entries
            .lock()
            .map_err(|_| anyhow::anyhow!("InMemorySecretBackend lock poisoned"))?;
        Ok(guard.get(target).cloned())
    }

    fn set(&self, target: &str, value: &str) -> anyhow::Result<()> {
        let mut guard = self
            .entries
            .lock()
            .map_err(|_| anyhow::anyhow!("InMemorySecretBackend lock poisoned"))?;
        guard.insert(target.to_string(), value.to_string());
        Ok(())
    }

    fn delete(&self, target: &str) -> anyhow::Result<()> {
        let mut guard = self
            .entries
            .lock()
            .map_err(|_| anyhow::anyhow!("InMemorySecretBackend lock poisoned"))?;
        guard.remove(target);
        Ok(())
    }
}
