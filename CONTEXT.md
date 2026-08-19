# UxPlay Launcher Context

Windows上でUxPlayを準備・起動・停止するランチャーです。利用者がAirPlay受信を開始できる状態を整え、runtimeの準備状態と失敗理由を理解できることを中心に扱います。

## Language

### UxPlay host

AirPlay接続を待ち受けるWindows側の受信機です。UxPlayの実行ファイル、native runtime、GStreamer plugin、ネットワーク設定が揃って初めてReadyになります。
_Avoid_: server（ネットワーク実装だけを指す場合）、launcher（UxPlay hostそのものを指す場合）

### AirPlay client

UxPlay hostへ接続して画面ミラーリングまたは音声ストリーミングを開始するiPhone、iPad、Macなどの端末です。
_Avoid_: source device、sender（文脈がない場合）

### Runtime bundle

UxPlay hostを外部のMSYS2環境へ依存せずに起動するための実行ファイル、native DLL、GStreamer plugin、設定、ライセンス通知をまとめた配布単位です。
_Avoid_: portable（runtime bundleが自己完結していることを検証していない場合）

### Launch profile

解像度、FPS、音声、映像sink、アクセス制御など、UxPlay hostの一回の起動に使う利用者向けの設定です。利用者が意味を選び、UxPlay固有のコマンドライン表現は内部で解決されるものとします。
_Avoid_: raw flags、command string（利用者向け概念として）

### Readiness

UxPlay hostを起動できるかを表す状態です。実行ファイル、runtime bundle、GStreamer plugin、設定値、ネットワーク準備の検査結果を含みます。
_Avoid_: path found（実行ファイルが見つかっただけの状態）

### Diagnostic log

起動、接続、停止、警告、失敗を利用者が原因調査に使うための情報です。秘密情報や不要な端末識別子を含めず、画面表示と保存内容で同じredaction規則を使います。
_Avoid_: raw process output（利用者向けの意味付けをしない場合）

### Build artifact

特定のUxPlay source commit、toolchain、runtime bundle、launcher publish結果から生成され、検査可能な配布物です。build artifactは再現可能で、含まれる依存関係とライセンスを追跡できる必要があります。
_Avoid_: executable（UxPlay単体だけを指す場合）

## Established findings

### Runtime bundle is not yet a proven self-contained unit

公開済みportable artifactのUxPlay executableは、MinGW runtime、OpenSSL、libplistなどのnative DLLとGStreamer pluginを要求しますが、配布内容とruntime検査の責務が分散しています。外部MSYS2が必要な状態をportableまたはself-containedと呼ばないことを基準にします。

### Build ownership is split

UxPlayのbuildはCI、dotnet publish、過去のBuildServiceという複数の経路にまたがっています。Build artifactを一度だけ作り、検査してからlauncherへ渡すことを基本方針とします。利用者向けlauncherと開発者向けsource buildは別の関心です。

### Launch meaning must match the pinned UxPlay contract

現在のsubmoduleはUxPlay 1.72系を指しています。Launch profileの項目はこの固定されたUxPlayの実仕様に対応していなければなりません。対応しない項目、意味が異なる項目、起動に反映されない項目は利用者向け画面に残しません。

### Secrets do not belong in Diagnostic log

UxPlayのアクセス用パスワードは、画面ログ、保存ログ、表示用の起動コマンドへ出してはいけません。Custom argumentsにも秘密情報を入力し得るため、redactionまたは明示的な制限が必要です。

### MainWindow currently owns too much knowledge

UI、Launch profileの収集、引数の組み立て、環境変数、runtime準備、process lifecycle、Diagnostic logが一つの画面実装へ集中しています。この状態では変更のlocalityとtestsの信頼性が低く、runtime、launch contract、process lifecycle、operator UIの各moduleを独立して深める余地があります。

### Readiness and lifecycle are different facts

「uxplay.exeが見つかった」「起動処理中」「接続待ち」「AirPlay client接続済み」「正常停止」「異常終了」は別の状態です。一つの文字列を上書きして表現せず、利用者が次に取るべき操作と失敗理由を区別します。

### CI is part of the product contract

CIは単にlauncherをcompileする場所ではなく、Build artifactの再現性、runtime dependency、GStreamer plugin、ライセンス通知、最小起動を検査する場所です。現状はWindows向け実行をmacOS上で完全検証できないため、Windows runnerでの明示的なsmoke testが必要です。

### Documentation is a source of truth

root READMEとnested READMEは同じclone、build、runtime前提を説明しなければなりません。repository名、submodule初期化、Bonjour/MSYS2の要否、artifactの出力先、ライセンス、対応UxPlay source commitが実装と一致していることを基準にします。

## Investigation record

- 対象リポジトリ: `koh11235813/UxPlayLauncher`
- 監査対象HEAD: `f4ff03f` (`main`)
- 調査日: 2026-08-20
- 見つかった主な改善候補: Runtime bundle、Build ownership、Launch contract、Process lifecycle、Operator UI、Release provenance
- Windows向けWPFの実ビルドと実機AirPlay接続は、監査環境がmacOSで.NET SDKとWindows toolchainを持たないため未検証
- 詳細な候補比較とbefore/after図: `/private/var/folders/4p/c6d6mn915yngx55y3qqcybf00000gn/T/architecture-review-20260820_014450.html`

## External references

- UxPlay pinned source: `https://raw.githubusercontent.com/antimof/UxPlay/a08c51e662658bffcefafdbe591e1f261d116f4f/README.md`
- UxPlay pinned CLI implementation: `https://raw.githubusercontent.com/antimof/UxPlay/a08c51e662658bffcefafdbe591e1f261d116f4f/uxplay.cpp`
- UxPlay pinned Windows dependency configuration: `https://raw.githubusercontent.com/antimof/UxPlay/a08c51e662658bffcefafdbe591e1f261d116f4f/lib/CMakeLists.txt`
- MSYS2 GitHub Action usage: `https://raw.githubusercontent.com/msys2/setup-msys2/main/README.md`
