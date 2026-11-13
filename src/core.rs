use serde::{Deserialize, Serialize, de::DeserializeOwned};
use serde_json::{Value, json};
use std::{
    fs,
    io::{BufRead, BufReader, BufWriter, Read, Write},
    path::{Path, PathBuf},
    process::{Child, ChildStdin, ChildStdout, Command, Stdio},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

pub type Result<T> = std::result::Result<T, String>;

#[derive(Default)]
pub struct Job {
    cancelled: AtomicBool,
    child: Mutex<Option<Child>>,
}

impl Job {
    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
        if let Some(child) = self.child.lock().unwrap().as_mut() {
            let _ = child.kill();
        }
    }

    pub fn check(&self) -> Result<()> {
        if self.cancelled.load(Ordering::Acquire) {
            Err("已取消".into())
        } else {
            Ok(())
        }
    }
}

struct Engine<'a> {
    job: &'a Job,
    input: BufWriter<ChildStdin>,
    output: BufReader<ChildStdout>,
}

impl<'a> Engine<'a> {
    fn start(executable: &Path, job: &'a Job) -> Result<Self> {
        let mut guard = job.child.lock().unwrap();
        job.check()?;
        let mut command = Command::new(executable);
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000); // CREATE_NO_WINDOW
        }
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(|error| format!("無法啟動簡報處理元件：{error}"))?;
        let input = BufWriter::new(child.stdin.take().unwrap());
        let output = BufReader::new(child.stdout.take().unwrap());
        *guard = Some(child);
        Ok(Self { job, input, output })
    }

    fn request<T: DeserializeOwned>(&mut self, payload: Value) -> Result<Result<T>> {
        self.job.check()?;
        let mut line = String::new();
        let exchange = (|| -> std::io::Result<()> {
            serde_json::to_writer(&mut self.input, &payload)?;
            self.input.write_all(b"\n")?;
            self.input.flush()?;
            (&mut self.output)
                .take(64 * 1024 * 1024)
                .read_line(&mut line)?;
            Ok(())
        })();
        self.job.check()?;
        exchange.map_err(|error| format!("簡報處理中斷：{error}"))?;
        if !line.ends_with('\n') {
            return Err("簡報處理元件沒有回傳完整結果。".into());
        }
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Reply<T> {
            Failure { error: String },
            Success(T),
        }
        match serde_json::from_str(&line).map_err(|error| format!("無法讀取處理結果：{error}"))?
        {
            Reply::Failure { error } => Ok(Err(error)),
            Reply::Success(data) => Ok(Ok(data)),
        }
    }
}

impl Drop for Engine<'_> {
    fn drop(&mut self) {
        if let Some(mut child) = self.job.child.lock().unwrap().take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[derive(Deserialize)]
struct Deck {
    digest: String,
    width: u32,
    height: u32,
    slides: Vec<Slide>,
}

#[derive(Deserialize)]
struct Slide {
    page: u32,
    text: String,
}

#[derive(Clone, Serialize)]
pub struct Hit {
    pub id: usize,
    pub path: String,
    pub page: u32,
    pub terms: Vec<String>,
    pub snippet: String,
    pub width: u32,
    pub height: u32,
}

struct Source {
    path: PathBuf,
    label: String,
    digest: String,
}

pub struct Scan {
    pub hits: Vec<Hit>,
    sources: Vec<Arc<Source>>,
    pub issues: Vec<String>,
    pub files: usize,
    pub legacy: usize,
}

#[derive(Clone, Serialize)]
pub struct Progress {
    pub done: usize,
    pub total: usize,
    pub hits: usize,
}

fn file_type(path: &Path, extension: &str) -> bool {
    path.extension()
        .is_some_and(|value| value.eq_ignore_ascii_case(extension))
}

pub fn display_path(path: &Path) -> String {
    let path = path.to_string_lossy();
    #[cfg(windows)]
    {
        if let Some(unc) = path.strip_prefix(r"\\?\UNC\") {
            return format!(r"\\{unc}");
        }
        return path.strip_prefix(r"\\?\").unwrap_or(&path).to_owned();
    }
    #[cfg(not(windows))]
    path.into_owned()
}

pub fn scan(
    engine: &Path,
    root: &Path,
    terms: Vec<String>,
    require_all: bool,
    job: &Job,
    progress: impl Fn(Progress),
) -> Result<Scan> {
    let mut keywords: Vec<(String, String)> = Vec::new();
    for term in &terms {
        let trimmed = term.trim();
        let folded = trimmed.to_lowercase();
        if !folded.is_empty() && !keywords.iter().any(|(_, existing)| existing == &folded) {
            keywords.push((trimmed.to_owned(), folded));
        }
    }
    if keywords.is_empty() {
        return Err("請至少輸入一個關鍵字。".into());
    }
    let mut paths = Vec::new();
    let mut issues = Vec::new();
    let mut legacy = 0;
    let mut folders = vec![root.to_path_buf()];
    while let Some(folder) = folders.pop() {
        job.check()?;
        match fs::read_dir(&folder) {
            Ok(entries) => {
                for entry in entries {
                    job.check()?;
                    match entry.and_then(|entry| Ok((entry.path(), entry.file_type()?))) {
                        Ok((path, kind)) if kind.is_dir() && !is_reparse_directory(&path) => {
                            folders.push(path)
                        }
                        Ok((path, kind))
                            if kind.is_file()
                                && !path.file_name().is_some_and(|name| {
                                    name.to_string_lossy().starts_with("~$")
                                }) =>
                        {
                            if file_type(&path, "pptx") {
                                paths.push(path);
                            } else if file_type(&path, "ppt") {
                                legacy += 1;
                            }
                        }
                        Ok(_) => {}
                        Err(error) => issues.push(format!("{}：{error}", display_path(&folder))),
                    }
                }
            }
            Err(error) => issues.push(format!("{}：{error}", display_path(&folder))),
        }
    }
    paths.sort();
    let files = paths.len();
    progress(Progress {
        done: 0,
        total: files,
        hits: 0,
    });
    let mut result = Scan {
        hits: Vec::new(),
        sources: Vec::new(),
        issues,
        files,
        legacy,
    };
    if paths.is_empty() {
        return Ok(result);
    }

    let mut worker = Engine::start(engine, job)?;
    let mut last_update = Instant::now();
    for (index, path) in paths.into_iter().enumerate() {
        job.check()?;
        let label = display_path(path.strip_prefix(root).unwrap_or(&path));
        let physical = match path.canonicalize() {
            Ok(physical) if physical.starts_with(root) => physical,
            Ok(_) => {
                result
                    .issues
                    .push(format!("{label}：檔案已移出所選資料夾。"));
                continue;
            }
            Err(error) => {
                result.issues.push(format!("{label}：{error}"));
                continue;
            }
        };
        let Some(absolute) = physical.to_str() else {
            result
                .issues
                .push(format!("{label}：檔案路徑不是有效的 Unicode。"));
            continue;
        };
        match worker.request::<Deck>(json!({"kind":"scan", "path": absolute}))? {
            Ok(deck) => {
                let source = Arc::new(Source {
                    path: physical,
                    label: label.clone(),
                    digest: deck.digest,
                });
                for slide in deck.slides {
                    let folded = slide.text.to_lowercase();
                    let matched: Vec<_> = keywords
                        .iter()
                        .filter(|(_, word)| folded.contains(word))
                        .collect();
                    if matched.is_empty() || (require_all && matched.len() != keywords.len()) {
                        continue;
                    }
                    let line = slide
                        .text
                        .lines()
                        .find(|line| {
                            let folded = line.to_lowercase();
                            matched.iter().any(|(_, term)| folded.contains(term))
                        })
                        .unwrap_or(&slide.text);
                    let mut characters = line.trim().chars();
                    let mut snippet: String = characters.by_ref().take(190).collect();
                    if characters.next().is_some() {
                        snippet.push('…');
                    }
                    result.hits.push(Hit {
                        id: result.hits.len(),
                        path: label.clone(),
                        page: slide.page,
                        terms: matched.into_iter().map(|(term, _)| term.clone()).collect(),
                        snippet,
                        width: deck.width,
                        height: deck.height,
                    });
                    result.sources.push(Arc::clone(&source));
                }
            }
            Err(error) => result.issues.push(format!("{label}：{error}")),
        }
        if index + 1 == files || last_update.elapsed() >= Duration::from_millis(100) {
            progress(Progress {
                done: index + 1,
                total: files,
                hits: result.hits.len(),
            });
            last_update = Instant::now();
        }
    }
    job.check()?;
    Ok(result)
}

#[cfg(windows)]
fn is_reparse_directory(path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    fs::symlink_metadata(path).map_or(true, |meta| meta.file_attributes() & 0x400 != 0)
}

#[cfg(not(windows))]
fn is_reparse_directory(_: &Path) -> bool {
    false
}

const SECURITY_SUFFIX: &str = "_(Security C)";
const MAX_OUTPUT_STEM_BYTES: usize = 245;

fn split_security_suffix(stem: &str) -> (&str, &str) {
    let start = stem.len().saturating_sub(SECURITY_SUFFIX.len());
    if stem
        .get(start..)
        .is_some_and(|tail| tail.eq_ignore_ascii_case(SECURITY_SUFFIX))
    {
        stem.split_at(start)
    } else {
        (stem, "")
    }
}

fn output_name(input: &str, security: bool) -> Result<String> {
    let trimmed = input.trim();
    let stem = if trimmed.to_ascii_lowercase().ends_with(".pptx") {
        &trimmed[..trimmed.len() - 5]
    } else {
        trimmed
    };
    let device = stem
        .split('.')
        .next()
        .unwrap_or_default()
        .trim_end()
        .to_ascii_uppercase();
    let reserved = matches!(device.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || ["COM", "LPT"].iter().any(|prefix| {
            device.strip_prefix(prefix).is_some_and(|number| {
                matches!(
                    number,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            })
        });
    let name = if security {
        split_security_suffix(stem).0
    } else {
        stem
    };
    let suffix = if security { SECURITY_SUFFIX } else { "" };
    if stem.is_empty()
        || stem.ends_with(['.', ' '])
        || reserved
        || name.len() + suffix.len() > MAX_OUTPUT_STEM_BYTES
        || stem
            .chars()
            .any(|ch| ch.is_control() || "<>:\"/\\|?*".contains(ch))
    {
        return Err("請輸入可在 Mac 與 Windows 使用的檔名，例如「良率分析」。".into());
    }
    Ok(format!("{name}{suffix}.pptx"))
}

pub fn source_path(root: &Path, scan: &Scan, id: usize) -> Result<PathBuf> {
    let source = scan.sources.get(id).ok_or("搜尋結果已過期，請重新搜尋。")?;
    let path = source
        .path
        .canonicalize()
        .map_err(|error| format!("找不到來源簡報：{error}"))?;
    if path != source.path
        || !path.starts_with(root)
        || !path.is_file()
        || !file_type(&path, "pptx")
    {
        return Err("來源簡報已移動或變更，請重新搜尋。".into());
    }
    Ok(path)
}

#[derive(Debug, Serialize)]
pub struct Exported {
    pub path: String,
    pub count: usize,
    pub adjusted_notes: usize,
}

#[derive(Default)]
pub struct ExportOptions {
    pub security_name: bool,
    pub canvas: Option<(u32, u32)>,
}

pub fn export(
    engine: &Path,
    root: &Path,
    scan: &Scan,
    ids: Vec<usize>,
    name: &str,
    options: ExportOptions,
    job: &Job,
) -> Result<Exported> {
    let name = output_name(name, options.security_name)?;
    if root.canonicalize().ok().as_deref() != Some(root) {
        return Err("輸出資料夾已移動，請重新選取。".into());
    }
    let mut ids = ids;
    ids.sort_unstable();
    ids.dedup();
    let mut canvas_found = false;
    let mut files: Vec<(&Source, Vec<u32>)> = Vec::new();
    for id in ids {
        let hit = scan.hits.get(id).ok_or("搜尋結果已過期，請重新搜尋。")?;
        let source = &scan.sources[id];
        let size = (hit.width, hit.height);
        canvas_found |= options.canvas == Some(size);
        match files.last_mut() {
            Some((previous, pages)) if previous.path == source.path => pages.push(hit.page),
            _ => files.push((source, vec![hit.page])),
        }
    }
    if files.is_empty() {
        return Err("請至少選取一張投影片。".into());
    }
    if options.canvas.is_some() && !canvas_found {
        return Err("請從已選取的投影片尺寸中選擇輸出尺寸。".into());
    }
    job.check()?;
    let mut temp = tempfile::Builder::new()
        .prefix(".pptcollector-")
        .suffix(".tmp")
        .tempfile_in(root)
        .map_err(|error| format!("無法在輸出資料夾寫入：{error}"))?
        .into_temp_path();
    let sources: Result<Vec<Value>> = files.iter().map(|(source, pages)| {
        let path = source.path.to_str().ok_or("來源檔案路徑不是有效的 Unicode。")?;
        Ok(json!({"path": path, "label": source.label, "digest": source.digest, "pages": pages}))
    }).collect();
    let temp_name = temp.to_str().ok_or("輸出資料夾路徑不是有效的 Unicode。")?;
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Report {
        count: usize,
        #[serde(default)]
        adjusted_notes: usize,
        timestamp: String,
    }
    let report = Engine::start(engine, job)?.request::<Report>(json!({
        "kind": "export", "path": temp_name, "files": sources?, "canvas": options.canvas
    }))??;
    job.check()?;
    let stamp = report.timestamp.as_bytes();
    if stamp.len() != 15
        || stamp[8] != b'_'
        || !stamp
            .iter()
            .enumerate()
            .all(|(i, byte)| i == 8 || byte.is_ascii_digit())
    {
        return Err("簡報處理元件回傳的日期時間格式無效。".into());
    }
    let (stem, security) = split_security_suffix(name.strip_suffix(".pptx").unwrap());
    for attempt in 0..1000 {
        let target = if attempt == 0 {
            root.join(&name)
        } else {
            let suffix = if attempt == 1 {
                format!("_{}{security}", report.timestamp)
            } else {
                format!("_{}_{attempt}{security}", report.timestamp)
            };
            let end = stem.floor_char_boundary(MAX_OUTPUT_STEM_BYTES - suffix.len());
            root.join(format!("{}{suffix}.pptx", &stem[..end]))
        };
        // The destination may be created after the export starts. Keep the finished
        // temporary file and retry without replacing anything already there.
        match temp.persist_noclobber(&target) {
            Ok(()) => {
                return Ok(Exported {
                    path: display_path(&target),
                    count: report.count,
                    adjusted_notes: report.adjusted_notes,
                });
            }
            Err(error)
                if error.error.kind() == std::io::ErrorKind::AlreadyExists
                    || target.symlink_metadata().is_ok() =>
            {
                temp = error.path;
            }
            Err(error) => return Err(format!("無法儲存匯出檔：{}", error.error)),
        }
    }
    Err("目前有太多同名檔案，請換一個檔名。".into())
}
