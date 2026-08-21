# Launch contract 設計 — UxPlay v1.73.6

- Status: **Conditional GO（Gate 2 完了、Gate 3 は submodule 明示承認待ち、Gate 6/7 は Bonjour provisioning 待ち）**
- 設計日: 2026-08-21
- 改訂日: 2026-08-22（Phase 2 reviewer findings 反映）
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

### 2.1.1 Project boundary（WPF からの分離）

- `UxplayLauncher/UxplayLauncher.LaunchContract/` に `UxplayLauncher.LaunchContract`（`net8.0` の WPF-independent class library）を置く。`UseWPF`、`System.Windows`、`WindowsBase`、WPF view 型を参照しない。Launch profile の検証、flag/token の構築、Readiness の manifest validator、redaction、Launch workspace factory、opaque command を消費する process adapter をこの module に置く。
- `UxplayLauncher/UxplayLauncher.LaunchContract.Tests/` に `UxplayLauncher.LaunchContract.Tests`（`net8.0` の test project）を置き、上記 module だけを参照する。macOS と Windows の双方で `dotnet test` を実行できることを受け入れ条件にする。
- WPF launcher は module の公開型だけを利用する。Windows の process 起動、WPF lifecycle、GStreamer、Bonjour/DNS-SD の実測は launcher 側の adapter または Windows の検証 job に閉じ込める。これにより、macOS で契約ロジックを検証できる（[Windows／macOS 検証調査](../research/windows-container-validation.md)）。

### 2.2 入力型

**`LaunchProfile`** — 利用者が選んだ起動設定の値オブジェクト。

- public constructor は次の入力をこの順で受け取る。型と排他関係をこの契約で固定し、実装者が nullable bool や複数 property の組み合わせを推測しないようにする。

```csharp
public LaunchProfile(
    string resolution,
    int maxFps,
    string deviceName,
    bool enableHls,
    bool asyncAudio,
    bool lowLatencyMirror,
    AudioOutput audioOutput,
    decimal? audioLatencySeconds,
    string? password,
    int? basePort,
    string videoSink,
    bool enableUxPlayDebug,
    Mp4Recording? recording);

public abstract class AudioOutput
{
    private AudioOutput();
    public sealed class Disabled : AudioOutput;
    public sealed class Sink : AudioOutput
    {
        public Sink(string name);
        public string Name { get; }
    }
}

public sealed record Mp4Recording(string? FileName);
```

- `CreateDefault()` は `1920x1080`、30 fps、`UxPlay-Windows`、HLS／async／low-latency／UxPlay debug OFF、`AudioOutput.Sink("directsoundsink")`、音声遅延／password／base port／録画なし、`d3d11videosink fullscreen-toggle-mode=alt-enter` を返す。音声 OFF は `AudioOutput.Disabled` で表し、sink と同時指定できる形を public model に持ち込まない。
- public constructor が全項目を検証し、不正なら **`LaunchProfileValidationException`** を投げる。例外は全 `ContractViolation`（field key・reason code・安全に表示できる値の要約）を固定 field 順で保持し、UI は一度の操作で全問題を表示できる。password の実値は exception、violation、message、`ToString()` のどこにも保持せず、存在／長さ不足という reason だけを返す。
- `null` または空文字の password は「未指定」へ正規化する。非空 password は Unicode scalar value で 6 文字以上とし、値自体は変更しない。argv または path に渡る全 string（resolution、device name、audio/video sink、password、recording filename）は U+0000 と unpaired surrogate を拒否する。device name／sink は空または空白だけの値を拒否するが、受理した値の空白や引用符を trim／rewrite しない。
- `Mp4Recording.FileName = null` は UxPlay の既定 filename を使う指定とする。非 null filename は空／空白、rooted path、drive／UNC prefix、`.`／`..` segment、U+0000、unpaired surrogate を拒否し、後段の `LaunchWorkspaceFactory` が workspace 内の実 path へ解決する。separator の検査は実行 OS に依存させず `/` と `\` の両方を segment delimiter として扱う。
- `audioLatencySeconds` は `decimal?` とし、0–10 の閉区間で検証する。command token へは `CultureInfo.InvariantCulture` と format `0.############################` で小数点を `.` に固定して出力する。
- 生成後の instance は常に valid（不変条件）。

**`ExpertOverride`** — Launch contract の意味保証の外にある上級者向け引数。

- constructor は `CommandLineToArgvW` と互換の backslash／double-quote／空白規則で入力を token 化する。shell expansion、環境変数展開、single-quote 特別扱いは行わず、閉じていない double quote は **`ExpertOverrideSyntaxException`**（文字位置と原因を保持）とする。空の quoted token は保持する。
- token 列は内部に隠し、Diagnostic log へは一切出さない。`-pw` など秘密を伴う flag も禁止しない。秘密を禁止する代わりに、下記の一律 redaction と Expert 有効時の process output 抑制を適用する。
- 引数の意味と対象 release での有効性は利用者責任。追加の runtime 依存（例: `-ble` の Python/winrt/psutil）は Ready runtime の保証外であり、UI は有効化時にその旨を警告する。

**`ManifestValidatedRuntime`** — bundle の静的検査を通過した artifact capability。

- WPF-independent な `RuntimeManifestValidator` だけが生成する。manifest schema、release/full SHA、file hash、path containment、import closure、GStreamer element 宣言、license、外部 Bonjour prerequisite 宣言が対象であり、Windows process／service の実在は保証しない。

**`ReadyRuntime`** — Windows の動的 Readiness まで通過した実行環境 capability。

- Windows の `IReadinessProbe` が `ManifestValidatedRuntime` を入力に、`uxplay.exe -v`、GStreamer element、DLL/plugin load、外部 Bonjour service/DNS-SD を検査した場合だけ生成する。constructor は module 内部に閉じ、manifest validator は `ReadyRuntime` を直接生成できない。
- `BuildCommand` は defense-in-depth として release 一致を再確認し、不一致なら **`LaunchContractMismatchException`** を投げる（利用者入力エラーとは区別される構成ミス）。

**`LaunchWorkspace`** — 書き込み可能な出力領域。

- ログ・録画（`-mp4`）・dump 等の相対パス出力はここを基準に解決する。既定はユーザー領域（例: `%LOCALAPPDATA%\UxplayLauncher\workspace`）。
- 不変の Runtime bundle と分離し、Program Files 配下への書き込み失敗を構造的に防ぐ。
- この containment 保証は typed `Mp4Recording` と launcher 自身が管理する出力に適用する。`Coverage = Partial` となる Expert override は path-bearing flag（例: `-mp4`／`-md`／`-vdmp`）を含められ、その path の意味・書き込み先は保証外である。Expert UI は「workspace 外へ書き込める」ことも警告し、Diagnostic log は raw path を公開しない。Expert の秘密 flag は引き続き禁止しない。

### 2.3 出力型

**`LaunchCommand`** — 安全な起動指示。raw token と秘密値は内部に隠す。

```csharp
public sealed class LaunchCommand
{
    public ContractCoverage Coverage { get; }   // Full | Partial

    public DiagnosticLaunch ToDiagnosticLaunch(); // typed secret は placeholder、Expert token は非公開
}

public interface ILaunchProcess
{
    LaunchSession Start(LaunchCommand command);
}
```

- `Coverage = Partial` は Expert override 有効時。意味検証と optional runtime capability が保証外であることを UI/log に明示する。
- `LaunchCommand` は public な opaque value とし、raw token、環境変数 map、`Arguments`、`ArgumentList`、`ProcessStartInfo` を public API に出さない。`ToDiagnosticLaunch()` が返すのは redaction 済みの表示値だけで、Expert token は含めない。
- production `ILaunchProcess` adapter は `LaunchCommand` と同じ `UxplayLauncher.LaunchContract` assembly に置き、同一 assembly の internal execution data を `ProcessStartInfo.ArgumentList` へ移す。shell は経由しない。WPF UI は `Start(LaunchCommand)` と `LaunchSession` の lifecycle event だけを利用し、raw command を再構築できる public seam は持たない。
- raw token や password を public にすると Diagnostic/UI 層が秘密を再流出させられる一方、password preservation、Expert tokenizer、command serialization をテストで観測不能にしてはならない。このため test-only の friend は `InternalsVisibleTo("UxplayLauncher.LaunchContract.Tests")` 一つだけに限定する。Gate 2 では internal `LaunchProfile.Password` の同値性だけを boolean assertion で検証し、Gate 4 で internal `ExecutionSpec`（executable、token 列、environment）を同じ friend へ追加する。production project は他の friend assembly を持たず、test は secret の実値を assertion API、failure message、snapshot、test output に渡さない。これは hostile な同一 process code に対する security boundary ではなく、production の public API と通常ログからの偶発的漏洩を防ぐ seam である。
- typed arguments と Expert token の連結順は、parser の検証対象として内部に固定する。ただし重複 flag の扱いは一律の「後勝ち」にしない。per-flag conflict policy を次のように適用する。
  - **Assignment**: 対象 release の parser が重複時の厳密な意味（最後の代入など）まで検証済みの flag だけ、typed 値に対する Expert override を許可する。未検証の assignment は重複を reject する。
  - **Toggle / non-idempotent**: v1.73.6 parser で反転動作を確認した `-d` のような toggle は duplicate を reject する。`-hls`、`-async` を含む他の bare flag は assignment／idempotent／toggle の分類を exact source で記録するまで、安全側で duplicate を reject する。
  - **Secret-bearing**: `-pw` などは許可対象のままにする（少なくとも単一指定は受理する）。禁止や値の推測は行わず、assignment の conflict policy に従って duplicate を受理または reject し、値は常に redactor の対象にする。Expert 有効時は UxPlay の raw stdout/stderr 自体を保存・表示しない。
  - **Unknown / unclassified**: Expert の意味保証外という性質は維持するが、同一 flag の duplicate は安全側で reject する。新しい assignment を許可するには、v1.73.6 parser の実挙動を test とこの表へ記録する。

### 2.3.1 Diagnostic redaction と process output

- typed password、Diagnostic launch 表示、通常起動時の UxPlay stdout/stderr は一つの `DiagnosticRedactor` を通す。caller ごとの ad-hoc masking や raw command の再構築は禁止する。
- 通常起動では既知の typed secret を redactor が置換し、秘密値を画面表示・保存ログ・`ToDiagnosticLaunch()` に出さない。
- Expert override が有効な起動では、任意 token に未知の秘密が入り得るため、UxPlay の raw stdout/stderr を redactor に通す前に **Diagnostic log への取り込み自体を抑制**する。開始・終了・exit code・timeout・Readiness など launcher lifecycle event は redacted metadata として残す。
- Expert 有効化時の UI には「任意引数に秘密が含まれ得るため、UxPlay stdout/stderr は Diagnostic log に記録しない」と表示する。`ToDiagnosticLaunch()` は Expert token をどのような形式でも公開しない。

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
| 解像度 | `-s wxh[@r]` | ASCII 数字のみ。幅・高さ 1–9999、refresh は省略可で 1–255 | 既定 `1920x1080`（UxPlay 既定と一致・Q37） |
| 最大 FPS | `-fps n` | 1–255 | 既定 30。presets 30/60。「上限値」であることを UI 文言に明記 |
| デバイス名 | `-n name` | UTF-8 有効性 | 既定 `UxPlay-Windows`。日本語・空白・引用符の round-trip を CI golden test で保証 |
| HLS | `-hls` | bare のみ（`2`/`3` は Expert） | OFF |
| 音質優先同期（Audio-Only） | `-async` | bare | OFF |
| 低遅延（Mirror） | `-vsync no` | 固定値 | OFF |
| 音声 OFF | `-as 0` | 排他: AudioSink | OFF |
| AudioSink | `-as sink` | 非空文字列 | `directsoundsink` |
| 音声遅延 | `-al x` | 0–10 秒（小数可） | **空欄時は渡さない**（UxPlay 既定 0.25 秒を尊重・Q35） |
| パスワード | `-pw pwd` | **6 文字以上**（launcher 側規則。code は `MIN_PASSWORD_LENGTH 4` だが README は 6 文字以上で矛盾しており安全側を採用） | 空なら渡さない |
| 基底ポート | `-p n` | 1024–65533。UxPlay が base/base+1/base+2 の 3 ポートを使うため、65534/65535 は不可 | 空なら渡さない |
| VideoSink | `-vs sink` | 非空文字列 | `d3d11videosink fullscreen-toggle-mode=alt-enter`（明示指定を維持。auto の d3d12 化は別変更） |
| デバッグログ（UxPlay） | `-d` | bare | OFF。**`GST_DEBUG` とは別物として扱う**（Q34）。GStreamer 詳細度は UI に出さず、ON 時に `GST_DEBUG=3` |
| 録画 | `-mp4 [fn]` | 出力先は Launch workspace 配下に解決 | OFF（Q36 で typed 昇格） |

**UI から削除する項目**（v1.73.6 実装で確認済みの事実に基づく）:

- `-a2`: **存在しない**（旧・新どちらにも）。UxPlay 自体が AirPlay2 legacy protocol receiver
- `-r`（「RAOP 対応」表示）: 実際は 90 度回転 `-r {R|L}`。通常 UI から削除、Expert override で利用可
- `-v`（「詳細ログ」表示）: 実際は version 表示後に即終了。削除
- `-m`（「ミラーリングモード」表示）: 実際は MAC/Device ID 変更。通常 UI から削除、Expert override で利用可

**Expert override に留める新 flags**: `-lang`, `-scrsv`, `-vrtp`, `-artp`, `-ble`（`-ble` は Python/winrt/psutil 依存のため Ready runtime 保証外の警告対象）

解像度は文字列の正規表現だけで受理せず、ASCII 数字を整数へ parse して各境界を検証する。`0x1080`、`10000x1080`、全角数字、`1920x1080@0`、`1920x1080@256`、port `65534`/`65535` は negative case とする。

## 4. UI 再編成（Operator UI への入力）

- 通常 UI は typed Launch profile の明示的入力欄のみ。flag 定義テーブルからの動的生成はしない
- Expert mode: checkbox は起動時常に OFF。ON で警告文と入力欄を表示。起動中は status に「Expert override 有効」を表示。値は保存しない
- Expert mode の警告には、任意 token が秘密を含み得るため UxPlay の stdout/stderr を Diagnostic log に記録しないことを明記する。launcher の lifecycle event（開始・終了・失敗・timeout）は継続して redacted 形式で表示する。
- `LaunchProfileValidationException` の violations は項目単位で入力欄の近くに表示
- `ExpertOverrideSyntaxException` は「位置 N: 引用符が閉じていません」形式で表示

## 5. Runtime bundle 配置と manifest

### 5.1 配布契約

利用者に MSYS2 を要求せず、UxPlay executable、MinGW/native DLL、GStreamer runtime/pluginについて自己完結する Runtime bundle（Q25）。「7 DLL だけコピー」方式は廃止する。外部 Bonjour/DNS-SD はこの自己完結性の例外として manifest と Readiness に明示する。

Bonjour/DNS-SD は Runtime bundle に含めない外部 prerequisite とする。Apple の配布条件が確定していないため、Apple SDK/runtime/service を bundle へ取り込まない。UxPlay v1.73.6 の native build は Bonjour SDK の headers/libs を build 時に必要とするため、native CI job は承認済みの SDK provisioning がなければ失敗させる。provisioning は Apple から直接取得した検証済み入力、またはその出所・hash を追跡できる組織管理 artifact に限定し、非公式 mirror を fetch しない。runtime smoke と Ready 判定では、対象 Windows に外部 Bonjour service/DNS-SD が存在・起動できることを別途検査する。[v1.73.6 upstream README](https://github.com/FDH2/UxPlay/blob/21eef8df25d91e12635c36d8176ad192725baca2/README.md) の前提と一致させる。

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

manifest には bundle に含めない external prerequisite の宣言（Bonjour service、native build に使った Bonjour SDK の provenance/hash）も記録する。ただし SDK/runtime/service 自体を bundle 済みとは扱わない。

### 5.4 Launcher-side Readiness validator と Launch workspace factory

- `RuntimeManifestValidator` は launcher 側で manifest と実ファイルを再検査し、CI の判定を無条件に信頼しない。必須 schema、exact UxPlay commit、runtime file の SHA-256、bundle 内 path containment、import closure の外部参照、GStreamer plugin directory、license、外部 Bonjour prerequisite の宣言を検査する。
- Windows の動的 Readiness adapter は `uxplay.exe -v`、必要な `gst-inspect-1.0` element、missing DLL/plugin、Bonjour service/DNS-SD の存在を確認する。d3d11 sink が contract default の間は `d3d11videosink` と `d3d11h264dec` の両方がなければ Ready にしない。exact v1.73.6 upstream は decoder default に `decodebin` を使うため、`d3d11h264dec` は upstream 必須条件ではなく launcher が意図的に採用する厳しい Windows baseline である。software decoder 対応を広げる場合は別 gate で capability smoke へ置き換える。
- validator は `ManifestValidatedRuntime` または構造化された static violation を返し、WPF 型を参照しない。release mismatch、manifest tamper、hash mismatch、path traversal はここで fail closed する。Windows の `IReadinessProbe` はこれを受け取り、missing `d3d11h264dec`、DLL/plugin load failure、外部 prerequisite 欠落を dynamic violation として返す。すべて成功したときだけ `ReadyRuntime` を発行し、`BuildCommand` は `ReadyRuntime` 以外を受け取らない。
- `LaunchWorkspaceFactory` は既定のユーザー領域を作成・検査し、ログ・録画・dump の実パスが workspace 外へ出ない `LaunchWorkspace` を生成する。Runtime bundle や Program Files を workspace として受理しない。factory の実装と path containment／作成失敗の test を Launch contract test project に含める。
- lexical な rooted／`..`／`Path.GetFullPath` 検査だけでは workspace 内の symlink／junction／Windows reparse point から外部へ脱出できる。factory は既存の各 path component をリンク解決して workspace の実体 path 配下であることを確認し、未知または追跡不能な reparse point を fail closed する。Linux/macOS の symlink test と Windows の junction/reparse-point test の両方で `workspace/link -> outside` を拒否する。

## 6. CI test surface

Windows container は acceptance basis にしない。macOS は WPF-independent な `net8.0` contract test、fresh Windows build job は native artifact 作成、別の fresh Windows job は `setup-msys2` なしで artifact を download して smoke、管理下の Windows 環境は外部 Bonjour/AirPlay E2E を担当する（詳細は [Windows／macOS 検証調査](../research/windows-container-validation.md)）。Docker が使えたことや Windows container の実験成功は、Windows launcher artifact の受け入れ根拠にしない。

### 6.1 Job model と single native-build ownership

1. **macOS contract-test job**: `UxplayLauncher.LaunchContract.Tests` の `net8.0` `dotnet test` のみを必須とする。validation、内部の限定 test seam を用いた token 化、conflict policy、redaction、manifest tamper/release、workspace、境界値を実行する。Windows の `CommandLineToArgvW` 実 process round-trip、WPF UI、WindowsDesktop runtime、GStreamer、Bonjour/AirPlay の成功は主張しない。
2. **fresh Windows native-build job**: 承認済み Bonjour SDK provisioning と `msys2/setup-msys2` を用い、pinned UxPlay を MINGW64/Ninja で **一度だけ** build し、runtime staging directory と manifest を Build artifact として出力する。native build の所有者はこの job だけとする。
3. **fresh Windows artifact-smoke job**: `setup-msys2`、UxPlay source、native build script を持たない新しい Windows job が Build artifact を download し、PE/import closure、manifest/hash、`uxplay.exe -v/-h`、GStreamer、最小 host smoke を実行する。外部 Bonjour runtime/service は承認済み provisioning で用意するか、欠落を明示的な prerequisite failure とする。
4. **managed Windows E2E**: 外部 Bonjour/DNS-SD service、Windows audio/video sink、実ネットワーク、Apple AirPlay client を管理下の Windows 実機または VM で検証する。GitHub-hosted job や Windows container の代替成功を E2E とみなさない。

`dotnet publish` は staging 済みの native artifact を入力として packaging するだけであり、UxPlay を build しない、source tree を探さない、`uxplay.exe` を再帰 search して別のものを採用しない。publish に必要な staging path がなければ fail closed とし、`invoke-msys2-build.ps1` のような native build target を Publish 経路から除去する。この invariant を CI の publish test と review checklist で確認する。

現行の `Services/UxplayBuildService.cs` も native build の第二経路であり、`BuildUxplayAsync` が source fetch、`git reset --hard`、native build script 呼び出しを行う。single native-build ownership を成立させるには、Publish target の除去だけでなく、この service と UI 呼び出しを削除または「承認済み staging artifact の検証／取得だけを行う adapter」へ置換し、launcher 実行中に source checkout を変更したり native build を開始できないことを repository search と test で確認する。

native inputs の一部（runner image、MSYS2 package、Bonjour SDK の取得経路）が未 pin の間は、成果物の主張を bit-for-bit の「reproducible build」とせず、exact source commit、toolchain/package version、runner image、SDK provenance/hash、artifact file hash の **provenance/traceability** と表現する。再現可能性を主張するにはこれら全入力を pin する必要がある。

### 6.2 現行 CI の既知の欠陥（修正必須）

- UxPlay build step が `shell: bash`（MSYS2 環境外）で走り、CMake が Visual Studio generator を選択して `Could NOT find PkgConfig` で失敗している
- 修正: `shell: msys2 {0}` + `cmake -G Ninja` の明示。`build-uxplay-github-actions.sh` 内の bare `cmake ..` を廃止。Bonjour SDK はこの job の承認済み provisioning でだけ供給し、非公式 mirror を参照しない。

### 6.3 検査項目（Ready runtime 認定条件・Q29）

Static:

1. pin 整合性: `.gitmodules` が `https://github.com/FDH2/UxPlay.git`、fresh recursive clone 後の gitlink = `21eef8df...`、`git ls-remote refs/tags/v1.73.6` も同 SHA
2. import closure: exe、plugin scanner、全 plugin DLL の PE import 名を再帰解決する。各 import は artifact 内の一意な file、またはversion管理した Windows system-DLL allowlistのどちらかへ解決されなければ失敗とする。PE import table に絶対pathがあるとは仮定しない。別jobの実行時検査では `PATH` と GStreamer path をartifact＋Windows system directoryだけに制限し、MSYS2由来の偶然の解決を失敗扱いにする
3. manifest: 全ファイルの SHA-256 一致、license 同梱
4. CLI golden test: `BuildCommand` → `ToDiagnosticLaunch()` の public interface から、redaction 済み typed token 表現を検証
   - `-v` / `-m` / `-r` / `-a2` を誤出力しないこと（negative test）
   - `-hls` / `-async` / `-vsync no` / `-p` / `-n` / `-vs` / `-as` / `-al` / `-fps` / `-pw` / `-mp4` の token 化と値
   - 日本語・空白・引用符を含むデバイス名の redaction 済み typed token 表現。実際の `ArgumentList` round-trip は Windows の process-adapter integration test で、引数を検証して exit code を返す capture executable を外部 process として起動し、`LaunchSession` の public outcome から検証する
   - Expert override の per-flag conflict policy（verified assignment のみ override、duplicate toggle/non-idempotent は reject、secret-bearing flag は許可）、redaction（`ToDiagnosticLaunch` に Expert token が現れない）
   - typed password の一元 redaction、Expert 有効時の stdout/stderr suppression と lifecycle event 保持
   - 解像度（ASCII、1–9999、refresh 1–255）と base port（1024–65533）の境界値 negative test
   - manifest tamper/release mismatch、workspace path containment、LaunchWorkspace factory 作成失敗

Dynamic（**MSYS2 を setup しない別 job** で artifact をダウンロードして実行——runtime isolation の検証を兼ねる）:

5. `uxplay.exe -v` が厳密に `1.73.6` を表示、`-h` が期待 option 集合を返す
6. bundled environment だけで `gst-inspect-1.0` が以下を解決:
   `appsrc, h264parse, h265parse, aacparse, decodebin, videoconvert, avdec_aac, playbin3, d3d11videosink, d3d11h264dec, directsoundsink, wasapisink, mp4mux, rtph264pay, rtph265pay, rtpL16pay, udpsink, jpegdec, imagefreeze, textoverlay`
7. 最小起動 smoke: 空きポートで UxPlay host を起動、ready marker を待ち、timeout 内に正常停止。異常終了・missing DLL/plugin を失敗扱い

AirPlay client からの実接続（映像・音声）は CI では行わない（Deferred の hardware E2E）。

### 6.4 Test project と `dotnet test`

`UxplayLauncher.LaunchContract.Tests` は `net8.0` とし、macOS で次を実行できることを最初の gate にする。

Gate 2 の test framework は成熟した xUnit v2 を選び、`xunit 2.9.3`、`xunit.runner.visualstudio 3.1.5`、`Microsoft.NET.Test.Sdk 17.14.1` を project file で固定する。リリース直後の xUnit v3 系への移行と、要求のない coverage collector はこの slice に含めない。

```sh
dotnet test UxplayLauncher/UxplayLauncher.LaunchContract.Tests/UxplayLauncher.LaunchContract.Tests.csproj
```

最初の Gate 2 では `LaunchProfile` と aggregate validation の test だけを作る。境界値に加え、複数違反が固定 field 順で一度に返ること、argv/path-bound string の U+0000／unpaired surrogate、recording filename の rooted／traversal path、password の違反が実値を保持・表示しないことを含める。以下は対応する後段 gate で同じ test project へ累積追加する。最終的なテスト集合には、typed flag の golden token、Expert tokenizer の documented quoting grammar、ASCII 境界、verified/unverified Expert conflict、duplicate toggle reject、secret-bearing `-pw` の受理と redaction、Expert 時の stdout/stderr suppression、manifest hash/release/plugin tamper、workspace containment/factory failure を含める。内部 token は限定 test seam だけで観測し、secret 値を失敗メッセージや snapshot に埋め込まない。Windows の実 process における `ArgumentList` round-trip、`d3d11h264dec` の動的解決、Bonjour は Windows job で検証する。WPF 型を test assembly に持ち込まない。

## 7. 移行計画（次セッション以降の実装順）

設計の fresh external review で Gate 2 は GO、全体は Conditional GO となった。承認済み Bonjour SDK provisioning がないため Gate 6/7 はまだ実行できず、以下の各 gate を通過するまで全体を実装完了とは扱わない。

1. **External review gate**: 本改訂（Bonjour 外部 prerequisite、redaction、Expert conflict policy、single native build、Windows 検証分割）を再レビューし、最初の core slice に関する未解決仕様を閉じる。
2. **Pre-submodule core gate**: submodule／Bonjour／WPF に触れる前に `UxplayLauncher.LaunchContract`（`net8.0`）と `UxplayLauncher.LaunchContract.Tests`（`net8.0`）だけを作る。上で固定した `LaunchProfile`、`AudioOutput`、`Mp4Recording`、aggregate validation を test-first で実装し、macOS の `dotnet test` を独立して通す。これが最初の実装 slice であり、Expert／command／manifest／workspace を混ぜない。
3. **Submodule approval gate**: `.gitmodules` URL を `https://github.com/FDH2/UxPlay.git` へ変更し、gitlink を `21eef8df25d91e12635c36d8176ad192725baca2` へ更新する操作は、他の実装変更と分離して別途明示承認を得る。承認後にのみ `git submodule sync --recursive`、fresh clone、tag SHA の一致を検証する。
4. **Launch command gate**: exact v1.73.6 source から assignment／toggle 分類を記録し、ExpertOverride、opaque LaunchCommand、per-flag conflict policy、DiagnosticRedactor を実装する。public diagnostic API と限定 internal test seam の両方を test し、macOS の `dotnet test` を通す。
5. **Manifest/workspace gate**: `RuntimeManifestValidator`、`ManifestValidatedRuntime`、`LaunchWorkspaceFactory` を実装する。manifest tamper、release mismatch、hash mismatch、lexical path traversal、symlink／junction／reparse-point escape、workspace containment/factory failure を negative test で確認し、manifest validator が `ReadyRuntime` を発行できないことを compile/API test で固定する。
6. **Native build ownership gate**: fresh Windows native-build job で承認済み Bonjour SDK provisioning を確認してから UxPlay v1.73.6 を MINGW64/Ninja で一度だけ staging へ build する。manifest と provenance/traceability を生成する。WPF csproj の Publish target に加えて `UxplayBuildService.BuildUxplayAsync` の fetch／`reset --hard`／build 経路と UI 呼び出しを削除または artifact-only adapter へ置換し、native build がこの job 以外で起きないことを確認する。
7. **Artifact isolation／dynamic readiness gate**: fresh Windows の `setup-msys2` なし job が staging artifact を download して PE/import closure、GStreamer（`d3d11videosink` と `d3d11h264dec` を含む）、CLI、最小 host smoke、外部 Bonjour prerequisite を検査する。成功時だけ Windows `IReadinessProbe` が `ManifestValidatedRuntime` から `ReadyRuntime` を発行する。`dotnet publish` は staged native input のみを package し、UxPlay を build/search しないことを確認する。
8. **Launcher integration gate**: trusted process adapter と MainWindow を接続し、opaque LaunchCommand の内部 token 化、Windows capture executable による `ArgumentList` round-trip、Expert 有効時の stdout/stderr suppression、lifecycle event、UI warning、Launch workspace を実装する。WPF build/runtime の検証は Windows 側で行う。
9. **Managed Windows E2E gate**: 外部 Bonjour/DNS-SD service を備えた管理下 Windows で Mirror / Audio-only / password / 30・60fps / Mirror→HLS→Mirror / 反復 connect-disconnect を回帰する（v1.73.6 の主修正は HLS 遷移と `"select: not a socket"` 経路）。Apple AirPlay client を用いる実接続はこの gate に限定する。

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
- [UxPlay v1.73.6 upstream README（exact commit）](https://github.com/FDH2/UxPlay/blob/21eef8df25d91e12635c36d8176ad192725baca2/README.md)
- [Windows コンテナ／Windows ビルド検証の実行可能性（repo research note）](../research/windows-container-validation.md)
- 差分: `https://github.com/FDH2/UxPlay/compare/a08c51e662658bffcefafdbe591e1f261d116f4f...v1.73.6`（194 commits ahead / 52 files / behind 0）
- v1.73.6 `uxplay.cpp`（help: L909–L1010、`-fps` 上限 255: L1305–1312、`-al` 0–10 秒: L1592–1604、port 範囲/password 長: L76–86 付近の `#define`）
- 現行 launcher の欠陥箇所: `MainWindow.xaml.cs` L99–137（BuildArgs）、L189–205（`C:\msys64` 固定環境変数）、`DependencyManager.cs` L10–20（7 DLL 固定リスト）、`.github/workflows/build.yml` L35–39（MSYS2 外 shell）
- GStreamer Windows deployment: `https://gstreamer.freedesktop.org/documentation/deploying/windows.html`
