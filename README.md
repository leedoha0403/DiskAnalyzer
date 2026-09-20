# Disk Analyzer

Windows 10/11 용 디스크 용량 분석 프로그램. WizTree / TreeSize 와 같은 목적이며
**빠른 스캔 / 한눈에 읽히는 UI / 대용량에서의 안정성** 세 가지를 우선한다.

- 플랫폼: .NET 9 (`net9.0-windows`, x64), WPF
- 솔루션: `DiskAnalyzer.sln`
- **배포용 실행 파일: `dist/DiskAnalyzer.exe`** — 단일 파일 · 자체 포함(.NET 설치 불필요), 그대로 복사해서 실행
  - `src/.../bin/Release/` 쪽 exe 는 옆의 DLL 과 .NET 9 Desktop Runtime 이 있어야 동작한다
- **설명서(HTML): [`docs/index.html`](docs/index.html)** — 사용법 · 동작 원리 · 성능 수치와 draw.io 다이어그램 6개
  (그림은 인터넷 연결이 필요하며, 원본은 `docs/diagrams/*.drawio`. 고친 뒤 `python docs/build.py` 로 다시 만든다)

---

## 1. 전체 아키텍처

```
┌───────────────────────────────────────────────────────────┐
│ DiskAnalyzer.App (WPF)                                    │
│   Views(XAML) / ViewModels / Controls(RatioBar, Treemap)  │
└───────────────┬───────────────────────────────────────────┘
                │ 150ms DispatcherTimer 로 "당겨 읽기"(pull)
┌───────────────▼───────────────────────────────────────────┐
│ ScanController            세션 관리 / 모드 선택 / fallback │
│   ├ FastScanner           NTFS $MFT 직접 파싱             │
│   └ CompatibilityScanner  FindFirstFileEx + Worker Pool   │
│              │ Channel<ScanBatch> (bounded)               │
│   Aggregator (단일 스레드)  NodeStore 의 유일한 writer     │
│              ▼                                            │
│   NodeStore (SoA + StringPool + TopKHeap + ExtensionTable)│
│              ▼                                            │
│   ProtectionEvaluator     P0~P3  "삭제해도 되는가"         │
│   CleanupScorer           0~100  "삭제할 가치가 있는가"     │
│   CleanupAnalyzer / CleanupGrouper / DeveloperAnalyzer     │
│   Services: DriveService / CacheService / ExportService /  │
│             ShellService / FileVerifier / DeletionService │
└───────────────────────────────────────────────────────────┘
```

UI 코드에는 파일 시스템 접근이 한 줄도 없다. 엔진은 UI 타입을 전혀 참조하지 않는다.

## 2. 스레드 구조

| 스레드 | 개수 | 역할 |
|---|---|---|
| UI | 1 | 렌더링 / 입력 / 상태 표시 |
| Worker | HDD 2, SSD `min(코어, 12)` | 디렉터리 열거 (I/O) |
| Aggregator | 1 | `NodeStore` 갱신, 스냅샷 게시 |
| 정리 분석 | 1 (일시) | 스캔 종료 후 읽기 전용 분석 |

- 파일/폴더마다 스레드를 만들지 않는다. `Task.Run` 기반 워커 풀 고정.
- **Aggregator 가 유일한 writer** 이므로 `NodeStore` 에는 lock 이 하나도 없다.
- 워커는 4,096건 단위 배치로만 공유 상태에 접근한다(파일당 접근 → 배치당 접근).

## 3. 스캔 파이프라인

```
Directory Queue (Channel<DirWorkItem>, unbounded)
        │  워커가 꺼내 한 단계만 열거하고, 찾은 하위 폴더를 다시 넣는다
        ▼
Worker Pool ──▶ ScanBatch (SoA + char 풀, 풀링되어 재사용)
        │  Result Channel (bounded = worker×4, FullMode=Wait → 자연스러운 backpressure)
        ▼
Aggregator ──▶ NodeStore  + 확장자 집계 + Top-K 힙  (한 번의 순회에서 동시에)
        │  150ms 마다
        ▼
ViewSnapshot (불변) ──▶ UI 가 참조 하나만 읽어 간다
```

- 재귀 함수로 전체 디스크를 도는 구조가 아니다 → 취소가 즉시 먹고, 병렬화가 가능하다.
- 종료 판정: `_pendingDirs` 를 Interlocked 로 세고 0이 되면 큐를 닫는다.
- 취소: 워커 루프 선두에서 `CancellationToken` 확인 → 큐에 남은 수십만 건을 처리하지 않는다.

## 4. 데이터 모델

`NodeStore` 는 인덱스 기반 트리를 **Structure of Arrays** 로 보관한다.

```
디렉터리: parent[], name[], ownSize[], ownFiles[], totalSize[], totalFiles[],
          totalDirs[], time[], attr[], category[]
파일    : parent[], name[], size[], time[], createdDay[], accessedDay[], ext[], attr[]
```

- **Full Path 를 저장하지 않는다.** parentId 체인으로 필요할 때만 만든다.
- 이름은 `StringPool`(2MB `char[]` 청크)에 연속 저장하고 60bit 핸들만 배열에 둔다.
- 생성일/접근일은 FILETIME(8B) 대신 "1601 기준 일수"(4B)로 저장 → 파일당 16B 절약.
- 삭제는 tombstone 으로 표시한다(파일 `parent = -1`, 폴더 `_dirDeleted[i]`).
  배열을 압축하지 않는 이유: **인덱스가 곧 노드 id** 라서 압축하면 모든 참조가 깨진다.

### 핵심 불변식: `자식 폴더 id > 부모 폴더 id`

Compatibility 스캔은 BFS 라 자연히 성립하고, Fast(MFT) 스캔도 MFT 인덱스를 그대로 쓰지 않고
루트에서 BFS 로 다시 번호를 매겨 이 불변식을 복구한다. 덕분에:

```csharp
for (int i = n - 1; i > 0; i--) {          // 폴더 크기 롤업 = 역순 1패스, O(D)
    int p = parent[i];
    totalSize[p] += totalSize[i];
    totalFiles[p] += totalFiles[i];
    totalDirs[p]  += totalDirs[i] + 1;
}
```

재귀도, 자식 리스트도, 두 번째 순회도 없다. 같은 파일의 크기를 두 번 세지 않는다.

## 5. UI 구조

- 탭: **폴더 / Treemap / 큰 파일 / 파일 유형 / 정리 추천 / 빠른 이동**
- 드라이브 카드(용량·사용률·파일시스템), 실시간 진행 패널, 상태바, 설정 패널, 성능 모니터
- **폴더 탭 / 큰 파일 탭**: Ctrl / Shift 다중 선택 + 선택 요약 + 일괄 삭제 (+ 큰 파일 탭은 새로고침)
  - 폴더를 선택하면 **하위 전체**가 삭제 대상이 되고, 확인 창이 하위까지 훑어 보호 등급을 매긴다
- **폴더 ↔ Treemap 연동**
  - 뒤로 / 앞으로 / 상위 / 루트 버튼과 경로 막대(breadcrumb)는 탭 위에 있고 **폴더·Treemap 이 함께 쓴다**(같은 위치 상태)
  - Treemap 에서 폴더를 더블 클릭하면 **탭을 바꾸지 않고 그 폴더로 들어간다**
  - **[분할 보기]** 토글: 폴더 목록(좌)과 Treemap(우)을 나란히 보여 준다(`GridSplitter` 로 너비 조절, 두 탭 어디서든 동일)
  - 폴더 목록에서 고른 항목은 Treemap 에서 테두리로 강조되고, Treemap 사각형을 누르면 목록에서 선택된다
    (깊은 곳을 눌렀다면 현재 폴더 바로 아래에서 그것을 품은 항목이 선택된다)
- **빠른 이동 탭** (`Views/QuickMove*`, 엔진은 `Core/QuickMove`): 찾아낸 파일을 실제로 옮기는 마지막 단계
  - 출발지 | 이동 컨트롤 | 목적지 두 패널(실제 파일 시스템 탐색) + 아래 **이동 대기열**. 고르고 → 를 누르면 **즉시 옮기지 않고 대기열에 쌓이며**, `[이동 시작]` 뒤에야 옮긴다
  - Breadcrumb · **경로 직접 입력**(✎ / Ctrl+L, 붙여넣기, `Tab` 폴더 자동완성) · 뒤로/앞으로/상위 · 즐겨찾기 · 최근 위치 · 빠른 위치 · 좌우 경로 교환 · 정렬 · Drag & Drop
  - 같은 드라이브면 이름 바꾸기로 **즉시 이동**, 다른 드라이브면 `CopyFileEx`(진행률/취소/일시정지)로 복사한 **뒤에만** 원본 삭제
  - 이동 전 검사: 원본·대상 존재, 쓰기 권한, 동일 위치, 자기 하위 폴더로 이동, 보호 등급(P0 금지 / P1 강한 경고), 대상 드라이브 여유 공간(부족하면 시작 불가)
  - 이름 충돌: 항목별 확인(기본) / 모두 건너뛰기 / 모두 새 이름 / 모두 덮어쓰기. 같은 이름 폴더는 병합
  - 대기열이 비어 있으면 자동으로 접혀 폴더 목록이 넓게 보이고, 목록 이름 열은 창 너비에 맞춰 늘어난다
  - **빠른 이동 도크**: 폴더 / Treemap / 큰 파일 등 분석 탭 오른쪽에 붙는 좁은 배치(출발지 위 · 목적지 아래). 화면 인스턴스가 탭과 같아서 선택·대기열이 이어진다.
    폴더 목록·Treemap 우클릭 → **이동 (좌)**(출발지에서 선택) / **이동 (우)**(목적지로 열기) / **이동 대기열에 추가**(현재 목적지로 바로 담기). 탭을 벗어나지 않는다
- **검색**: `*` `?` 와일드카드 지원(없으면 부분 일치), 범위는 "전체 / 현재 폴더 하위", 결과는 크기 상위 5,000건과 전체 건수를 표시
- 관리자 권한이 아니면 헤더에 **[관리자 권한으로 다시 실행]** 버튼이 뜬다(UAC 승인 후 같은 경로를 다시 스캔)
- 정리 추천 탭: 보기 방식 5종(기본 **폴더 트리 + 반복 패턴 접기**) + 그룹 3단계 체크박스 + 다시 분석
- 별도 창: **삭제 대상 확인**(판정 요약 → 개별 제외 → 진행률 → 결과)
- 모든 목록은 `VirtualizingPanel` + `Recycling` + `ScrollUnit=Pixel`
  → 결과가 수십만 건이어도 실제 UI 컨테이너는 화면에 보이는 수십 개뿐
- 점유율 막대는 `FrameworkElement` 하나가 `OnRender` 로 사각형 2개만 그리는 `RatioBar`
  (Rectangle+Grid 조합 대비 행당 Visual 3~4개 절감)
- Treemap 은 squarified 배치 + 카테고리 7색 + 깊이별 밝기, 폴더는 상단 헤더 띠에만 이름 표시
- 테마: 팔레트 `ResourceDictionary` 만 교체(모든 스타일이 `DynamicResource`), 타이틀 바까지 다크

## 6. 메모리 전략

| 기법 | 효과 |
|---|---|
| SoA + 인덱스 트리 | 파일당 객체 0개 (헤더 16B×N 제거) |
| StringPool | 이름 문자열 객체 N개 → 청크 N/50만 개 |
| 경로 미저장 | 공통 prefix 중복 제거 |
| 일 단위 타임스탬프 | 파일당 16B 절약 |
| 배치 풀링 | 스캔 중 배치 할당 사실상 0 |
| Bounded 결과 채널 | Aggregator 지연 시 워커가 대기 → 큐 폭주 방지 |
| Lazy | 폴더별 파일 CSR 인덱스는 스캔 후 처음 필요할 때만 생성 |

실측: **2,913만 파일 스캔 시 피크 working set 3.63 GB** (파일당 약 125B, 긴 `.svn-base` 이름 포함).

## 7. NTFS Fast Scan 전략

1. `\\.\C:` 볼륨 핸들 열기 (관리자 권한 필요)
2. 부트 섹터(BPB) 파싱 → 섹터/클러스터 크기, `$MFT` 위치, FILE 레코드 크기
3. 레코드 0(`$MFT` 자신)의 비상주 `$DATA` 런리스트 디코딩 → MFT 의 물리적 배치
4. 런을 따라 4MB 단위 **순차 읽기**, 1KB FILE 레코드마다
   - Update Sequence Array 픽스업 복원
   - `$STANDARD_INFORMATION`(생성/수정/접근/속성), `$FILE_NAME`(부모 참조 + 이름), `$DATA`(실제 크기)
   - DOS 8.3 전용 이름은 우선순위를 낮춰 무시, 확장 레코드의 `$DATA` 크기는 기본 레코드에 합산
5. 루트(레코드 5)에서 BFS 로 트리 구성 + 노드 id 재번호 → 고아 레코드 자동 제외

**Fast Scan 이 불가능하면(비 NTFS / 권한 없음 / 하위 폴더 스캔 / 파싱 실패) 예외 없이
Compatibility 로 자동 전환**하고 상태 메시지로 이유를 알린다.

> ⚠ **현재 상태**: Fast Scan 경로는 구현되어 있으나 이 개발 환경이 비관리자 세션이라
> 실행 검증을 하지 못했다. 아래 명령으로 관리자 권한에서 먼저 확인할 것.
> ```
> DiskAnalyzer.Bench.exe C:\ --mode fast
> ```
> 실패하면 자동으로 Compatibility 로 내려가므로 앱이 깨지지는 않지만,
> Fast 경로의 실측 성능/정확도는 아직 미검증이다.

## 8. 예상 병목과 실측 결과

| 예상 병목 | 대응 | 실측 |
|---|---|---|
| `FindNextFile` syscall | `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH` | 28만 파일 0.72s (397k/s) |
| 과도한 병렬화 | 워커 상한 12 | 스윕 결과 8~12에서 포화, 16은 오히려 저하 |
| Aggregator 단일 스레드 | 배치 4,096 | 2,913만 파일에서 CPU 9%, 병목 아님 |
| UI 렌더링 | 가상화 + 150ms 배치 갱신 | 스캔 중 UI 멈춤 없음 |
| 메모리 | SoA + 풀 | 2,913만 파일 3.63 GB |

### 워커 수 스윕 (`--sweep`, 28코어 / NVMe, 28.7만 파일, 웜 캐시)

| Worker | Elapsed | Files/sec |
|---:|---:|---:|
| 1 | 16.42s* | 17,504 |
| 2 | 2.98s | 96,546 |
| 4 | 1.17s | 245,620 |
| 8 | 0.91s | 316,027 |
| **12** | **0.85s** | **339,404** |
| 16 | 1.12s | 256,176 |
| 24 | 0.89s | 322,547 |

\* 1 워커 회차는 콜드 캐시 영향 포함.

### 대규모 실측

| 대상 | 모드 | Files | Folders | Size | Elapsed | Files/sec | Peak WS |
|---|---|---:|---:|---:|---:|---:|---:|
| `C:\DNF\...\Src` | Compat | 287,390 | 17,710 | 261.3 GB | 0.72s | 396,642 | 97 MB |
| `C:\` 전체 | Compat | **29,131,628** | 2,084,828 | 3.41 TB | 330.1s | 88,253 | 3.63 GB |

`C:\` 전체 스캔에서 접근 거부 303건 / 건너뜀 359건이 발생했지만 스캔은 중단 없이 완료되었다.
정확성은 PowerShell `Get-ChildItem -Recurse` 합계와 대조해 파일 수·바이트 수가 정확히 일치함을 확인했다.

## 9. 프로젝트 구조

```
DiskAnalyzer/
  DiskAnalyzer.sln
  dist/                              배포용 단일 exe (publish 산출물)
  src/
    DiskAnalyzer.Core/               스캔 엔진 (UI 참조 없음)
      Models/    NodeStore, StringPool, TopKHeap, ExtensionTable,
                 Categorizer, SizeFormatter, Rows, ScanOptions/Progress/Enums,
                 Protection, NodeMutations, CleanupModels, CleanupGrouping
      Scanning/  ScanController, CompatibilityScanner, FastScanner,
                 ScanBatch, ScanRuntime, ScanResult, Ntfs/MftReader
      Analysis/  ProtectionEvaluator, CleanupScorer,
                 CleanupAnalyzer, CleanupGrouper, DeveloperAnalyzer
      Interop/   Win32 (FindFirstFileEx, GetFileAttributesEx, DeviceIoControl, ...)
      Services/  DriveService, CacheService, ExportService, ShellService,
                 FileVerifier, DeletionService
    DiskAnalyzer.App/                WPF UI
      Themes/    Dark.xaml, Light.xaml, Shared.xaml
      Controls/  RatioBar, TreemapControl
      ViewModels/ MainViewModel, CleanupViewModel,
                  DeleteReviewViewModel, ObservableObject
      MainWindow.xaml(.cs), DeleteReviewWindow.xaml(.cs),
      App.xaml(.cs), app.manifest
    DiskAnalyzer.Bench/              벤치마크 러너
  tests/
    DiskAnalyzer.Tests/              xunit (184 tests)
```

## 10. 보호 등급 — "삭제해도 되는가"

프로그램은 삭제 안전성을 단정하지 않지만, **명백히 건드리면 안 되는 것은 아예 선택되지 않게** 막는다.

| 등급 | 의미 | 기본 동작 | 예 |
|---|---|---|---|
| **P0** | 절대 보호 | 선택 / 삭제 불가 | `pagefile.sys`, `System32`, 레지스트리 하이브, 드라이버, 부팅 영역 |
| **P1** | 고위험 | 기본 미선택, 강한 경고 후 삭제 | `Program Files`, `ProgramData`, `AppData`, Junction, 사용 중인 파일 |
| **P2** | 정리 가능 | 조건 충족 시 정리 추천 | Temp / Cache / 오래된 로그 / 빌드 산출물 |
| **P3** | 일반 파일 | 일반 삭제 가능 | 다운로드, 동영상, 압축 파일, 문서 |

한 대상이 여러 규칙에 걸리면 **항상 가장 높은 등급이 이긴다**(`Math.Max`).
정리 가능 규칙이 보호 규칙을 덮어쓰는 코드 경로는 구조적으로 존재하지 않는다 —
분석기가 보호 판정을 먼저 하고, P0/P1 이면 점수 계산 자체를 건너뛴다.

### 판정 순서

```
1. 경로 정규화 실패                                  → P0
2. 시스템 핵심 파일 (pagefile/hiberfil/bootmgr/BCD)  → P0
3. 부팅 · 레지스트리 · 드라이버 · 복구 영역            → P0
4. OS 핵심 경로 (System32/WinSxS/servicing/...)      → P0
5. Reparse Point (Junction/SymLink)                  → P1 + 재귀 금지
6. 현재 사용 중 (삭제 직전 재검증에서만)              → P1
7. Windows / Program Files / ProgramData / AppData   → P1
8. 알려진 Temp / Cache / Dump 경로                   → P2 후보
9. + 30일 이상 미수정 + 재생성 가능                   → P2
10. 그 외                                            → P3
```

몇 가지 함정을 의도적으로 피했다.

- **파일명만으로 판단하지 않는다.** 페이지 파일은 레지스트리 `PagingFiles` 값으로 실제 설정을 확인하고,
  이름 규칙으로 거를 때도 드라이브 루트 직속인지까지 본다 → `D:\Backup\pagefile.sys` 는 P0 가 아니다.
- **확장자만으로 P1 을 만들지 않는다.** `Downloads\old_setup.exe` = P3, `Program Files\App\app.exe` = P1.
- **`AppData\Local\Temp` 는 AppData 규칙에 먹히지 않는다.** 실사용 디스크에서 가장 큰 정리 대상이 여기 있다.
- **`D:\DBBackup\important.tmp` 는 P3 로 남는다.** 확장자가 `.tmp` 여도 알려진 임시 경로가 아니다.

### 대량 파일에 적용하는 방법

보호 판정은 전체 경로 문자열이 필요한데 `C:\` 전체에는 파일이 2,913만 개 있다.
**파일마다 경로를 만들면 그것만으로 수십 초**가 날아간다. 그래서 2단 구조를 쓴다.

1. **1차** — 폴더 상속 플래그(`SystemCore` / `System` / `AppData` / `Cache` ...)로 경로 문자열 없이 대량 제외. O(폴더 + 파일)
2. **2차** — 크기·나이 조건을 통과한 수천 건만 실제 경로로 정밀 판정

폴더 카테고리 상속은 "자식 id > 부모 id" 불변식 덕분에 폴더만 오름차순 1패스면 완성된다.
이 구조 덕분에 287,390 파일 트리의 전체 분석이 **26ms** 에 끝난다.

> 1차 분류용 플래그를 추가하며 `CategoryFlags` 를 `ushort` → `uint` 로 넓혔다(폴더당 2B 증가).

## 11. 정리 추천 — "삭제할 가치가 있는가"

보호 등급과 **완전히 별개 축**이다. P2/P3 에만 점수를 계산한다.

| 요소 | 가중치 |
|---|---:|
| 마지막 수정 후 경과 기간 | 0 ~ 30 |
| 파일 크기 (로그 스케일) | 0 ~ 25 |
| 마지막 접근 후 경과 기간 | 0 ~ 20 |
| Cache / Temp 여부 | 0 ~ 15 |
| 재생성 가능 여부 | 0 ~ 10 |

| 점수 | 구간 | 처리 |
|---|---|---|
| 0 ~ 29 | 추천 안 함 | 목록에 올리지 않음 |
| 30 ~ 59 | 참고 대상 | 표시 |
| 60 ~ 79 | 정리 추천 | 표시 |
| 80 ~ 100 | 우선 정리 추천 | 기본 정렬 상단 |

| 파일 | 보호 등급 | 정리 추천 |
|---|---:|---:|
| 오래된 ISO | P3 | 높음 |
| 오늘 생성한 ZIP | P3 | 낮음 |
| Temp 캐시 | P2 | 높음 |
| Program Files DLL | P1 | 대상 제외 |
| `pagefile.sys` | P0 | 대상 제외 |

**크기에 로그 스케일을 쓰는 이유**: 선형이면 초대형 파일 하나가 목록을 지배해서
"작지만 확실히 지워도 되는" 후보가 밀려난다.

### 그 외 정리 추천 규칙

- **설명 없는 추천은 만들지 않는다.** 모든 후보에 선정 이유 문장이 붙는다.
- **접근일을 단독으로 믿지 않는다.** 표본을 떠서 갱신이 꺼져 있으면 수정일 기준으로만 판단하고 그 사실을 화면에 표시한다.
- **생성일도 신호로 쓴다.** 다운로드/설치 파일은 "언제 받아 놓고 방치했는가"가 수정일보다 정확하다.
- **`.git` 은 크기만 알려 준다.** 캐시처럼 자동 정리 추천하지 않는다.
- **중복 가능 파일은 내용을 읽지 않는다.** 이름 + 크기만 비교하고 "직접 확인 필요"라고 명시한다.
- 캐시/임시/빌드 폴더는 **가장 바깥 폴더 하나만** 후보로 올려 하위 파일 중복 계산을 막는다.

### 보기 방식

**폴더 트리(기본) / 파일별 / 경로별 / 유형별 / 정리 이유별** 다섯 가지. 보기를 바꿔도 파일 시스템을 다시 분석하지 않는다
(`CleanupGrouper` 가 기존 후보 목록만 다시 묶는다). 경로별에서는 **경로 계층 트리**로 전환할 수 있고,
각 상위 폴더에 하위 정리 후보 용량이 합산된다.

그룹 체크박스는 **3단계(전체 / 일부 / 없음)** 다. 그룹을 체크하면 내부가 전부 선택되고,
안에서 개별 파일을 제외하면 "일부 선택"이 된다. P0 는 어떤 경로로도 선택되지 않는다.

#### 폴더 트리와 반복 패턴 접기

같은 이름의 파일이 수백 개 나열되어 한눈에 안 보이는 문제를 줄이려고 폴더별로 묶고 반복을 한 줄로 접는다
(`CleanupPatternFolder`). 접는 것은 **보여 주는 방식뿐**이며 후보를 만들거나 없애지 않는다.

| 규칙 | 예 | 표시 |
|---|---|---|
| (a) 같은 폴더 안에서 숫자 / 날짜 / 해시만 다른 이름 | `log_20240101.txt` … | `log_*.txt ×1,200건` |
| (b) 서로 다른 폴더에 있는 같은 이름 | `obj\Debug\a.pdb`, `obj\Release\a.pdb` | 공통 상위 폴더 아래 `a.pdb ×N건` (멤버는 상대 경로) |

- **5건 이상**일 때만 접는다. 확장자의 숫자(`.mp3` / `.mp4`)는 구분에 쓰고, 분할 압축(`.001`)은 변수로 본다
- 드라이브가 다르면 합치지 않는다(공통 상위 폴더가 없다)
- 후보가 하나뿐인 폴더 체인은 `C:\Users\me\AppData` 처럼 한 줄로 합친다
- 폴더/묶음 체크박스는 하위 후보 전체를 켜고 끈다. 수천 건을 한 번에 바꿔도 선택 요약은 **한 번만** 다시 계산한다
- 삭제 후 목록을 다시 만들어도 사용자가 펼쳐 둔 위치는 유지된다

### 개발자 정리 분석

Visual Studio 프로젝트별 빌드 산출물 합계 / `.git` 크기 / 로그 연령 분포(7·30·90·180일).

### 실측 (287,390 파일 트리)

```
정리 후보 193건 · 합계 168.8 GB   (분석 26ms)

[경로별]
...\DNFClient\Obj64\Debug        50.4 GB  10,499 files  [정리 가능성 높음]  위험도 낮음
...\DNFShared\GameScript\Lib     25.9 GB     111 files  [확인 필요]        위험도 보통
...\DNFClient\.vs                16.7 GB      54 files  [정리 가능성 높음]  위험도 낮음

개발자 분석: 빌드 산출물 137.7 GB · .git 0 B · 로그 525 MB (91일 이상 157 MB)
```

## 12. 삭제 / 새로고침

### 새로고침 — 두 탭이 다르게 동작한다

| 탭 | 1단계 | 2단계 |
|---|---|---|
| 큰 파일 | 현재 목록을 실제 파일 상태와 즉시 대조 | **분석 범위 전체 재스캔** (새 대용량 파일까지) |
| 정리 추천 | 동일 | **재분석** (재스캔 없음) |

대조는 `GetFileAttributesEx` 만 호출한다 — 파일을 열지 않으므로 사용 중인 파일도 잠기지 않고 내용도 읽지 않는다.
실행 중에는 버튼이 비활성화되고 `↻ 확인 중...` / `↻ 분석 중...` 으로 상태를 표시한다.

### 다중 선택과 삭제

**폴더 탭과 큰 파일 탭** 모두 탐색기와 동일하게 **Ctrl+클릭 / Shift+클릭 / Ctrl+A** 를 지원하고,
하단에 선택 개수와 총 용량을 항상 표시한다. 폴더 탭은 폴더/파일 개수를 나눠서 보여 준다
(`12개 항목 선택됨 (폴더 3 · 파일 9)`).

폴더를 선택하면 **하위 전체가 삭제 대상**이다. 그래서 확인 창을 띄우기 전에 각 폴더의 하위를 훑어
가장 높은 보호 등급을 매긴다(`ProtectionEvaluator.EvaluateEntry`). 이 작업은 I/O 라서 백그라운드에서
수행하고, 준비가 끝난 뒤 창을 연다 — 목록이 커도 UI 가 멈추지 않는다.

**삭제 버튼을 눌러도 즉시 지우지 않는다.** 반드시 확인 창을 거친다.

```
선택 → [삭제...] → 삭제 대상 확인 → (개별 제외) → 최종 검증 → 삭제 → 결과 → 즉시 반영
```

확인 창에는 파일명 · 경로 · 크기 · 수정일 · 유형 · **보호 등급** · 경고를 표시하고,
정렬 5종 + 검색 + 가상화를 지원한다. 체크가 바뀔 때마다 예상 확보 용량을 즉시 재계산한다.

- **P0 는 체크박스가 비활성**이고 전체 선택에서도 빠진다
- **P1 은 기본 체크 해제**, P2/P3 만 기본 삭제 대상
- 기본은 휴지통. **영구 삭제는 빨간 테두리 버튼 + 2단계 확인**

### 영구 삭제 엔진 (`FastDeleter`)

영구 삭제는 휴지통이 필요 없으므로 Windows 셸(`SHFileOperation`)을 거치지 않고 Win32 API 를 직접 호출한다.
셸은 항목마다 초기화 · 사전 열거 · 변경 알림을 하고 한 스레드에서 순서대로 처리해 파일이 많은 폴더에서 매우 느렸다.

- 같은 깊이의 폴더를 **병렬**로 열거하며 열거 결과의 파일을 바로 지우고(추가 조회 없음), 비어 버린 폴더는 깊은 곳부터 제거
- 한 폴더에 파일이 수만 개여도 그 안에서 나눠 병렬 삭제. 병렬도는 SSD 는 코어 수(상한 12), HDD 는 2
- Junction / 심볼릭 링크는 **따라가지 않고 링크만** 지운다(대상은 그대로). `\\?\` 접두사로 260자 초과 경로도 지운다
- 실패하면 단계적으로 재시도: ① 일반 삭제 → ② 읽기 전용·시스템·숨김 속성 해제 → ③ POSIX 삭제 시맨틱(읽기 전용 무시, `FILE_SHARE_DELETE` 로 열린 파일도 제거)
- 일부만 실패해도 나머지는 계속 지우고, 실패 수 · 첫 원인 · 사용 중 여부를 보고한다. 진행 보고는 50ms 로 솎아 UI 를 밀지 않는다
- 삭제 직전 보호 등급 재검증은 그대로다(P0 는 시도조차 하지 않음). 재검증의 하위 열거는 열거 결과의 속성을 써서 항목마다 속성 조회를 하지 않는다

| 60,000 파일 / 600 폴더 (SSD) | 시간 | 처리량 |
|---|---:|---:|
| 이전 (셸 `SHFileOperation`, 순차) | 50.5 s | 1,188 files/s |
| 현재 (`FastDeleter`, 병렬 + 재검증 포함) | 4.2 s | 14,200 files/s |

병렬도 스윕(3만 파일): 1 → 6.3k, 4 → 12.7k, 12 → 13.9k files/s. 4~8 에서 NTFS 메타데이터 갱신으로 포화된다.
한 폴더에 3만 파일이 몰린 경우 4.6k → 11.4k files/s.

> 휴지통 이동은 되돌릴 수 있어야 하므로 여전히 셸을 쓰며 순차 처리한다(속도는 셸에 좌우된다).
> 다른 프로세스가 `FILE_SHARE_DELETE` 없이 열어 둔 파일과 실행 중인 exe 는 OS 규칙상 지울 수 없어 `[사용 중]` 으로 보고한다.

### 삭제 전 최종 검증

스캔 시점의 판정을 그대로 믿지 않는다. 항목마다 지금 다시 판정한다.

| 상황 | 결과 |
|---|---|
| 이미 사라짐 | `[이미 삭제됨]` — 건너뜀 |
| 보호 등급이 P0 로 상승 | `[보호됨]` — 삭제를 시도조차 하지 않음 |
| 다른 프로세스가 사용 중 | P1 로 승격 + `[사용 중]` |
| 폴더 삭제 | 하위를 훑어 **가장 높은 등급**을 취함. Junction 에서 재귀 중단 |

**한 파일 실패가 전체 작업을 멈추지 않는다.** 진행률과 현재 경로를 표시하고,
완료 후 성공 / 실패 / 이미 존재하지 않음을 집계한다.

### 삭제 후 즉시 반영

성공한 항목은 **전체 재스캔 없이** 데이터 모델에서 제거한다.

```
RemoveNodes
  1. 삭제 대상 폴더 tombstone 을 id 오름차순으로 하위 전파
  2. 파일 배열 1패스 — 직속 부모 누적 크기 · 확장자 통계에서 차감
  3. 롤업 1회 — 상위 폴더까지 자동 반영
  4. Top-K 힙 재구축 O(N log K)
```

> 삭제된 파일마다 부모 체인을 거슬러 빼면 O(삭제 수 × 깊이)가 된다.
> 직속 부모에서만 빼고 롤업을 한 번 돌리면 O(폴더 수)로 끝난다.
> 여러 건을 한 번에 받는 이유도 같다 — 건별로 훑으면 O(삭제 수 × 파일 수)가 된다.

tombstone 처리된 노드는 폴더 목록 · 검색 · Top-N · 확장자 목록 **모든 조회에서 자동으로 빠진다.**

실측: 2개 삭제 → 상태바 `7 files / 245 MB` → `5 files / 150 MB`,
목록 · 점유율 바 · 확장자 통계가 재스캔 없이 갱신됐다.

## 13. 빌드 / 실행

빌드에는 **.NET 9 SDK** 가 필요하다(런타임만으로는 빌드할 수 없다). SDK 를 시스템에 설치하지 않았다면
`dotnet-install` 스크립트로 폴더 하나에만 설치하고 `DOTNET_ROOT` / `PATH` 로 가리키면 된다.

```bash
dotnet build DiskAnalyzer.sln -c Release
dotnet test  tests/DiskAnalyzer.Tests/DiskAnalyzer.Tests.csproj
```

```bash
# 배포용 단일 exe 만들기 (dist 폴더로)
dotnet publish src/DiskAnalyzer.App/DiskAnalyzer.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o dist
```

```bash
# 앱 (인자 없이 실행하면 드라이브 목록만 표시)
dist/DiskAnalyzer.exe
# 자동화/검증용: 경로와 시작 탭 지정 (0=폴더 1=Treemap 2=큰파일 3=파일유형 4=정리추천)
dist/DiskAnalyzer.exe "C:\Users" --tab 4
```

```bash
# 벤치마크
dist/DiskAnalyzer.Bench.exe C:\                      # 전체 드라이브
dist/DiskAnalyzer.Bench.exe C:\Users --sweep         # 워커 수 스윕
dist/DiskAnalyzer.Bench.exe C:\ --mode fast          # NTFS Fast Scan (관리자 권한 필요)
dist/DiskAnalyzer.Bench.exe --gen D:\bench 1000000   # 합성 트리 생성
```

앱은 `asInvoker` 로 실행된다. 일반 권한에서는 Compatibility Scan 으로 완전히 동작하고,
관리자로 실행하면 드라이브 루트 스캔에서 NTFS Fast Scan 을 시도한다.

## 14. 설정

Scan Mode(Auto/Fast/Compatibility) · Worker Count(Auto/수동) · Follow Symbolic Links ·
Show Hidden/System Files · UI Theme(Dark/Light/시스템) · Size Unit(Auto/KB/MB/GB/TB) · Cache Results

## 15. 확장을 열어 둔 부분

- **USN Journal 기반 Incremental Scan**: `ScanController` 가 스캐너 구현을 갈아 끼우는 구조라
  `IncrementalScanner` 를 추가하고 `NodeStore` 에 델타를 적용하면 된다.
- 이전 스캔과 비교 / Scan History: 캐시 포맷(`CacheService`, 버전 필드 포함)에 스냅샷이 이미 남는다.
- 경로·확장자 제외, Scheduled Scan, 중복 파일 해시 비교 등.

## 16. 알려진 제약

- **NTFS Fast Scan 미검증** (7절 참고). 관리자 권한에서 벤치마크로 먼저 확인 필요.
- **"현재 로드된 커널 드라이버"는 실제 로드 목록을 조회하지 않는다.** `System32\drivers` 경로 전체를
  보수적으로 P0 처리한다. "실행 중인 프로그램"도 프로세스 모듈 열거가 아니라 파일 잠금 여부로 판정한다.
- 중복 가능 파일은 **이름 + 크기**만 비교한다. 파일 내용을 읽지 않으므로 확정이 아니다.
- 폴더의 "마지막 접근일"은 표시하지 않는다(폴더 단위 접근 시간은 의미가 약함).
- 삭제 직후 목록은 내부 모델 기준으로만 정확하다. 외부 변경까지 맞추려면 새로고침이 필요하다.
- **동작 변화**: P2 조건이 "재생성 가능한 위치 + 30일 이상 미수정"이라
  **오늘 빌드한 Debug 폴더는 자동 선택 대상이 아니다.** 되돌리려면
  `CleanupAnalyzer.ResolveP2()` 의 `ageDays >= 30` 조건을 조정한다.
