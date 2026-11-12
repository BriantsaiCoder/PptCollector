import { spawnSync } from "node:child_process";
import { mkdtempSync, renameSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const platform = process.env.TAURI_ENV_PLATFORM ?? process.platform;
const architecture = process.env.TAURI_ENV_ARCH ?? process.arch;
const rid = { darwin: "osx", win32: "win", windows: "win" }[platform];
const arch = { aarch64: "arm64", arm64: "arm64", x86_64: "x64", x64: "x64" }[architecture];
if (!rid || !arch) throw new Error(`Unsupported build target: ${platform}/${architecture}.`);

const env = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  NUGET_HTTP_CACHE_PATH: process.env.NUGET_HTTP_CACHE_PATH || path.join(root, "target/nuget-http"),
  NUGET_PACKAGES: process.env.NUGET_PACKAGES || path.join(root, "target/nuget-packages"),
};
const publish = path.join(root, "engine/publish");
const staging = mkdtempSync(path.join(root, "engine/publish-"));
try {
  const result = spawnSync("dotnet", [
    "publish", "engine/PptCollector.Engine.csproj", "-c", "Release", "-r", `${rid}-${arch}`,
    "--self-contained", "true", "-o", staging,
    "-p:DebugType=None", "-p:DebugSymbols=false",
  ], { cwd: root, env, stdio: "inherit" });
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
  if (process.exitCode === 0) {
    rmSync(publish, { recursive: true, force: true });
    renameSync(staging, publish);
  }
} finally {
  rmSync(staging, { recursive: true, force: true });
}
