#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod core;

use crate::core::{Exported, Hit, Job, Result, Scan, display_path};
use serde::Serialize;
use std::{
    path::PathBuf,
    process::Command,
    sync::{Arc, Mutex},
};
use tauri::{AppHandle, Emitter, Manager, State};
use tauri_plugin_dialog::DialogExt;

#[derive(Default)]
struct Workspace {
    folder: Option<PathBuf>,
    current: Option<Arc<Scan>>,
    revision: u64,
    active: Option<Arc<Job>>,
    exported: bool,
}

#[derive(Default)]
struct AppState(Mutex<Workspace>);

fn engine_path(app: &AppHandle) -> Result<PathBuf> {
    let binary = if cfg!(windows) {
        "PptCollector.Engine.exe"
    } else {
        "PptCollector.Engine"
    };
    let base = app
        .path()
        .resource_dir()
        .map_err(|error| error.to_string())?;
    let candidates = [
        base.join("engine").join(binary),
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("engine/publish")
            .join(binary),
    ];
    candidates
        .into_iter()
        .find(|path| path.is_file())
        .ok_or_else(|| "找不到簡報處理元件。請重新安裝完整的 PptCollector。".into())
}

struct ActiveJob {
    app: AppHandle,
    job: Arc<Job>,
    folder: Option<PathBuf>,
}

impl ActiveJob {
    fn begin(app: &AppHandle, require_folder: bool) -> Result<Self> {
        let state = app.state::<AppState>();
        let mut workspace = state.0.lock().unwrap();
        if workspace.active.is_some() {
            return Err("請先等目前的操作結束。".into());
        }
        let folder = workspace.folder.clone();
        if require_folder && folder.is_none() {
            return Err("請先選擇資料夾。".into());
        }
        let job = Arc::new(Job::default());
        workspace.active = Some(Arc::clone(&job));
        Ok(Self {
            app: app.clone(),
            job,
            folder,
        })
    }
}

impl Drop for ActiveJob {
    fn drop(&mut self) {
        self.app.state::<AppState>().0.lock().unwrap().active = None;
    }
}

#[tauri::command]
async fn pick_folder(app: AppHandle) -> Result<Option<String>> {
    let active = ActiveJob::begin(&app, false)?;
    tauri::async_runtime::spawn_blocking(move || {
        let mut picker = app.dialog().file().set_title("選擇簡報資料夾");
        if let Some(previous) = &active.folder {
            picker = picker.set_directory(previous);
        }
        let Some(picked) = picker.blocking_pick_folder() else {
            return Ok(None);
        };
        let path = picked
            .into_path()
            .map_err(|error| error.to_string())?
            .canonicalize()
            .map_err(|error| format!("無法開啟所選資料夾：{error}"))?;
        let label = display_path(&path);
        let state = app.state::<AppState>();
        let mut workspace = state.0.lock().unwrap();
        workspace.folder = Some(path);
        workspace.current = None;
        workspace.exported = false;
        Ok(Some(label))
    })
    .await
    .map_err(|error| error.to_string())?
}

#[derive(Serialize)]
struct SearchResponse {
    revision: u64,
    hits: Vec<Hit>,
    issues: Vec<String>,
    files: usize,
    legacy: usize,
}

#[tauri::command]
async fn search(app: AppHandle, terms: Vec<String>, require_all: bool) -> Result<SearchResponse> {
    let active = ActiveJob::begin(&app, true)?;
    {
        let state = app.state::<AppState>();
        state.0.lock().unwrap().current = None;
    }
    tauri::async_runtime::spawn_blocking(move || {
        let result = core::scan(
            &engine_path(&app)?,
            active.folder.as_ref().unwrap(),
            terms,
            require_all,
            &active.job,
            |progress| {
                let _ = app.emit_to("main", "scan-progress", progress);
            },
        )?;
        active.job.check()?;
        let state = app.state::<AppState>();
        let mut workspace = state.0.lock().unwrap();
        workspace.revision += 1;
        let response = SearchResponse {
            revision: workspace.revision,
            hits: result.hits.clone(),
            issues: result.issues.clone(),
            files: result.files,
            legacy: result.legacy,
        };
        workspace.current = Some(Arc::new(result));
        Ok(response)
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
fn cancel_scan(state: State<'_, AppState>) {
    if let Some(job) = &state.0.lock().unwrap().active {
        job.cancel();
    }
}

#[tauri::command]
async fn export(app: AppHandle, revision: u64, ids: Vec<usize>, name: String) -> Result<Exported> {
    let active = ActiveJob::begin(&app, true)?;
    let scan = {
        let state = app.state::<AppState>();
        let workspace = state.0.lock().unwrap();
        workspace
            .current
            .as_ref()
            .filter(|_| workspace.revision == revision)
            .cloned()
            .ok_or("搜尋結果已過期，請重新搜尋。")?
    };
    tauri::async_runtime::spawn_blocking(move || {
        let result = core::export(
            &engine_path(&app)?,
            active.folder.as_ref().unwrap(),
            &scan,
            ids,
            &name,
            &active.job,
        )?;
        app.state::<AppState>().0.lock().unwrap().exported = true;
        Ok(result)
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
async fn open_output_folder(app: AppHandle) -> Result<()> {
    let path = {
        let state = app.state::<AppState>();
        let workspace = state.0.lock().unwrap();
        workspace
            .folder
            .clone()
            .filter(|_| workspace.exported)
            .ok_or("尚未匯出簡報。")?
    };
    #[cfg(target_os = "macos")]
    let opener = "/usr/bin/open";
    #[cfg(target_os = "windows")]
    let opener = "explorer.exe";
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    let opener = "xdg-open";
    tauri::async_runtime::spawn_blocking(move || {
        let mut process = Command::new(opener)
            .arg(display_path(&path))
            .spawn()
            .map_err(|error| format!("無法開啟資料夾：{error}"))?;
        let _ = process.wait();
        Ok(())
    })
    .await
    .map_err(|error| error.to_string())?
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            pick_folder,
            search,
            cancel_scan,
            export,
            open_output_folder
        ])
        .run(tauri::generate_context!())
        .expect("PptCollector failed to start");
}
