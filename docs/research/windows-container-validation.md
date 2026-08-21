# Windows コンテナ／Windows ビルド検証の実行可能性

調査日: 2026-08-22

## 結論

- GitHub-hosted `windows-latest` は Windows VM と Docker CLI を提供するが、Windows コンテナを CI の安定した検証基盤として保証していない。`jobs.<job>.container`、Docker container action、service container の GitHub が文書化する対応は Linux runner である。Windows コンテナを手動 `docker run` できるかは別問題であり、probe job での実測なしに利用可能とは扱わない。process isolation はホストとイメージの Windows build 互換性に依存し、Hyper-V isolation は nested virtualization を必要とする。
- Apple Silicon macOS の Docker Desktop と Colima は Linux VM／Linux container の実行環境であり、Windows コンテナ daemon にはならない。Windows 11 Arm64 VM 自体は Apple Silicon 上に作れるが、Windows コンテナと Hyper-V isolation がその VM で検証できることを Microsoft は保証していない。`win-x64` のユーザーモード実行は Windows on Arm の x64 エミュレーションで試せるが、x64 Windows CI と同値ではない。
- .NET SDK は仕様上、`EnableWindowsTargeting=true` を明示すれば macOS から `net8.0-windows`／WPF の targeting pack と runtime pack を取得して restore/build できる。ただしこの端末の Homebrew `dotnet@8` 8.0.130 では WindowsDesktop SDK targets がなく、現行 WPF project は `MSB4019` で失敗した。WPF は Windows 専用なので、macOS で WPF UI、WindowsDesktop runtime、WPF の実行時テストはできない。純粋な契約ロジックを `net8.0` のテストプロジェクトへ分離すれば macOS でも実行できる。
- この repo の受け入れ検証は、macOS では契約テストを必須とし、WPF compile は WindowsDesktop targets を持つ SDK でだけ補助的に行う。Windows runner では MinGW/UxPlay build と PE/DLL/GStreamer の実行検査、準備済み Windows 実機では Bonjour/DNS-SD と AirPlay E2E を分けるのが妥当である。Windows コンテナを再現性の根拠にはしない。

以下では、資料に明記された事実（事実）と、repo への適用判断（推論）を分ける。

## 1. GitHub-hosted `windows-latest` と Windows コンテナ

### 事実（公式資料）

- GitHub-hosted runner は通常 1 job ごとに作られる VM で、Windows runner は Microsoft Azure の VM 上でホストされる。[GitHub-hosted runners](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners)
- GitHub runner の container job 対応実装では Windows runner の container operations が未対応として追跡されている。[actions/runner #904](https://github.com/actions/runner/issues/904)
- GitHub の self-hosted runner 要件では、Docker container action または service container を使う場合は Linux machine と Docker が必要と明記されている。[Self-hosted runners reference](https://docs.github.com/en/actions/reference/runners/self-hosted-runners#requirements-for-self-hosted-runner-machines)
- `jobs.<job_id>.container` の公式例は Linux の `ubuntu-latest` であり、job container を使わない場合は `runs-on` のホスト上で step が直接実行される。[Running jobs in a container](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/run-jobs-in-a-container)
- Windows Server 2025 runner image の mutable な `main` 文書には Docker、Docker Compose、MSYS2、GCC、CMake が掲載されることがあるが、このリンク自体は調査日の snapshot／provenance ではない。[Windows2025 runner image（current）](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) 実際の CI run は runner log の `Image Version`、`Included Software` の immutable release URL、tool version を artifact metadata に保存し、その run の根拠を固定する。
- Windows コンテナは Windows 10/11 Pro または Enterprise と Hyper-V を前提に準備し、Windows Server コンテナは Windows Server の物理機または VM をホストにする、というのが Microsoft の手順である。[Windows コンテナ環境の準備](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/set-up-environment)
- Windows コンテナには process isolation と Hyper-V isolation があり、process isolation はホスト／イメージの build 互換性に依存する。Hyper-V isolation はコンテナ自身の Windows kernel を持つ。[Windows container version compatibility](https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/version-compatibility)

### 推論と判定

- 「Windows runner に Docker CLI がある」ことと「GitHub が Windows コンテナ CI をサポートする」ことは別である。`jobs.container`／container action／service container の GitHub の対応モデルを Windows コンテナ検証へ読み替えてはならない。
- `docker run` を Windows runner 上で直接試す余地はあるが、process isolation は runner の Windows build と pull したイメージの build を毎回合わせる必要がある。`windows-latest` の image 更新とタグの patch 更新が組み合わさるため、再現性のある受け入れ条件にならない。
- `--isolation=hyperv` は nested virtualization に依存する。標準 GitHub-hosted Windows runner でこれを利用できるという製品保証は確認できないため、成功しても安定した受け入れ基盤とはみなさない。Windows コンテナの実行可否を CI の必須条件にするなら、機能を明示的に有効化・管理できる Windows self-hosted runner／VM を用意する。

## 2. Apple Silicon macOS でのローカル Windows コンテナ

### 事実（公式資料）

- Docker Desktop for Mac は Docker daemon とコンテナを Docker 管理の軽量 Linux VM 内で実行する。[Docker Desktop for Mac の権限と Linux VM](https://docs.docker.com/desktop/setup/install/mac-permission-requirements/#containers-running-as-root-within-the-linux-vm)
- Docker Desktop の「Linux／Windows container daemon の切り替え」は Windows の機能として説明されており、Mac の Docker Desktop に Windows daemon モードは記載されていない。[Docker Desktop](https://docs.docker.com/desktop/)
- Colima は公式 README で「macOS (and Linux) の container runtimes」と説明され、Docker runtime を選ぶと macOS 上の VM で Docker を動かす設計である。[Colima README](https://github.com/abiosoft/colima)
- Microsoft は Windows 11 Arm64 ISO を Apple Silicon Mac で VM 作成に使えるとしている。[Windows 11 Arm ISO files](https://learn.microsoft.com/en-us/windows/arm/iso)
- Windows 11 on Arm は x86 と x64 のユーザーモードアプリをエミュレーションできるが、エミュレーションは kernel-mode code／driver を対象にしない。[How emulation works on Arm](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation)
- Microsoft の Windows コンテナ手順は Windows Pro/Enterprise と Hyper-V を要求し、コンテナの isolation と Windows build の組み合わせには互換性表がある。[Windows コンテナ環境の準備](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/set-up-environment)、[互換性表](https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/version-compatibility)

### 推論と判定

| ローカル方式 | Windows コンテナ検証 | 判定理由 |
|---|---:|---|
| Docker Desktop for Mac | 不可 | Mac 側の daemon は Linux VM 内。Windows daemon への切り替えは Windows 専用の機能として説明されている。 |
| Colima | 不可 | macOS/Linux 用の Linux VM／container runtime。Windows guest／Windows container runtime の公式モードがない。 |
| Windows 11 Arm64 VM | 条件付き・受け入れ基盤にはしない | Windows VM 自体は作成可能。ただし Microsoft の Windows コンテナ要件、Hyper-V、Windows build／image 互換性を Apple Silicon 上の VM で満たせる保証は資料から得られない。 |

Windows 11 Arm64 VM で `win-x64` の UxPlay／DLL を起動できる可能性は、x64 **ユーザーモード**エミュレーションから導ける推論に過ぎない。GStreamer の Windows plugin、Bonjour、ネットワーク、コンテナの Hyper-V isolation は個別に検証する必要があり、x64 Windows 実機／runner の結果を代替しない。

## 3. macOS での .NET 8 `net8.0-windows`／WPF

### 事実（公式資料）

- `NETSDK1100` は Linux/macOS で Windows target を build したときのエラーで、`EnableWindowsTargeting=true` を project property または CLI で指定すると解消できる。[NETSDK1100](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)
- この property は、非 Windows SDK に同梱されていない Windows targeting pack（self-contained の場合は runtime pack も含む）を opt-in で取得するためのものだ。[NETSDK1100 の pack 説明](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)
- WPF は .NET が cross-platform でも Windows でのみ実行される。[WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

### repo への適用

現在のプロジェクトは [`TargetFramework=net8.0-windows` と `UseWPF=true`](../../UxplayLauncher/UxplayLauncher/UxplayLauncher.csproj) を設定している。公式仕様上は、macOS で次のような compile 検証を行える（NuGet へのアクセスと WindowsDesktop SDK targets を含む対応 SDK が別途必要）。

```sh
dotnet restore UxplayLauncher/UxplayLauncher/UxplayLauncher.csproj \
  -p:EnableWindowsTargeting=true
dotnet build UxplayLauncher/UxplayLauncher/UxplayLauncher.csproj \
  -p:EnableWindowsTargeting=true --no-restore
```

ここで検証できるのは C#／XAML の compile、参照解決、MSBuild の構成であり、WPF の window creation、D3D11、Windows audio、Windows process／DLL loading の成功ではない。WPF を参照する test assembly を macOS で実行することも、WPF が Windows 専用であるため受け入れ条件にしない。

2026-08-22 のローカル実測では、`/opt/homebrew/opt/dotnet@8/bin/dotnet` は `net8.0` class library の restore/build に成功した一方、現行 WPF project は `Microsoft.NET.Sdk.WindowsDesktop.targets` 不在による `MSB4019` で失敗した。この環境では WPF compile をローカル必須条件にせず、Windows runner で実行する。

Launch contract のように Windows UI／Process API から独立させられるロジックは、別 test project を `net8.0` にして macOS と Windows の双方で `dotnet test` を実行する。WPF view、`ProcessStartInfo` の Windows runtime 挙動、GStreamer／Bonjour の smoke は Windows 側だけで実行する。

なお、現在の [`csproj`](../../UxplayLauncher/UxplayLauncher/UxplayLauncher.csproj) は `Publish` 前に [`invoke-msys2-build.ps1`](../../UxplayLauncher/scripts/invoke-msys2-build.ps1) を呼ぶ構成である。`dotnet build` と `dotnet publish` は別の実行面であり、macOS からの WPF compile 成功を、そのまま Windows artifact publish／UxPlay build 成功と解釈しない。

## 4. repo の実用検証マトリクス

記号: ○ = その環境の受け入れ検証に適する、△ = 条件付き／部分検証、× = その環境では実行しない。

| 検証項目 | Apple Silicon macOS | GitHub `windows-latest` | 準備済み Windows x64（実機または管理下 VM） | 合格条件・境界 |
|---|---:|---:|---:|---|
| Launch contract の純粋 C# unit/golden test（`net8.0`） | ○ | ○ | ○ | token 化、validation、quoting、redaction。WPF 型を test project に持ち込まない。 |
| WPF restore/build（`net8.0-windows`、`EnableWindowsTargeting`） | △（SDK distribution 依存） | ○ | ○ | 公式には cross-build 可能だが、現 Homebrew SDK は WindowsDesktop targets 不足。 |
| WPF UI／WindowsDesktop runtime／UI test | × | △ | ○ | GitHub runner は非対話環境・image 更新を考慮し、最終確認は管理下 Windows。 |
| MSYS2 MINGW64 の UxPlay build | ×（repo script 前提外） | ○ | ○ | [`build.yml`](../../.github/workflows/build.yml) と MSYS2 package/toolchain を固定・記録する。 |
| PE header・DLL import closure の静的検査 | △ | ○ | ○ | macOS はファイル形式／hash の静的検査まで。実行時 search path は Windows で確認する。 |
| GStreamer plugin／Windows sink の解決 | × | ○ | ○ | `gst-inspect-1.0` で Windows plugin と runtime bundle のみから解決する。 |
| Bonjour/DNS-SD の存在と service 起動 | ×（Windows integration ではない） | △ | ○ | Apple terms を考慮した repo 方針として、[README](../../README.md) は Bonjour SDK を外部 prerequisite としている。CI image に既設・bundle 済みと仮定せず、Windows smoke 前に service／DNS-SD を確認する。 |
| `uxplay.exe -v/-h`、missing DLL/plugin 検査 | × | ○（artifact job） | ○ | artifact 単独で起動し、固定 release・依存解決・終了コードを検査する。 |
| 最小 UxPlay host smoke（空き port、ready marker、正常停止） | × | ○（外部 Bonjour 等を用意できる場合） | ○ | timeout、異常終了、runtime 外参照を失敗にする。 |
| 実 AirPlay client の映像／音声 E2E | × | × | △／○ | 実 Apple client と同一ネットワークが必要。CI ではなく手動／専用 Windows 機。 |
| Windows container の実行 | ×（Docker Desktop/Colima） | △（手動実験、非サポート） | ○（要 Hyper-V／build 互換性） | Docker を再現性の根拠にしない。実施時は host build、image digest、isolation を記録する。 |

### 推奨する責務分割

1. **macOS job**: `net8.0` の contract test。WPF restore/build は利用 SDK が WindowsDesktop targets を提供する場合だけ補助的に実行し、Windows 実行成功を主張しない。
2. **GitHub Windows job**: MSYS2 MINGW64 で UxPlay を build し、artifact を別 step/job で PE/DLL、GStreamer、CLI、最小 process smoke へ通す。`windows-latest` の image と tool version をログへ保存する。
3. **管理下 Windows job／手動検証**: 外部 Bonjour/DNS-SD の存在、Windows audio/video sink、実ネットワーク、AirPlay client、必要なら Windows container を検証する。

この分割なら、GitHub の unsupported nested virtualization や macOS の Linux-only container runtime を、Windows launcher artifact の正しさと混同せずに済む。

## 5. 一次資料

- GitHub: [GitHub-hosted runners](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners)、[Self-hosted runners reference](https://docs.github.com/en/actions/reference/runners/self-hosted-runners)、[Running jobs in a container](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/run-jobs-in-a-container)、[Windows2025 runner image](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)
- Docker: [Docker Desktop for Mac の権限と Linux VM](https://docs.docker.com/desktop/setup/install/mac-permission-requirements/)、[Docker Desktop](https://docs.docker.com/desktop/)
- Colima: [公式リポジトリ README](https://github.com/abiosoft/colima)
- Microsoft: [Windows 11 Arm ISO](https://learn.microsoft.com/en-us/windows/arm/iso)、[Windows コンテナ環境の準備](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/set-up-environment)、[Windows container version compatibility](https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/version-compatibility)、[Windows on Arm のエミュレーション](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation)
- .NET／WPF: [NETSDK1100](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)、[WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
