use crate::{ActiveJob, core::Result, open_path};
use semver::Version;
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, path::Path, sync::Mutex, time::Duration};
#[cfg(windows)]
use tauri::Manager;
use tauri::{AppHandle, Emitter, State};
use tauri_plugin_updater::{Update, UpdaterExt};

const RELEASES: &str = "https://github.com/BriantsaiCoder/PptCollector/releases";

#[derive(Default)]
pub(crate) struct UpdateState(Mutex<Option<Update>>);

#[derive(Serialize)]
pub(crate) struct UpdateInfo {
    version: String,
    portable: bool,
    message: Option<String>,
}

#[derive(Deserialize, Serialize)]
struct Change {
    version: String,
    notes: String,
}

#[derive(Serialize)]
pub(crate) struct UpdateOffer {
    version: String,
    can_install: bool,
    portable_download: bool,
    changes: Vec<Change>,
}

fn changes(current: &Version, latest: &Version, update: &Update) -> Vec<Change> {
    let history = update
        .raw_json
        .get("changes")
        .cloned()
        .and_then(|value| serde_json::from_value(value).ok())
        .unwrap_or_default();
    select_changes(current, latest, history, update.body.as_deref())
}

fn select_changes(
    current: &Version,
    latest: &Version,
    history: Vec<Change>,
    latest_notes: Option<&str>,
) -> Vec<Change> {
    let mut by_version = BTreeMap::new();
    for change in history {
        let Ok(version) = Version::parse(change.version.trim_start_matches('v')) else {
            continue;
        };
        if version > *current && version <= *latest && !change.notes.trim().is_empty() {
            by_version.entry(version).or_insert(change.notes);
        }
    }
    if let Some(notes) = latest_notes.filter(|notes| !notes.trim().is_empty()) {
        by_version
            .entry(latest.clone())
            .or_insert_with(|| notes.into());
    }

    by_version
        .into_iter()
        .rev()
        .map(|(version, notes)| Change {
            version: version.to_string(),
            notes,
        })
        .collect()
}

#[tauri::command]
pub(crate) async fn update_info(app: AppHandle) -> Result<UpdateInfo> {
    let version = app.package_info().version.to_string();
    #[cfg(windows)]
    {
        let trial_version = version.clone();
        if crate::portable::is_trial() {
            // This IPC confirms that the bundled main WebView loaded its application script.
            let window = app.get_webview_window("main").ok_or("找不到主視窗。")?;
            let result = tauri::async_runtime::spawn_blocking(move || {
                crate::portable::confirm_trial(&trial_version, || {
                    window.show().map_err(|error| error.to_string())
                })
            })
            .await
            .map_err(|error| error.to_string())?;
            if result.is_err() {
                app.exit(1);
                return Err("新版未能正常啟動。".into());
            }
        }
    }
    Ok(UpdateInfo {
        version,
        portable: is_portable(),
        message: {
            #[cfg(windows)]
            {
                crate::portable::restored_message()
            }
            #[cfg(not(windows))]
            None
        },
    })
}

fn is_portable() -> bool {
    #[cfg(windows)]
    return crate::portable::runtime_root().is_some();
    #[cfg(not(windows))]
    false
}

fn can_install() -> bool {
    #[cfg(windows)]
    return !is_portable() || crate::portable::can_stage();
    #[cfg(not(windows))]
    cfg!(target_os = "macos")
}

#[tauri::command]
pub(crate) async fn check_update(
    app: AppHandle,
    state: State<'_, UpdateState>,
) -> Result<Option<UpdateOffer>> {
    state.0.lock().unwrap().take();
    let config = app.config().plugins.0.get("updater");
    let enabled = config.is_some_and(|value| {
        value["pubkey"].as_str().is_some_and(|key| !key.is_empty())
            && value["endpoints"]
                .as_array()
                .is_some_and(|endpoints| !endpoints.is_empty())
            && value["requireSignedVersion"].as_bool() == Some(true)
    });
    if !enabled {
        return Err("這個版本尚未啟用線上更新，可前往發行頁查看版本。".into());
    }

    #[allow(unused_mut)]
    let mut builder = app.updater_builder().timeout(Duration::from_secs(15));
    #[cfg(windows)]
    {
        builder = builder.target(if is_portable() {
            "windows-x86_64-portable"
        } else {
            "windows-x86_64-nsis"
        });
    }
    let updater = builder.build().map_err(|_| "無法啟用版本查詢。")?;
    let Some(update) = updater
        .check()
        .await
        .map_err(|_| "目前無法查詢新版，請確認網路連線後再試。")?
    else {
        return Ok(None);
    };
    // Bind the offer to these exact manifest bytes and enforce signed version on every path.
    let mut update = updater
        .restore_update(update.raw_json)
        .map_err(|_| "更新資料格式有誤。")?
        .ok_or("更新資料已變更，請重新檢查。")?;
    let latest = Version::parse(&update.version).map_err(|_| "更新版號格式有誤。")?;
    if update.download_url.scheme() != "https"
        && !(cfg!(debug_assertions) && update.download_url.host_str() == Some("127.0.0.1"))
    {
        return Err("更新檔的下載網址必須使用 HTTPS。".into());
    }
    #[cfg(windows)]
    {
        let suffix = if is_portable() {
            "-portable-update.zip"
        } else {
            "-setup.exe"
        };
        if !update.download_url.path().ends_with(suffix) {
            return Err("找不到符合這個版本類型的更新檔。".into());
        }
    }
    let installable = can_install();
    let offer = UpdateOffer {
        version: update.version.clone(),
        can_install: installable,
        portable_download: is_portable() && !installable,
        changes: changes(&app.package_info().version, &latest, &update),
    };
    update.timeout = Some(Duration::from_secs(300));
    state.0.lock().unwrap().replace(update);
    Ok(Some(offer))
}

#[derive(Clone, Serialize)]
struct UpdateProgress {
    done: u64,
    total: Option<u64>,
    installing: bool,
}

#[tauri::command]
pub(crate) async fn install_update(
    app: AppHandle,
    state: State<'_, UpdateState>,
    version: String,
) -> Result<()> {
    let _active = ActiveJob::begin(&app, false)?;
    if !can_install() {
        return Err("這個位置無法自動更新，請前往發行頁下載。".into());
    }
    let update = {
        let mut pending = state.0.lock().unwrap();
        if pending
            .as_ref()
            .is_none_or(|update| update.version != version)
        {
            return Err("請重新檢查更新。".into());
        }
        pending.take().unwrap()
    };
    let mut done = 0;
    let bytes = update
        .download(
            |chunk, total| {
                done += chunk as u64;
                let _ = app.emit_to(
                    "main",
                    "update-progress",
                    UpdateProgress {
                        done,
                        total,
                        installing: false,
                    },
                );
            },
            || {
                let _ = app.emit_to(
                    "main",
                    "update-progress",
                    UpdateProgress {
                        done: 0,
                        total: None,
                        installing: true,
                    },
                );
            },
        )
        .await
        .map_err(|_| "下載或驗證更新失敗，請重新檢查後再試。")?;

    #[cfg(windows)]
    if is_portable() {
        tauri::async_runtime::spawn_blocking(move || crate::portable::stage(&version, &bytes))
            .await
            .map_err(|_| "無法準備新版，請再試一次。")??;
        app.exit(crate::portable::UPDATE_EXIT);
        return Ok(());
    }

    tauri::async_runtime::spawn_blocking(move || update.install(bytes))
        .await
        .map_err(|_| "無法安裝新版，請再試一次。")?
        .map_err(|_| "無法安裝新版，請確認安裝位置的權限或從發行頁下載。")?;
    #[cfg(target_os = "macos")]
    app.restart();
    #[allow(unreachable_code)]
    Ok(())
}

#[tauri::command]
pub(crate) async fn open_update_release(version: Option<String>) -> Result<()> {
    let url = match version {
        Some(version) => {
            let version = Version::parse(&version).map_err(|_| "更新版號格式有誤。")?;
            if is_portable() && !can_install() {
                format!(
                    "{RELEASES}/download/v{version}/PptCollector-v{version}-windows-x64-portable.zip"
                )
            } else {
                format!("{RELEASES}/tag/v{version}")
            }
        }
        None => RELEASES.to_string(),
    };
    tauri::async_runtime::spawn_blocking(move || open_path(Path::new(&url), "發行頁"))
        .await
        .map_err(|error| error.to_string())?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn history_shows_only_versions_skipped_in_descending_order() {
        let history = [
            ("v0.2.4", "installed"),
            ("0.2.9", "outside"),
            ("0.2.10", "outside"),
            ("0.2.6", "second"),
            ("0.2.5", "first"),
            ("0.2.6", "duplicate"),
            ("0.2.8", " "),
            ("invalid", "invalid"),
        ]
        .map(|(version, notes)| Change {
            version: version.into(),
            notes: notes.into(),
        });
        let rows = select_changes(
            &Version::new(0, 2, 4),
            &Version::new(0, 2, 8),
            history.into(),
            Some("latest"),
        );
        assert_eq!(
            rows.iter()
                .map(|r| (r.version.as_str(), r.notes.as_str()))
                .collect::<Vec<_>>(),
            [("0.2.8", "latest"), ("0.2.6", "second"), ("0.2.5", "first")],
        );
    }
    #[test]
    fn signed_release_rejects_modified_bytes_and_relabeling() {
        // Public key and signature from a disposable key generated for this fixture.
        let public_key = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IDNDQUVGNTE2N0ZDMzBCMDQKUldRRUM4Ti9Gdld1UEtlOVBQZXhuR3FwTEhZN090Q3VkWVFCZU44cFVobkRSb1Jjc21odWVQRCsK";
        let signature = "dW50cnVzdGVkIGNvbW1lbnQ6IHNpZ25hdHVyZSBmcm9tIHRhdXJpIHNlY3JldCBrZXkKUlVRRUM4Ti9Gdld1UEJnR3ZuY2MwOE1LMUpUZXdZY1RyMW5BTGl1SWthT1hSV29VekdoV2xJNkYzWHlXYWlpQWdLKzF4K2NCdXFGdVI4c3YxL1VZc0xCN0lhdWlvSTl1OHdFPQp0cnVzdGVkIGNvbW1lbnQ6IHRpbWVzdGFtcDoxNzk0ODk1NDc4CWZpbGU6cGF5bG9hZAl2ZXJzaW9uOjIuMC4wClFZcWYrL0pWMU1EekE3MUNZdzRtbnZhQTMyU0VKTUZXSWNLVWRCZWRFVUxDbE1OMU5TMnVVKzMybDNySlBaMkNTZGRPcmZsUlFZOE1PS2ZEVXZiT0JnPT0K";
        let mut context = tauri::test::mock_context(tauri::test::noop_assets());
        context.config_mut().plugins.0.insert(
            "updater".into(),
            serde_json::json!({ "pubkey": public_key }),
        );
        let app = tauri::test::mock_builder()
            .plugin(tauri_plugin_updater::Builder::new().build())
            .build(context)
            .unwrap();
        let updater = app
            .handle()
            .updater_builder()
            .target("fixture")
            .endpoints(vec![
                "https://example.invalid/updates.json".parse().unwrap(),
            ])
            .unwrap()
            .build()
            .unwrap();
        let mut manifest = serde_json::json!({
            "version": "2.0.0",
            "platforms": {
                "fixture": {
                    "url": "https://example.invalid/payload",
                    "signature": signature
                }
            }
        });
        let payload = b"PptCollector updater fixture";
        let signed = updater.restore_update(manifest.clone()).unwrap().unwrap();
        assert!(signed.verify(payload).is_ok());
        assert!(signed.verify(b"tampered fixture").is_err());

        manifest["version"] = serde_json::json!("2.0.1");
        let relabeled = updater.restore_update(manifest).unwrap().unwrap();
        assert!(matches!(
            relabeled.verify(payload),
            Err(tauri_plugin_updater::Error::SignedVersionMismatch { .. })
        ));
    }
}
