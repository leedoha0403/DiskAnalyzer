# 자동 업데이트 + GitHub Release 배포 가이드 (WPF / .NET)

새 데스크톱 프로그램을 만들 때 **처음부터** 이 구조로 잡기 위한 문서다. 작업 시작 전에 끝까지 읽고 따른다.
기준 구현은 두 곳이다. 이 문서의 코드 조각은 모두 거기서 온 것이니, 막히면 원본을 보고 그대로 옮긴다.

| 프로젝트 | 위치 | 비고 |
|---|---|---|
| AIUsageMonitor | `C:\Work\App` (`UsageMonitorWpf/Shell/`, `tools/publish-release.ps1`, `.github/workflows/release.yml`, `.claude/skills/release-package/SKILL.md`) | **완성형**. 태그를 push 하면 CI 가 릴리스를 만든다 |
| DiskAnalyzer | `C:\Work\DiskAnalyzer` (`src/DiskAnalyzer.App/SelfUpdater.cs`, `src/DiskAnalyzer.Core/Services/UpdateChecker.cs`, `MainViewModel.About.cs`) | 앱 쪽 코드는 이식됨. **배포 스크립트 / CI 는 아직 없음** (수동 publish) |

---

## 1. 전체 그림

```
[개발자]  버전 올림 → 커밋 → 태그 vX.Y.Z push
             │
             ▼
[GitHub Actions]  publish-release.ps1 → 릴리스 자산 3개를 만들어 GitHub Release 로 게시
             │      · <App>.exe                    (제자리 교체용, 이름 고정)
             │      · <App>-vX.Y.Z-win-x64.zip     (수동 설치 / 옛 버전 호환)
             │      · SHA256SUMS.txt               (위 두 파일의 해시)
             ▼
[사용자 PC의 앱]  시작할 때 releases/latest 조회 → 새 버전이면 "지금 업데이트" 버튼
             │      exe 다운로드 → SHA256 검증 → 임시 도우미 exe 실행 → 앱 종료
             ▼
[도우미]  앱 종료 대기 → 기존 exe 를 .bak 으로 옮김 → 새 exe 복사 → 앱 재실행
             (실패하면 .bak 으로 되돌리고 재실행)
```

핵심 아이디어 두 가지:
1. **실행 중인 exe 는 자기 자신을 덮어쓸 수 없다** → 자기 자신의 *임시 복사본*을 `--apply-update` 로 띄워 대신 교체하게 한다.
2. **받은 exe 를 실행하기 전에 반드시 해시를 검증한다** → `SHA256SUMS.txt` 에 그 exe 의 줄이 *없으면* 실패로 본다(항목 필수).

---

## 2. 프로젝트 설정 (csproj)

```xml
<Version>X.Y.Z</Version>
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
<InformationalVersion>X.Y.Z-internal</InformationalVersion>
<AssemblyName>MyApp</AssemblyName>   <!-- exe 이름. 아래 ExeAssetName 과 반드시 같아야 한다 -->
```

- 네 값은 **항상 같이** 올린다. `-internal` 은 로컬 개발 빌드 표시이고, 릴리스 빌드는 publish 스크립트가 `-p:InformationalVersion=X.Y.Z` 로 덮어쓴다.
- **함정**: .NET 은 빌드 때 `InformationalVersion` 뒤에 `+<git-sha>` 를 자동으로 붙인다(`0.5.3+65f549a…`). 버전을 비교하는 코드는 **첫 `-` 또는 `+` 앞부분**만 써야 한다. `-` 만 자르면 업데이트 확인이 조용히 망가진다(AIUsageMonitor v0.5.0 에서 실제로 발생). 아래 `UpdateChecker.ParseVersion` 이 이미 맞게 되어 있으니 그대로 쓴다.
- 배포 산출물은 **자체 포함(self-contained) 단일 파일**이다: `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `DebugType=None`. 그래야 exe 한 개만 바꿔 넣어도 앱이 완전하다. 폰트·이미지는 `<Resource>` 로 exe 안에 넣고, 런타임에 exe 옆 파일을 읽는 코드(`AppContext.BaseDirectory`)는 없거나 "없어도 되는" 것만 둔다. 교체는 **exe 하나만** 바꾸기 때문이다.

---

## 3. 앱 쪽 코드 — 파일 3개 + 연결

### 3-1. `UpdateChecker` (확인 · 다운로드 · 해시 검증)
원본: `C:\Work\DiskAnalyzer\src\DiskAnalyzer.Core\Services\UpdateChecker.cs`. 복사한 뒤 **저장소 URL 과 User-Agent 만** 바꾼다.

- `https://api.github.com/repos/<owner>/<repo>/releases/latest` 를 조회한다(User-Agent 헤더 필수, 타임아웃 15초). 실패하면 `null` — 오프라인이어도 앱은 조용히 동작해야 한다.
- 자산 이름으로 찾는다: `FindExe(exeName)`, `FindZip()`, `FindChecksums()` (= `SHA256SUMS.txt`).
- `IsNewer(tag, current)`: 위 §2 의 `+`/`-` 자르기 규칙.
- `VerifyChecksum(sums, fileName, path, requireEntry)`: **exe 는 `requireEntry: true`**, zip 은 false(예전 릴리스 호환).
- 릴리스가 private 이면 `releases/latest` 가 404 다 → 저장소는 public 이어야 한다.

### 3-2. `SelfUpdater` (교체 도우미)
원본: `C:\Work\DiskAnalyzer\src\DiskAnalyzer.App\SelfUpdater.cs`. 바꿀 것: `ExeAssetName`, `UpdateDirectory`(`%LOCALAPPDATA%\<App>\update`), 임시 파일 접두어, 로그 함수.

| 멤버 | 하는 일 |
|---|---|
| `CanReplaceInPlace()` | 실행 exe 옆에 쓰기 테스트 파일을 만들어 본다. Program Files 등 쓰기 금지면 false → **zip 다운로드로 폴백**. `dotnet.exe` 로 띄운 개발 실행도 false |
| `LaunchHelper(newExe)` | 자기 exe 를 `%TEMP%\<App>-updater-<guid>.exe` 로 복사해 `--apply-update <pid> <대상exe> <새exe>` 로 실행 |
| `RunHelper(args)` | (임시 복사본에서) 부모 pid 종료를 최대 30초 대기 → 대상을 `.bak` 으로 이동 → 새 exe 복사 → 실패 시 `.bak` 복구 → 대상 재실행. 파일 잠김(백신 등)에 대비해 **300ms 간격 최대 20회 재시도** |
| `CleanupBackup()` | 새 빌드가 뜨면 남은 `.bak` 삭제 |

### 3-3. `App.xaml.cs` 시작부 — **순서가 중요하다**
```csharp
protected override void OnStartup(StartupEventArgs e)
{
    // (다른 특수 인자 처리가 있으면 여기)

    // 도우미 모드: UI·단일 인스턴스 뮤텍스보다 먼저. 창을 띄우지 않고 끝난다.
    if (e.Args.Length == 4 && e.Args[0] == SelfUpdater.ApplyArgument)
    {
        SelfUpdater.RunHelper(e.Args);
        Environment.Exit(0);
        return;
    }
    SelfUpdater.CleanupBackup();

    base.OnStartup(e);
    // ... 일반 시작
}
```
- **단일 인스턴스 가드(Mutex)가 있는 앱은 도우미 분기를 그 *앞*에 둔다.** 안 그러면 도우미가 뮤텍스에 걸려 즉시 종료하거나, 새로 뜬 앱이 옛 프로세스가 아직 살아 있다고 판단해 끝난다.
- 도우미가 재실행한 앱은 일반 인자 없이 시작한다(교체 후 원래 인자가 필요하면 인자를 도우미에 추가로 넘기도록 확장).

### 3-4. 화면 / ViewModel 연결
원본: `MainViewModel.About.cs`(DiskAnalyzer) 또는 `MainViewModel.cs` 의 `CheckForUpdateAsync / DownloadUpdateAsync / InstallUpdateAsync`(AIUsageMonitor).

- 앱 시작 시 백그라운드로 `CheckForUpdateAsync(manual:false)`. 새 버전이면 상태바/알림으로만 알린다(**자동으로 설치하지 않는다** — 사용자가 버튼을 눌러야 한다).
- 버튼 문구는 `CanInstallInPlace` 로 분기: 가능하면 "지금 업데이트 (자동 재시작)", 아니면 "업데이트 내려받기".
- `InstallUpdateAsync` 순서: exe 다운로드(진행률) → 체크섬 파일 받기 → `VerifyChecksum(..., requireEntry: true)` 실패 시 **파일 삭제 후 중단** → `LaunchHelper` 성공하면 `Application.Current.Shutdown()`.
- 폴백(`DownloadUpdateAsync`): zip 을 `Downloads` 로 받고 체크섬 검증 후 탐색기로 보여 준다. 설치는 사용자가 한다.
- 다운로드 타임아웃 10분, 확인 타임아웃 15초.

---

## 4. 릴리스 패키징 스크립트 `tools/publish-release.ps1`
원본: `C:\Work\App\tools\publish-release.ps1`. 새 프로젝트로 복사해 프로젝트 경로 · zip/exe 이름만 바꾼다. 하는 일:

1. csproj 에서 `<Version>` 을 읽거나 `-Version X.Y.Z` 를 받는다.
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:InformationalVersion=$Version -o dist\release\$Version\win-x64`
3. 산출물 폴더를 `dist\release\<App>-vX.Y.Z-win-x64.zip` 으로 압축.
4. `dist\release\<App>.exe` 로 **고정 이름**의 단일 exe 복사본 생성 (자동 업데이트가 이 이름을 찾는다).
5. `SHA256SUMS.txt` 에 **zip 줄과 exe 줄 둘 다** 기록(`<해시>  <파일명>` 형식, 같은 이름의 예전 줄은 교체).

`dist/` 는 `.gitignore` 에 넣는다. 이 파일들은 커밋하지 않고 릴리스에만 첨부한다.

---

## 5. CI: 태그 push → 릴리스 자동 생성 `.github/workflows/release.yml`
원본: `C:\Work\App\.github\workflows\release.yml`.

```yaml
name: Release
on:
  push:
    tags: ['v*']
permissions:
  contents: write
jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }     # 프로젝트 TargetFramework 에 맞춘다
      - name: Package
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart('v')
          ./tools/publish-release.ps1 -Version $version
      - name: Publish release
        shell: pwsh
        env: { GH_TOKEN: '${{ github.token }}' }
        run: |
          $tag = "${{ github.ref_name }}"; $version = $tag.TrimStart('v'); $dir = "dist/release"
          $notes = if (Test-Path "release-notes/$tag.md") { @('--notes-file', "release-notes/$tag.md") } else { @('--generate-notes') }
          gh release create $tag "$dir/<App>-v$version-win-x64.zip" "$dir/<App>.exe" "$dir/SHA256SUMS.txt" --title $tag @notes
```
- `windows-latest` 러너에서 돌므로 WPF 빌드가 된다. dotnet 버전은 프로젝트 대상 프레임워크(net8/net9)에 맞춘다.
- 릴리스 본문은 `release-notes/vX.Y.Z.md` 가 있으면 그것을, 없으면 GitHub 자동 생성 노트를 쓴다. → **릴리스 노트를 미리 `release-notes/` 에 커밋해 두면** 한글 변경 내역이 본문이 된다.
- 로컬 `dist\release\SHA256SUMS.txt` 는 CI 산출물과 별개다(로컬은 확인용).

---

## 5-1. 릴리스 절차 (매번)

1. `git status` / `git log` 로 미커밋 작업 확인 — 사용자의 작업 중 변경을 릴리스에 섞지 않는다.
2. 다음 버전 결정: 버그 수정·소소한 변경은 patch, 의미 있는 기능은 minor. 사용자가 버전을 주면 그것을 쓴다.
3. csproj 네 값을 함께 올린다(§2).
4. **빌드 0 경고 0 오류 + 테스트 통과**를 확인한다.
5. (선택) 로컬 점검: `.\tools\publish-release.ps1 -Version X.Y.Z` 후 `(Get-Item dist\release\X.Y.Z\win-x64\<App>.exe).VersionInfo.ProductVersion` 이 기대한 버전인지 본다. 이때 **실행 중인 앱은 먼저 종료**(exe 잠김).
6. `release-notes/vX.Y.Z.md` 작성(추가 / 수정 구분, 사용자에게 보이는 말로).
7. 필요한 파일만 스테이징해 커밋(`git add -A` 금지), 태그 `git tag -a vX.Y.Z -m "vX.Y.Z"`, `git push origin main` 후 `git push origin vX.Y.Z`.
8. CI 결과 확인: `gh run list --workflow release.yml --limit 1`, `curl -s https://api.github.com/repos/<owner>/<repo>/releases/latest` 에 자산 3개가 있는지.
9. 위키 변경 내역(있는 프로젝트만)을 갱신한다.

> **커밋 · 푸시 · 태그 · 릴리스 게시는 밖으로 나가는 작업이다.** 사용자가 요청하거나 명시적으로 허용했을 때만 한다.

---

## 6. 지켜야 할 규칙 / 함정 모음

- **exe 자산 이름은 고정**이다(`<App>.exe`). 버전을 이름에 넣지 않는다. 앱의 `ExeAssetName` 상수와 스크립트·CI 가 모두 같아야 한다. 이름을 바꾸면 이미 배포된 앱은 업데이트를 못 찾는다.
- **`SHA256SUMS.txt` 에 exe 줄이 없으면 자동 업데이트는 실패**한다(의도된 동작). 스크립트가 zip/exe 둘 다 쓰는지 확인한다.
- **zip 은 계속 올린다.** 자동 업데이트가 없는 옛 버전은 `.zip` 자산을 찾아 수동 설치한다.
- **최초 도입 버전은 수동 배포**다. 자동 업데이트 코드가 없는 버전 사용자는 그 코드가 들어간 버전을 직접 받아 설치해야 하고, 그다음부터 자동이 된다. 처음부터 이 구조로 시작하면 이 문제는 없다.
- **교체는 exe 하나만** 한다. 설정·데이터는 `%LOCALAPPDATA%\<App>\` 에 두고(exe 옆에 두지 않는다), 교체 후에도 유지된다.
- **관리자 권한 재실행 등 exe 경로가 필요한 코드는 `Environment.ProcessPath` 를 쓴다.** 단일 파일 자체 포함 배포에서는 `Process.MainModule.FileName` 이 실행 중 풀린 내부 임시 경로를 가리킬 수 있고, 그 경로로 재실행하면 "You must install or update .NET" 오류가 난다(DiskAnalyzer v0.1.3 에서 고친 버그. `Assembly.Location` 도 단일 파일에서는 믿을 수 없다).
- 도우미 로그는 `%LOCALAPPDATA%\<App>\update\update.log`. 업데이트가 안 됐다는 제보는 여기부터 본다.
- 도우미는 임시 복사본이 뜰 때 자체 포함 exe 의 압축 해제 시간이 걸린다. 앱 종료 → 재시작 사이에 몇 초 공백이 있는 것은 정상이다.
- 확인 요청은 GitHub 무인증 API 한도(시간당 60회/IP)에 걸릴 수 있다. 시작 시 1회만 부르고 실패는 조용히 무시한다.
- 코드 서명이 없으므로 SmartScreen 경고가 뜰 수 있다. 서명은 별개 과제다.

---

## 7. 검증 체크리스트 (새 프로그램에 적용한 뒤)

- [ ] `ExeAssetName` = csproj `AssemblyName` + `.exe` = 스크립트/CI 의 exe 이름
- [ ] 버전 파서를 실제 빌드된 exe 의 `ProductVersion` 으로 검증(`+sha` 붙은 값)
- [ ] 도우미 단독 테스트: 더미 exe 두 개로 `앱.exe --apply-update 999999 <대상> <새것>` 실행 → 대상이 새것으로 바뀌고 `.bak` 이 남고 재실행되는지
- [ ] 체크섬 조작 테스트: `SHA256SUMS.txt` 의 해시를 틀리게 하면 "체크섬 검증 실패" 로 멈추고 다운로드 파일이 지워지는지
- [ ] 쓰기 금지 폴더에서 실행 → 버튼이 "업데이트 내려받기"(zip 폴백)로 나오는지
- [ ] 단일 인스턴스 앱이면 도우미 분기가 뮤텍스보다 앞인지
- [ ] 실제로 버전 두 개를 릴리스해 보고, 낮은 버전에서 "지금 업데이트" 가 끝까지 되는지(마지막에 한 번은 반드시 실전으로)
- [ ] 릴리스 페이지에 자산 3개(exe / zip / SHA256SUMS.txt)가 모두 있는지

---

## 8. DiskAnalyzer 현재 상태 (참고)

- 앱 쪽(§3)은 구현됨. 릴리스 자산에 `DiskAnalyzer.exe` + exe 줄이 든 `SHA256SUMS.txt` 가 있으면 자동 교체, 없으면 zip 폴백.
- 아직 없는 것: `tools/publish-release.ps1`, `.github/workflows/release.yml`, `release-notes/`. 현재는 README 의 수동 publish 명령과 `dist/release/` 로 손으로 만든다. 다음 릴리스 전에 §4·§5 를 이 저장소에도 이식하면 AIUsageMonitor 와 같은 흐름이 된다.
- 저장소: `leedoha0403/DiskAnalyzer`, exe 자산 이름: `DiskAnalyzer.exe`, 설정 폴더: `%LOCALAPPDATA%\DiskAnalyzer\`.
