# Launch contract 設計 — UxPlay v1.73.6

- Status: **Designed（未実装）**
- 設計日: 2026-08-21
- 対象 release: `FDH2/UxPlay v1.73.6`
- Exact commit: `21eef8df25d91e12635c36d8176ad192725baca2`
  - 軽量 tag のため tag ref と commit SHA は同一。真正性の基準は tag 名ではなく **full SHA**。
- 用語は `CONTEXT.md` の定義に従う（Launch profile / Launch contract / Expert override / Readiness / Ready runtime / Launch workspace / Diagnostic log / Runtime bundle / Build artifact）。

## 1. 目的

MainWindow に集中している「Launch profile の収集 → UxPlay 引数の組み立て → pinned UxPlay との整合」という知識を、独立した深い module（Launch contract module）へ移す。目的は locality（変更が一箇所に集中する）、leverage（一つの実装が UI・process・Diagnostic log・テストへ効く）、testability（interface が test surface になる）。

現行実装の問題（監査で確定した事実）:

- UI が pinned UxPlay に存在しないフラグを表示していた（`-a2`）
- 意味の違うフラグを別用途で表示していた（`-m` は MAC/Device ID、`-r` は 90 度回転、`-v` は version 表示）
- 引数を一つの文字列として組み立て `ProcessStartInfo.Arguments` へ渡すため、quoting と秘密情報保護が不安定
- password が Diagnostic log に流出し得る

## 2. Module 構成

### 2.1 Interface（seam）

```csharp
public interface ILaunchContract
{
    UxPlayRelease SupportedRelease { get; }

    LaunchCommand BuildCommand(
        LaunchProfile profile,
        ReadyRuntime runtime,
        LaunchWorkspace workspace,
        ExpertOverride? expertOverride = null);
}
```

- Property 1 つ + method 1 つの最小 interface。検証メソッドは公開しない（入力型が生成時点で valid であることを保証する）。
- Production adapter は **`UxPlay1736LaunchContract`** の一つのみ。
  - **注記（正直な状態表示）**: adapter が一つしかない現在、この C# `interface` は仮説上の seam である。将来の release 移行時に二つ目の adapter（例: `UxPlay174xLaunchContract`）を一時的に併存させて差分検証する用途を想定して導入した。二つ目が現れない期間が長く続くなら、seam の存続を再評価すること。

### 2.2 入力型

**`LaunchProfile`** — 利用者が選んだ起動設定の値オブジェクト。

- public constructor が検証し、不正なら **`LaunchProfileValidationException`** を投げる。例外は全 `ContractViolation`（項目・値・理由）を保持し、UI は一度の操作で全問題を表示できる。
- 生成後の instance は常に valid（不変条件）。

**`ExpertOverride`** — Launch contract の意味保証の外にある上級者向け引数。

- constructor が Windows command-line quoting 規則で token 化し、構文エラーは **`ExpertOverrideSyntaxException`**（文字位置と原因を保持）を投げる。
- token 列は内部に隠し、Diagnostic log へは一切出さない。
- 引数の意味と対象 release での有効性は利用者責任。追加の runtime 依存（例: `-ble` の Python/winrt/psutil）は Ready runtime の保証外であり、UI は有効化時にその旨を警告する。

**`ReadyRuntime`** — Readiness 検査を通過した実行環境。

- Readiness module が `runtime-manifest.json` を検査し、`ILaunchContract.SupportedRelease` と一致した場合のみ生成する。
- `BuildCommand` は defense-in-depth として release 一致を再確認し、不一致なら **`LaunchContractMismatchException`** を投げる（利用者入力エラーとは区別される構成ミス）。

**`LaunchWorkspace`** — 書き込み可能な出力領域。

- ログ・録画（`-mp4`）・dump 等の相対パス出力はここを基準に解決する。既定はユーザー領域（例: `%LOCALAPPDATA%\UxplayLauncher\workspace`）。
- 不変の Runtime bundle と分離し、Program Files 配下への書き込み失敗を構造的に防ぐ。

### 2.3 出力型

**`LaunchCommand`** — 安全な起動指示。raw token と秘密値は内部に隠す。

```csharp
public sealed class LaunchCommand
{
    public ContractCoverage Coverage { get; }   // Full | Partial

    public ProcessStartInfo CreateProcessStartInfo(); // ArgumentList + 環境変数
    public DiagnosticLaunch ToDiagnosticLaunch();     // redaction 済み表示用
}
```

- `Coverage = Partial` は Expert override 有効時。意味検証と optional runtime capability が保証外であることを UI/log に明示する。
- process 実行と Diagnostic log が同じ `LaunchCommand` を通るため、caller が raw command を再構築する seam は存在しない。
- 引数は `ProcessStartInfo.ArgumentList` へ token 単位で渡す（shell 非経由）。順序は typed arguments が先、Expert override が後。重複 flag は「後勝ち」を意図的 override として許可する。
  - 実装時の検証項目: UxPlay parser が「最後の指定を優先」しない flag があれば、この文書の例外リストとして記録すること。

### 2.4 環境変数（contract の責務）

`BuildCommand` が生成する環境変数は Runtime bundle の相対配置を基準にする（`C:\msys64` 固定値は廃止）:

- `PATH`: `<bundle>/runtime/bin` を先頭に追加
- `GST_PLUGIN_SYSTEM_PATH_1_0` / `GST_PLUGIN_PATH_1_0`: `<bundle>/runtime/lib/gstreamer-1.0`
- `GST_PLUGIN_SCANNER`: `<bundle>/runtime/libexec/gstreamer-1.0/gst-plugin-scanner.exe`
- `GST_DEBUG`: デバッグログ ON のとき `3`、それ以外 `2`。`GST_DEBUG_NO_COLOR=1`
- DLL コピー（旧 `DependencyManager`）は行わない。Runtime bundle が完結しているため不要（Readiness が検査する）。

## 3. Flag 定義テーブル（module 内部・v1.73.6）

宣言的テーブル（名前・引数型・許可値・既定値・UI 対応・説明）を module 内部に持ち、検証器と golden test が共有する。**public interface には公開しない**（Q14 で UI 動的生成を却下）。

Typed Launch profile と flag の対応（確定値は v1.73.6 ソースで検証済み）:

| Launch profile 項目 | Flag | 検証規則 | 既定挙動 |
|---|---|---|---|
| 解像度 | `-s wxh[@r]` | `\d+x\d+(@\d+)?` | 既定 `1920x1080`（UxPlay 既定と一致・Q37） |
| 最大 FPS | `-fps n` | 1–255 | 既定 30。presets 30/60。「上限値」であることを UI 文言に明記 |
| デバイス名 | `-n name` | UTF-8 有効性 | 既定 `UxPlay-Windows`。日本語・空白・引用符の round-trip を CI golden test で保証 |
| HLS | `-hls` | bare のみ（`2`/`3` は Expert） | OFF |
| 音質優先同期（Audio-Only） | `-async` | bare | OFF |
| 低遅延（Mirror） | `-vsync no` | 固定値 | OFF |
| 音声 OFF | `-as 0` | 排他: AudioSink | OFF |
| AudioSink | `-as sink` | 非空文字列 | `directsoundsink` |
| 音声遅延 | `-al x` | 0–10 秒（小数可） | **空欄時は渡さない**（UxPlay 既定 0.25 秒を尊重・Q35） |
| パスワード | `-pw pwd` | **6 文字以上**（launcher 側規則。code は `MIN_PASSWORD_LENGTH 4` だが README は 6 文字以上で矛盾しており安全側を採用） | 空なら渡さない |
| 基底ポート | `-p n` | 1024–65535（`LOWEST_ALLOWED_PORT`/`HIGHEST_PORT`） | 空なら渡さない |
| VideoSink | `-vs sink` | 非空文字列 | `d3d11videosink fullscreen-toggle-mode=alt-enter`（明示指定を維持。auto の d3d12 化は別変更） |
| デバッグログ（UxPlay） | `-d` | bare | OFF。**`GST_DEBUG` とは別物として扱う**（Q34）。GStreamer 詳細度は UI に出さず、ON 時に `GST_DEBUG=3` |
| 録画 | `-mp4 [fn]` | 出力先は Launch workspace 配下に解決 | OFF（Q36 で typed 昇格） |

**UI から削除する項目**（v1.73.6 実装で確認済みの事実に基づく）:

- `-a2`: **存在しない**（旧・新どちらにも）。UxPlay 自体が AirPlay2 legacy protocol receiver
- `-r`（「RAOP 対応」表示）: 実際は 90 度回転 `-r {R|L}`。通常 UI から削除、Expert override で利用可
- `-v`（「詳細ログ」表示）: 実際は version 表示後に即終了。削除
- `-m`（「ミラーリングモード」表示）: 実際は MAC/Device ID 変更。通常 UI から削除、Expert override で利用可

**Expert override に留める新 flags**: `-lang`, `-scrsv`, `-vrtp`, `-artp`, `-ble`（`-ble` は Python/winrt/psutil 依存のため Ready runtime 保証外の警告対象）

## 4. UI 再編成（Operator UI への入力）

- 通常 UI は typed Launch profile の明示的入力欄のみ。flag 定義テーブルからの動的生成はしない
- Expert mode: checkbox は起動時常に OFF。ON で警告文と入力欄を表示。起動中は status に「Expert override 有効」を表示。値は保存しない
- `LaunchProfileValidationException` の violations は項目単位で入力欄の近くに表示
- `ExpertOverrideSyntaxException` は「位置 N: 引用符が閉じていません」形式で表示

## 5. Runtime bundle 配置と manifest

### 5.1 配布契約

利用者に MSYS2 を要求しない自己完結 Runtime bundle（Q25）。「7 DLL だけコピー」方式は廃止。

### 5.2 レイアウト

```text
artifact/
├── UxplayLauncher.exe
├── runtime/
│   ├── bin/
│   │   ├── uxplay.exe
│   │   ├── gst-inspect-1.0.exe
│   │   └── *.dll                    # import closure 全体
│   ├── lib/
│   │   └── gstreamer-1.0/*.dll      # GStreamer plugins
│   └── libexec/
│       └── gstreamer-1.0/
│           └── gst-plugin-scanner.exe
├── runtime-manifest.json
└── licenses/
```

- 同一 MSYS2 MINGW64 ABI から収集。MINGW64 と UCRT64 の exe/DLL/plugin を混在させない
- 実測で `uxplay.exe` は少なくとも `libgcc_s_seh-1.dll`, `libwinpthread-1.dll`, `libstdc++-6.dll`, `libcrypto-3-x64.dll`, `libplist-2.0.dll` を直接 import する。同梱対象は固定リストではなく **import closure の再帰検査**で決める

### 5.3 runtime-manifest.json

CI が生成し、Readiness が検査する:

- UxPlay release tag と **exact commit SHA**（`21eef8df...`）
- toolchain（MSYS2 msystem、gcc/cmake バージョン）
- MSYS2 package versions
- runtime files と SHA-256
- GStreamer plugin directories
- bundled licenses 一覧

## 6. CI test surface

実行環境は GitHub-hosted `windows-latest` + `msys2/setup-msys2`（Q28）。Windows container は GitHub-hosted で使えず、self-hosted runner は現段階で過剰。再現性は Docker ではなく、exact commit pin・明示的 toolchain・manifest・smoke test で確保する。

### 6.1 現行 CI の既知の欠陥（修正必須）

- UxPlay build step が `shell: bash`（MSYS2 環境外）で走り、CMake が Visual Studio generator を選択して `Could NOT find PkgConfig` で失敗している
- 修正: `shell: msys2 {0}` + `cmake -G Ninja` の明示。`build-uxplay-github-actions.sh` 内の bare `cmake ..` を廃止

### 6.2 検査項目（Ready runtime 認定条件・Q29）

Static:

1. pin 整合性: `.gitmodules` が `https://github.com/FDH2/UxPlay.git`、fresh recursive clone 後の gitlink = `21eef8df...`、`git ls-remote refs/tags/v1.73.6` も同 SHA
2. import closure: exe と全 plugin DLL を再帰検査し、artifact 外参照（`C:\msys64` 等）を失敗扱い。plugin scanner も対象
3. manifest: 全ファイルの SHA-256 一致、license 同梱
4. CLI golden test: `BuildCommand` の出力検証
   - `-v` / `-m` / `-r` / `-a2` を誤出力しないこと（negative test）
   - `-hls` / `-async` / `-vsync no` / `-p` / `-n` / `-vs` / `-as` / `-al` / `-fps` / `-pw` / `-mp4` の token 化と値
   - 日本語・空白・引用符を含むデバイス名の round-trip
   - Expert override の重複 flag 後勝ち、redaction（`ToDiagnosticLaunch` に Expert token が現れない）

Dynamic（**MSYS2 を setup しない別 job** で artifact をダウンロードして実行——runtime isolation の検証を兼ねる）:

5. `uxplay.exe -v` が厳密に `1.73.6` を表示、`-h` が期待 option 集合を返す
6. bundled environment だけで `gst-inspect-1.0` が以下を解決:
   `appsrc, h264parse, h265parse, aacparse, decodebin, videoconvert, avdec_aac, playbin3, d3d11videosink, directsoundsink, wasapisink, mp4mux, rtph264pay, rtph265pay, rtpL16pay, udpsink, jpegdec, imagefreeze, textoverlay`
7. 最小起動 smoke: 空きポートで UxPlay host を起動、ready marker を待ち、timeout 内に正常停止。異常終了・missing DLL/plugin を失敗扱い

AirPlay client からの実接続（映像・音声）は CI では行わない（Deferred の hardware E2E）。

## 7. 移行計画（次セッション以降の実装順）

今回は設計のみ（Q30）。実装セッションの推奨順:

1. `.gitmodules` URL を `https://github.com/FDH2/UxPlay.git` へ変更 + gitlink を `21eef8df` へ更新（**既存 clone は `git submodule sync --recursive` が必要**）
2. CI 修正（`shell: msys2 {0}` + Ninja）で v1.73.6 が MINGW64 で clean build することを確認
3. Runtime bundle 収集 + manifest 生成 + static/dynamic 検査を CI へ追加
4. Launch contract module 実装（golden test 含む）
5. MainWindow を新 module へ接続、UI 再編成
6. Mirror / Audio-only / password / 30・60fps / Mirror→HLS→Mirror / 反復 connect-disconnect の実機回帰（v1.73.6 の主修正は HLS 遷移と `"select: not a socket"` 経路）

注意: v1.73.6 は CMake >= 3.13 を要求。`-Ofast` → `-O3` 化と `mux_renderer.c` 常時ビルド化があるが、Windows の新規 pkg-config 依存はない。

## 8. Deferred（却下ではなく延期）

| 項目 | 内容 | 再検討の契機 |
|---|---|---|
| Expert override の永続化 | Q19-(b): 入力値の保存 | Launch profile 保存機能の設計時 |
| Launch profile の保存 | Q38: typed profile の JSON 保存 | 利用者からの実需 |
| Hardware AirPlay E2E | 実 client からの接続検証 | self-hosted runner + 実機を用意できたとき |
| 新 flags の typed 昇格 | `-lang` / `-scrsv` / `-vrtp` / `-artp` / `-ble` | Expert override での実需確認後 |
| UCRT64 化 | MINGW64 からの移行 | v1.73.6 移行の安定後、別 job で評価 |
| auto videosink（d3d12） | 明示的 d3d11 指定の見直し | upstream の d3d12 安定確認後 |
| 外部 MSYS2 adapter | Runtime bundle 以外の実行環境 | 実需が出たとき（現状 YAGNI） |
| `ILaunchContract` seam 再評価 | 二つ目の adapter が長期間現れない場合の interface 廃止 | 次の UxPlay release 移行時 |
| バージョン別 password 規則 | code(4) vs README(6) の矛盾が upstream で解消された場合の追従 | submodule 更新時 |

## 9. ADR について

現時点で ADR は作らない（Q31）。今回の決定はすべて実装前で可逆であり、「hard to reverse」を満たさない。将来、次のような不可逆・意外性のある決定が生じた時点で ADR 化する:

- 旧 release 用 adapter を互換性のため恒久維持する決定
- Windows container / self-hosted runner の正式採用
- Runtime bundle の配布形態を変える決定（installer / portable zip 等）

## 10. 根拠資料

- v1.73.6 tag ref: `https://api.github.com/repos/FDH2/UxPlay/git/ref/tags/v1.73.6` → `21eef8df25d91e12635c36d8176ad192725baca2`
- 差分: `https://github.com/FDH2/UxPlay/compare/a08c51e662658bffcefafdbe591e1f261d116f4f...v1.73.6`（194 commits ahead / 52 files / behind 0）
- v1.73.6 `uxplay.cpp`（help: L909–L1010、`-fps` 上限 255: L1305–1312、`-al` 0–10 秒: L1592–1604、port 範囲/password 長: L76–86 付近の `#define`）
- 現行 launcher の欠陥箇所: `MainWindow.xaml.cs` L99–137（BuildArgs）、L189–205（`C:\msys64` 固定環境変数）、`DependencyManager.cs` L10–20（7 DLL 固定リスト）、`.github/workflows/build.yml` L35–39（MSYS2 外 shell）
- GStreamer Windows deployment: `https://gstreamer.freedesktop.org/documentation/deploying/windows.html`
