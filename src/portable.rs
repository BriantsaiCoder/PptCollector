// The portable ZIP uses a fixed launcher and a complete app + engine in each slot.
use crate::core::Result;
use semver::Version;
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use std::{
    collections::HashSet,
    env, fs,
    io::{self, BufRead, BufReader, Read, Write},
    os::windows::{
        ffi::OsStrExt,
        fs::{MetadataExt, OpenOptionsExt},
        process::CommandExt,
    },
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{OnceLock, mpsc},
    thread,
    time::{Duration, Instant},
};

const APP: &str = "ppt-collector.exe";
const ENGINE: &str = "engine/PptCollector.Engine.exe";
const VERSION_FILE: &str = "portable-state.json";
const PENDING: &str = "portable-pending.json";
const MODE: &str = "PPTCOLLECTOR_PORTABLE";
const RESTORED: &str = "PPTCOLLECTOR_PORTABLE_RESTORED";
const REPARSE_POINT: u32 = 0x400;
const NO_WINDOW: u32 = 0x0800_0000;
const MAX_EXPANDED_SIZE: u64 = 512 * 1024 * 1024;
pub(crate) const UPDATE_EXIT: i32 = 75;

#[derive(Deserialize, Serialize)]
struct Selection {
    current: String,
    previous: Option<String>,
}

#[derive(Deserialize, Serialize)]
struct Candidate {
    slot: String,
    version: Version,
}

pub(crate) fn runtime_root() -> Option<PathBuf> {
    let exe = env::current_exe().ok()?;
    let versions = exe.parent()?.parent()?;
    if versions.file_name()? != "versions" {
        return None;
    }
    let root = versions.parent()?;
    root.join(VERSION_FILE)
        .is_file()
        .then(|| root.to_path_buf())
}

pub(crate) fn is_trial() -> bool {
    runtime_root().is_some() && env::var(MODE).as_deref() == Ok("trial")
}

pub(crate) fn restored_message() -> Option<String> {
    runtime_root()?;
    let reason = env::var(RESTORED).ok()?;
    Some(format!("新版未能正常啟動，已開回原版本。原因：{reason}"))
}

pub(crate) fn can_stage() -> bool {
    cfg!(target_arch = "x86_64")
        && env::var(MODE)
            .as_deref()
            .is_ok_and(|mode| matches!(mode, "ready" | "trial"))
        && runtime_root().is_some_and(|root| fixed_ntfs(&root))
}

pub(crate) fn manual_reason() -> String {
    env::var(MODE)
        .ok()
        .and_then(|mode| mode.strip_prefix("readonly:").map(str::to_owned))
        .unwrap_or_else(|| "請完整解壓縮新版，再從最外層的 PptCollector.exe 開啟。".into())
}

pub(crate) fn run_launcher() -> bool {
    let Ok(exe) = env::current_exe() else {
        return false;
    };
    let Some(root) = exe.parent().filter(|root| root.join(VERSION_FILE).exists()) else {
        return false;
    };
    if let Err(error) = supervise(root) {
        message(&error);
    }
    true
}

fn supervise(root: &Path) -> Result<()> {
    // Hold an exclusive Windows file handle for the entire app/launcher lifetime.
    let (lock, mode) = if fixed_ntfs(root) {
        match fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .share_mode(0)
            .open(root.join("portable.lock"))
        {
            Ok(lock) => (Some(lock), "ready".to_owned()),
            Err(error) if error.raw_os_error() == Some(32) => {
                return Err("PptCollector 已開啟。".into());
            }
            Err(error) => (
                None,
                format!("readonly:無法寫入這個位置，可手動下載新版。原因：{error}"),
            ),
        }
    } else {
        (
            None,
            "readonly:自動更新需要本機固定 NTFS 磁碟，可手動下載新版。".into(),
        )
    };
    let result = supervise_session(root, &mode, launch);
    drop(lock);
    result
}

fn supervise_session(
    root: &Path,
    mode: &str,
    mut start: impl FnMut(&Path, &str, &str, Option<&str>) -> Result<Child>,
) -> Result<()> {
    let pending = root.join(PENDING);
    let readonly = mode != "ready";
    if !readonly {
        // Only an update exit from this launched process can request a switch.
        let _ = fs::remove_file(&pending);
    }
    let selection: Selection = read_config(&root.join(VERSION_FILE))?;
    let mut child = start(root, &selection.current, mode, None).or_else(|error| {
        let previous = selection.previous.as_deref().ok_or(&error)?;
        start(
            root,
            previous,
            "readonly:這次從上一版開啟，可手動下載新版。",
            Some(&error),
        )
    })?;
    let status = child.wait().map_err(|error| error.to_string())?;
    if readonly || status.code() != Some(UPDATE_EXIT) {
        return Ok(());
    }
    let mut selection = selection;
    loop {
        let candidate = read_config::<Candidate>(&pending);
        let _ = fs::remove_file(&pending);
        let trial = candidate.and_then(|candidate| {
            if candidate.slot == selection.current
                || selection.previous.as_ref() == Some(&candidate.slot)
            {
                return Err("新版資料夾無效。".into());
            }
            let child = start(root, &candidate.slot, "trial", None)?;
            commit_trial(child, &root.join(VERSION_FILE), &selection, candidate)
        });
        let mut child = match trial {
            Ok((child, next)) => {
                selection = next;
                child
            }
            Err(error) => start(root, &selection.current, mode, Some(&error))?,
        };
        let status = child.wait().map_err(|error| error.to_string())?;
        if status.code() != Some(UPDATE_EXIT) {
            return Ok(());
        }
    }
}

fn launch(root: &Path, slot: &str, mode: &str, restored: Option<&str>) -> Result<Child> {
    let path = slot_path(root, slot)?;
    let mut command = Command::new(path.join(APP));
    command
        .current_dir(&path)
        .env(MODE, mode)
        .env_remove(RESTORED)
        .stderr(Stdio::null());
    if let Some(reason) = restored {
        command.env(RESTORED, reason);
    }
    if mode == "trial" {
        command.stdin(Stdio::piped()).stdout(Stdio::piped());
    } else {
        command.stdin(Stdio::null()).stdout(Stdio::null());
    }
    command
        .spawn()
        .map_err(|error| format!("無法啟動 PptCollector：{error}"))
}

fn commit_trial(
    mut child: Child,
    state: &Path,
    selection: &Selection,
    candidate: Candidate,
) -> Result<(Child, Selection)> {
    let expected = ready_message(&candidate.version.to_string());
    let stdout = child.stdout.take().unwrap();
    let (sender, receiver) = mpsc::channel();
    thread::spawn(move || {
        let mut reply = String::new();
        let result = match BufReader::new(stdout.take(4096)).read_line(&mut reply) {
            Ok(0) => Err("新版在準備完成前已關閉。".into()),
            Ok(_) if reply == expected => Ok(()),
            Ok(_) => Err(reply.strip_prefix("error ").map_or_else(
                || "新版回報的版號或啟動訊息有誤。".into(),
                |reason| reason.trim_end().to_owned(),
            )),
            Err(error) => Err(format!("無法讀取新版的啟動回報：{error}")),
        };
        let _ = sender.send(result);
    });

    let next = Selection {
        current: candidate.slot,
        previous: Some(selection.current.clone()),
    };
    let result = (|| {
        receiver
            .recv_timeout(Duration::from_secs(25))
            .map_err(|_| "等候新版確認啟動逾時。")??;
        if child
            .try_wait()
            .map_err(|error| error.to_string())?
            .is_some()
        {
            return Err("新版在切換前已關閉。".into());
        }
        write_config(state, &next).map_err(|error| format!("無法儲存新版選擇：{error}"))?;
        if child.stdin.take().unwrap().write_all(b"1").is_err() {
            write_config(state, selection)?;
            return Err("新版在切換時關閉。".into());
        }
        Ok(())
    })();
    if let Err(error) = result {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    Ok((child, next))
}

fn ready_message(version: &str) -> String {
    format!("PptCollector ready {version}\n")
}

pub(crate) fn confirm_trial(version: &str, show_window: impl FnOnce() -> Result<()>) -> Result<()> {
    static CONFIRMED: OnceLock<Result<()>> = OnceLock::new();
    CONFIRMED
        .get_or_init(|| perform_trial(version, show_window))
        .clone()
}

fn perform_trial(version: &str, show_window: impl FnOnce() -> Result<()>) -> Result<()> {
    let exe = env::current_exe().map_err(|error| error.to_string())?;
    let engine = exe.parent().ok_or("找不到新版所在位置。")?.join(ENGINE);
    let mut stdout = io::stdout().lock();
    if let Err(error) = probe_engine(&engine)
        .map_err(|error| format!("簡報處理元件：{error}"))
        .and_then(|()| show_window().map_err(|error| format!("無法顯示主視窗：{error}")))
    {
        let _ = writeln!(stdout, "error {}", error.replace(['\r', '\n'], " "));
        let _ = stdout.flush();
        return Err(error);
    }
    stdout
        .write_all(ready_message(version).as_bytes())
        .and_then(|_| stdout.flush())
        .map_err(|error| error.to_string())?;
    let mut ack = [0];
    io::stdin()
        .read_exact(&mut ack)
        .map_err(|error| error.to_string())?;
    if ack == [b'1'] {
        Ok(())
    } else {
        Err("無法切換到新版。".into())
    }
}

fn probe_engine(path: &Path) -> Result<()> {
    let mut child = Command::new(path)
        .creation_flags(NO_WINDOW)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| error.to_string())?;
    let deadline = Instant::now() + Duration::from_secs(10);
    let result = (|| {
        child
            .stdin
            .take()
            .unwrap()
            .write_all(b"{\"kind\":\"ci-smoke\"}\n")
            .map_err(|error| error.to_string())?;
        while Instant::now() < deadline {
            if let Some(status) = child.try_wait().map_err(|error| error.to_string())? {
                let reply =
                    serde_json::from_reader::<_, serde_json::Value>(child.stdout.take().unwrap());
                if status.success() && reply.is_ok_and(|r| r["error"] == "不支援的操作。") {
                    return Ok(());
                }
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
        Err("新版簡報處理元件無法正常執行。".into())
    })();
    if result.is_err() {
        let _ = child.kill();
        let _ = child.wait();
    }
    result
}

pub(crate) fn stage(version: &str, bytes: &[u8]) -> Result<()> {
    let root = runtime_root()
        .filter(|_| can_stage())
        .ok_or("這個位置無法自動更新。")?;
    let version = Version::parse(version).map_err(|_| "更新版號格式有誤。")?;
    let versions = root.join("versions");
    if !regular(&versions, true) {
        return Err("找不到免安裝版的版本資料夾。".into());
    }
    let folder = tempfile::Builder::new()
        .prefix("build-")
        .tempdir_in(&versions)
        .map_err(|error| format!("無法建立更新資料夾：{error}"))?;
    unpack(bytes, folder.path())?;
    let slot = folder
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    write_config(&root.join(PENDING), &Candidate { slot, version })?;
    // ponytail: keep previous slots since a user can save documents inside a slot.
    // Prune only after recording which files were created by the updater.
    let _ = folder.keep();
    Ok(())
}

fn unpack(bytes: &[u8], destination: &Path) -> Result<()> {
    let mut archive =
        zip::ZipArchive::new(io::Cursor::new(bytes)).map_err(|_| "無法開啟免安裝更新檔。")?;
    if archive.len() > 1000
        || archive
            .decompressed_size()
            .is_none_or(|bytes| bytes > u128::from(MAX_EXPANDED_SIZE))
    {
        return Err("免安裝更新檔的大小超出限制。".into());
    }
    if archive.len() < 2 {
        return Err("免安裝更新檔缺少主程式或簡報處理元件。".into());
    }
    fs::create_dir(destination.join("engine")).map_err(|error| error.to_string())?;
    let mut remaining = MAX_EXPANDED_SIZE;
    let mut names = HashSet::new();
    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|_| "免安裝更新檔已損壞。")?;
        let name = entry.name().map_err(|_| "免安裝更新檔含有無效的檔名。")?;
        let valid = name == APP || name.strip_prefix("engine/").is_some_and(safe_filename);
        if !valid
            || !entry.is_file()
            || entry.is_symlink()
            || !names.insert(name.to_ascii_lowercase())
            || entry.size() > remaining
        {
            return Err("免安裝更新檔含有無效的路徑或檔案。".into());
        }
        let declared = entry.size();
        let path = destination.join(name.as_ref());
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)
            .map_err(|error| format!("無法寫入更新檔案：{error}"))?;
        let actual = io::copy(&mut entry.by_ref().take(declared + 1), &mut file)
            .and_then(|size| file.sync_all().map(|_| size))
            .map_err(|error| format!("無法寫入更新檔案：{error}"))?;
        if actual != declared {
            return Err("免安裝更新檔已損壞。".into());
        }
        remaining -= actual;
    }
    if !regular(&destination.join(APP), false) || !regular(&destination.join(ENGINE), false) {
        return Err("免安裝更新檔缺少主程式或簡報處理元件。".into());
    }
    Ok(())
}

fn safe_filename(name: &str) -> bool {
    let stem = name
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_uppercase();
    !stem.is_empty()
        && !name.ends_with('.')
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        && !matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        && !["COM", "LPT"].iter().any(|prefix| {
            stem.strip_prefix(prefix)
                .is_some_and(|n| n.len() == 1 && n.as_bytes()[0].is_ascii_digit())
        })
}

fn regular(path: &Path, directory: bool) -> bool {
    fs::symlink_metadata(path).is_ok_and(|metadata| {
        metadata.file_attributes() & REPARSE_POINT == 0
            && if directory {
                metadata.is_dir()
            } else {
                metadata.is_file()
            }
    })
}

fn slot_path(root: &Path, slot: &str) -> Result<PathBuf> {
    if !slot.starts_with("build-") || slot.len() > 140 || !safe_filename(slot) {
        return Err("免安裝版的版本資料夾無效。".into());
    }
    let versions = root.join("versions");
    let path = versions.join(slot);
    if !regular(&versions, true)
        || !regular(&path, true)
        || !regular(&path.join(APP), false)
        || !regular(&path.join("engine"), true)
        || !regular(&path.join(ENGINE), false)
    {
        return Err("免安裝版缺少執行檔或簡報處理元件，請重新解壓縮完整檔案。".into());
    }
    Ok(path)
}

fn read_config<T: DeserializeOwned>(path: &Path) -> Result<T> {
    if !regular(path, false) {
        return Err("找不到免安裝版的版本設定。".into());
    }
    let file = fs::File::open(path).map_err(|error| error.to_string())?;
    serde_json::from_reader(file.take(4096)).map_err(|_| "免安裝版的版本設定有誤。".into())
}

fn write_config(path: &Path, state: &impl Serialize) -> Result<()> {
    let mut file = tempfile::NamedTempFile::new_in(path.parent().unwrap())
        .map_err(|error| error.to_string())?;
    serde_json::to_writer(&mut file, state).map_err(|error| error.to_string())?;
    file.as_file()
        .sync_all()
        .map_err(|error| error.to_string())?;
    file.persist(path)
        .map_err(|error| error.error.to_string())?;
    Ok(())
}

fn fixed_ntfs(root: &Path) -> bool {
    use std::ptr::null_mut;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetVolumePathNameW(path: *const u16, volume: *mut u16, length: u32) -> i32;
        fn GetDriveTypeW(root: *const u16) -> u32;
        fn GetVolumeInformationW(
            root: *const u16,
            name: *mut u16,
            name_length: u32,
            serial: *mut u32,
            component_length: *mut u32,
            flags: *mut u32,
            filesystem: *mut u16,
            filesystem_length: u32,
        ) -> i32;
    }
    if !regular(root, true) {
        return false;
    }
    let Ok(root) = root.canonicalize() else {
        return false;
    };
    let path: Vec<_> = root.as_os_str().encode_wide().chain([0]).collect();
    let mut volume = vec![0; 32768];
    let mut filesystem = [0; 32];
    unsafe {
        GetVolumePathNameW(path.as_ptr(), volume.as_mut_ptr(), volume.len() as u32) != 0
            && GetDriveTypeW(volume.as_ptr()) == 3 // DRIVE_FIXED
            && GetVolumeInformationW(
                volume.as_ptr(), null_mut(), 0, null_mut(), null_mut(), null_mut(),
                filesystem.as_mut_ptr(), filesystem.len() as u32,
            ) != 0
            && filesystem[..5] == [b'N' as u16, b'T' as u16, b'F' as u16, b'S' as u16, 0]
    }
}

fn message(contents: &str) {
    use std::ffi::c_void;
    #[link(name = "user32")]
    unsafe extern "system" {
        fn MessageBoxW(
            window: *mut c_void,
            text: *const u16,
            caption: *const u16,
            flags: u32,
        ) -> i32;
    }
    let contents: Vec<_> = contents.encode_utf16().chain([0]).collect();
    let title: Vec<_> = "PptCollector".encode_utf16().chain([0]).collect();
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            contents.as_ptr(),
            title.as_ptr(),
            0x10,
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use zip::{ZipWriter, write::SimpleFileOptions};

    fn fixture_child(command: &str) -> Child {
        Command::new("powershell.exe")
            .args(["-NoProfile", "-NonInteractive", "-Command", command])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .unwrap()
    }

    fn fixture_reply(reply: &str, exit: i32) -> String {
        format!(
            "[Console]::Out.Write(\"{reply}`n\"); [Console]::Out.Flush(); \
             if ([Console]::OpenStandardInput().ReadByte() -ne 49) {{ exit 1 }}; exit {exit}"
        )
    }

    #[test]
    fn native_engine_reports_ready() {
        let engine =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("engine/publish/PptCollector.Engine.exe");
        assert!(probe_engine(&engine).is_ok());
    }

    fn zip_with_extra(extra: Option<&str>) -> Vec<u8> {
        let mut zip = ZipWriter::new(io::Cursor::new(Vec::new()));
        for name in [Some(APP), Some(ENGINE), extra].into_iter().flatten() {
            zip.start_file(name, SimpleFileOptions::default()).unwrap();
            zip.write_all(name.as_bytes()).unwrap();
        }
        zip.finish().unwrap().into_inner()
    }

    #[test]
    fn unpack_accepts_matching_layout_and_rejects_unsafe_entries() {
        let folder = tempfile::tempdir().unwrap();
        unpack(&zip_with_extra(None), folder.path()).unwrap();
        let engine = fs::read_to_string(folder.path().join(ENGINE)).unwrap();
        assert_eq!(engine, ENGINE);
        for unsafe_name in [
            "engine/../../untouched.txt",
            "engine/game.dll:alternate",
            "engine/AUX.dll",
            "engine/PPTCOLLECTOR.ENGINE.EXE",
            "engine\\bad.dll",
            "outside.dll",
        ] {
            let staged = tempfile::tempdir_in(folder.path()).unwrap();
            assert!(
                unpack(&zip_with_extra(Some(unsafe_name)), staged.path()).is_err(),
                "{unsafe_name}"
            );
        }
        assert!(!folder.path().join("untouched.txt").exists());
    }

    #[test]
    fn unpack_accepts_the_packaged_engine_from_ci() {
        let Some(package) = env::var_os("PPTCOLLECTOR_PORTABLE_TEST_ZIP") else {
            return;
        };
        let folder = tempfile::tempdir().unwrap();
        unpack(&fs::read(package).unwrap(), folder.path()).unwrap();
        assert!(probe_engine(&folder.path().join(ENGINE)).is_ok());
    }

    #[test]
    fn supervisor_reopens_previous_if_current_cannot_start() {
        let folder = tempfile::tempdir().unwrap();
        write_config(
            &folder.path().join(VERSION_FILE),
            &Selection {
                current: "build-current".into(),
                previous: Some("build-old".into()),
            },
        )
        .unwrap();
        let mut launches = 0;
        supervise_session(folder.path(), "ready", |_, slot, mode, reason| {
            launches += 1;
            match launches {
                1 => {
                    assert_eq!(slot, "build-current");
                    Err("current did not start".into())
                }
                2 => {
                    assert_eq!(slot, "build-old");
                    assert!(mode.starts_with("readonly:"));
                    assert_eq!(reason, Some("current did not start"));
                    Ok(fixture_child("exit 0"))
                }
                _ => panic!("Unexpected launch {slot}"),
            }
        })
        .unwrap();
        assert_eq!(launches, 2);
    }

    #[test]
    fn supervisor_continues_after_commit_and_rolls_back_failed_trials() {
        let folder = tempfile::tempdir().unwrap();
        let state = folder.path().join(VERSION_FILE);
        let pending = folder.path().join(PENDING);
        let new_candidate = Candidate {
            slot: "build-new".into(),
            version: Version::new(0, 2, 5),
        };
        let bad_candidate = Candidate {
            slot: "build-bad".into(),
            version: Version::new(0, 2, 6),
        };
        write_config(
            &state,
            &Selection {
                current: "build-old".into(),
                previous: None,
            },
        )
        .unwrap();
        write_config(&pending, &bad_candidate).unwrap();

        let mut launches = 0;
        supervise_session(folder.path(), "ready", |_, slot, mode, reason| {
            launches += 1;
            assert!(!pending.exists());
            match launches {
                1 => {
                    assert_eq!((slot, mode, reason), ("build-old", "ready", None));
                    write_config(&pending, &new_candidate).unwrap();
                    Ok(fixture_child("exit 75"))
                }
                2 => {
                    assert_eq!((slot, mode, reason), ("build-new", "trial", None));
                    write_config(&pending, &bad_candidate).unwrap();
                    Ok(fixture_child(&fixture_reply(
                        "PptCollector ready 0.2.5",
                        UPDATE_EXIT,
                    )))
                }
                3 => {
                    assert_eq!((slot, mode, reason), ("build-bad", "trial", None));
                    Ok(fixture_child("exit 1"))
                }
                4 => {
                    assert_eq!((slot, mode), ("build-new", "ready"));
                    assert_eq!(reason, Some("新版在準備完成前已關閉。"));
                    Ok(fixture_child("exit 75"))
                }
                5 => {
                    assert_eq!((slot, mode), ("build-new", "ready"));
                    assert_eq!(reason, Some("找不到免安裝版的版本設定。"));
                    Ok(fixture_child("exit 0"))
                }
                _ => panic!("Unexpected launch {slot}"),
            }
        })
        .unwrap();
        assert_eq!(launches, 5);
        let saved: Selection = read_config(&state).unwrap();
        assert_eq!(saved.current, "build-new");
        assert_eq!(saved.previous.as_deref(), Some("build-old"));
    }

    #[test]
    fn only_ready_trial_changes_the_current_slot() {
        let folder = tempfile::tempdir().unwrap();
        let state = folder.path().join(VERSION_FILE);
        let old = Selection {
            current: "build-old".into(),
            previous: None,
        };
        write_config(&state, &old).unwrap();

        for (reply, expected_error) in [
            (None, Some("新版在準備完成前已關閉。")),
            (Some("error window failed"), Some("window failed")),
            (
                Some("PptCollector ready 0.2.3"),
                Some("新版回報的版號或啟動訊息有誤。"),
            ),
            (Some("PptCollector ready 0.2.5"), None),
        ] {
            let command = reply.map_or_else(|| "exit 1".into(), |reply| fixture_reply(reply, 0));
            let child = fixture_child(&command);
            let candidate = Candidate {
                slot: "build-new".into(),
                version: Version::new(0, 2, 5),
            };
            let trial = commit_trial(child, &state, &old, candidate);
            let saved = read_config::<Selection>(&state).unwrap();
            if let Some(reason) = expected_error {
                assert!(matches!(trial, Err(error) if error == reason));
                assert_eq!(saved.current, "build-old");
            } else {
                let (mut child, _) = trial.unwrap();
                assert_eq!(saved.current, "build-new");
                assert_eq!(saved.previous.as_deref(), Some("build-old"));
                assert!(child.wait().unwrap().success());
            }
        }
    }
}
