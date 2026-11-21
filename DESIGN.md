# LLM Pulse — 설계서 (Cursor 이관용)

> 가칭 **LLM Pulse**. 이름은 자유롭게 변경. 저장소: `mhbae-semi-lab/<repo-name>` (조직 계정에서 생성).
> 이 문서는 구현 지시서다. Cursor 에 이 파일을 그대로 컨텍스트로 넣고 마일스톤 순서대로 진행한다.

---

## 1. 목적

1. LLM 별 성능 수준을 분야별로 한눈에 파악한다.
2. 매일 08:00 에 데이터를 갱신해, 분야별로 "지금 가장 효율 좋은 LLM"을 추천한다.
3. 모든 지표에 대해 **출처 · 의미 · 척도 · 산정 기준**을 앱 안에서 확인할 수 있다.
4. 가독성 높은 UI, 다국어(영어/한국어/중국어/프랑스어).
5. 참고 사이트(Artificial Analysis leaderboards/models) 수준 이상의 정보량.

비목표(YAGNI): 모델 직접 호출/벤치마크 자체 실행, 사용자 계정/서버, 모바일 앱.

---

## 2. 기술 선택 — WPF 로 충분한가?

**결론: C# / .NET 8 / WPF (Visual Studio 2022) 로 진행. 성능 문제 없음.**

- 데이터량: 모델 수백 개 × 지표 수십 개. DataGrid + 차트 몇 개 수준이라 WPF 로 충분.
- 가상화(`VirtualizingPanel`)만 켜면 수천 행도 문제 없음.
- 차트는 **LiveCharts2 (SkiaSharp)** 또는 **ScottPlot 5**. 산점도(가격 vs 성능), 막대, 시계열 필요. 라이선스 MIT. 둘 중 ScottPlot 이 설정이 단순, LiveCharts2 가 애니메이션/툴팁이 좋음 → **LiveCharts2 권장**.
- 대안(필요 시): WinUI 3 / Avalonia(크로스플랫폼). 지금은 불필요. WPF 가 느려지는 상황(수만 행 실시간 갱신)이 아님.

| 용도 | 선택 |
|---|---|
| UI | WPF + **WPF-UI (lepoco)** — Fluent 스타일, 다크/라이트, 네비게이션 제공 |
| MVVM | **CommunityToolkit.Mvvm** (소스 생성기, 보일러플레이트 최소) |
| DI/호스팅 | `Microsoft.Extensions.Hosting` (DI, 로깅, HttpClientFactory) |
| HTTP | `HttpClient` + **Polly** (재시도/타임아웃) |
| 저장 | **SQLite** (`Microsoft.Data.Sqlite` + Dapper). EF Core 는 과함 |
| 차트 | LiveCharts2 (`LiveChartsCore.SkiaSharpView.WPF`) |
| 트레이 | `H.NotifyIcon.Wpf` |
| 다국어 | `.resx` + 런타임 전환(아래 §8) |
| 테스트 | xUnit (정규화/추천 로직만) |

---

## 3. 데이터 소스

원칙: **소스는 어댑터로 분리**하고, 실패해도 다른 소스로 앱이 동작해야 한다. 스크래핑보다 공식 API/공개 데이터셋 우선.

| 소스 | 제공 데이터 | 접근 | 비고 |
|---|---|---|---|
| **Artificial Analysis API** (주 소스) | Intelligence Index, Coding Index, Math Index, MMLU-Pro, GPQA, HLE, LiveCodeBench, SciCode, AIME, MATH-500, 가격(입력/출력/블렌디드 3:1), 출력 속도(tok/s), TTFT | `https://artificialanalysis.ai/api/v2/language/models/free?page=N`, 헤더 `x-api-key`. 무료 키(일 약 100 호출). 레거시 `/api/v2/data/*` 는 2026-11-04 폐기됨. **Free 응답에는 블렌디드 가격과 개별 벤치마크 점수가 없다** → 블렌디드는 (3·입력+출력)/4 로 앱이 계산, 지표는 Intelligence/Coding/Agentic Index + 속도/TTFT 만 사용 | **출처 표기(attribution) 의무** — 앱 About/푸터에 "Data: Artificial Analysis" + 링크. 약관·응답 필드는 구현 시 재확인 |
| **OpenRouter API** | 모델 목록, 컨텍스트 길이, 가격, 입출력 모달리티 | `https://openrouter.ai/api/v1/models` (키 불필요) | 가격 교차검증 + 컨텍스트/멀티모달 보강 |
| **LMArena (Chatbot Arena)** | 사람 선호 Elo (Text, Coding, Vision, 언어별) | 공식 API 없음. 공개 HF 데이터셋/리더보드 CSV 사용 | 구현 시 접근 경로 확인. 불가하면 해당 분야 "데이터 없음" 처리 |
| **Aider polyglot leaderboard** | 실제 코드 편집 성공률 | GitHub 저장소의 YAML | 코딩 분야 보조 |
| **SWE-bench / Terminal-Bench 등** | 에이전트형 코딩 | 공개 JSON/GitHub | 2차 단계 |
| **Open Ko-LLM / KMMLU** | 한국어 성능 | HF 공개 데이터 | 한국어 분야, 2차 단계 |
| **수동 보정 JSON** (`overrides.json`) | 구독 플랜, 모델 별칭, 출시일, 라이선스 | 앱 내장 + 사용자 편집 | 구독 정보는 API 가 없으므로 수동 |

각 소스는 `ISourceAdapter` 하나로 통일: `Task<IReadOnlyList<RawMetric>> FetchAsync(CancellationToken)`. 구현체는 소스당 1 클래스. (인터페이스가 소스 5개 이상이므로 여기서는 추상화 정당.)

**모델 이름 매칭이 가장 큰 리스크.** 소스마다 모델명이 다르다(`claude-4-opus` vs `Claude 4 Opus (Reasoning)`). → `model_alias` 테이블 + 정규화 규칙(소문자, 공백/하이픈 통일, 버전 토큰 추출) + 자동 매칭 실패 항목은 UI "매칭 필요" 목록으로 노출.

---

## 4. 분야(카테고리)와 지표 기준 — 요구사항 3번의 핵심

앱에는 **"지표 정보" 화면**을 둔다. 각 지표 카드에 아래 항목을 반드시 표시:

> 이름 · 한 줄 설명 · 측정 방법 · 척도(0–100 / Elo / tok/s / $) · 높을수록 좋은가 · 출처 링크 · 마지막 갱신일 · 신뢰도 주의사항(오염·자체보고 등)

정의는 코드가 아니라 `metrics.json` (다국어 설명 포함)에 둔다 → 지표 추가 시 코드 수정 없음.

### 4.1 분야 목록 (다양할수록 좋음)

| # | 분야 | 사용 지표 (우선순위순) | 1차/2차 |
|---|---|---|---|
| 1 | **종합 지능** | AA Intelligence Index | 1차 |
| 2 | **코딩** | AA Coding Index, LiveCodeBench, SciCode, Aider polyglot | 1차 |
| 3 | **수학** | AA Math Index, AIME, MATH-500 | 1차 |
| 4 | **추론/과학** | GPQA Diamond, HLE | 1차 |
| 5 | **지식/상식** | MMLU-Pro | 1차 |
| 6 | **속도** | 출력 tok/s | 1차 |
| 7 | **응답 지연** | TTFT (초, 낮을수록 좋음) | 1차 |
| 8 | **가격** | 입력/출력/블렌디드 $/1M tok | 1차 |
| 9 | **사람 선호(대화)** | LMArena Text Elo | 2차 |
| 10 | **에이전트/툴 사용** | SWE-bench Verified, τ²-bench, Terminal-Bench | 2차 |
| 11 | **긴 컨텍스트** | 컨텍스트 길이(OpenRouter), 장문 벤치(가능 시) | 2차 |
| 12 | **멀티모달(비전)** | LMArena Vision, MMMU | 2차 |
| 13 | **다국어/한국어** | MMMLU, KMMLU 등 | 2차 |
| 14 | **오픈 가중치** | 라이선스 플래그로 필터 (분야가 아니라 필터) | 1차 |

### 4.2 점수 정규화 (서로 다른 척도를 비교 가능하게)

- 원본 값(raw)은 항상 보존하고 표시한다.
- 분야 점수(0–100) = 해당 분야 지표들의 **백분위 점수 가중 평균**.
  - 지표별: `pct = 해당 지표를 가진 모델 중 순위 백분위(0–100)`. 지표가 "낮을수록 좋음"이면 뒤집는다.
  - 백분위 대신 min-max 는 이상치(1개 초고성능 모델)에 약하므로 **백분위 채택**.
  - 단, AA Intelligence Index 처럼 이미 0–100 척도의 합성 지표는 **그대로 사용**하고 백분위 변환하지 않는다 (표시 점수 = 원본, 이중 변환 방지).
- 지표 누락 시: 가용 지표만으로 가중 재정규화. 가용 지표가 분야 지표의 50% 미만이면 점수를 "—" 로 표시(추정하지 않음).
- 가중치 기본값은 `categories.json` 에 두고, 설정 화면에서 사용자가 조정 가능(2차).

### 4.3 "효율 좋은 LLM" 정의 — 추천 로직

세 가지 모드를 분야별로 각각 계산한다.

1. **최고 성능 (Best)**: 분야 점수 1위.
2. **가성비 (Best Value)**: *분야 점수가 최고 점수의 90% 이상*(허용치 `tolerance`, 설정 가능)인 모델 중 **블렌디드 가격이 가장 낮은 모델**.
   - 단순 `점수/가격` 비율은 싸고 약한 모델을 항상 1위로 만들므로 쓰지 않는다. 임계값 방식이 설명 가능하고 직관적.
   - 보조로 **파레토 프론티어** 산점도(X=가격 log 축, Y=분야 점수)를 보여주고 프론티어 위 모델을 강조.
3. **내 구독 기준 (My Plan)**: 사용자가 보유한 구독/접근 수단을 체크 → 그 안에서 Best / Best Value 재계산.
   - 구독 플랜은 `plans.json` (예: ChatGPT Plus/Pro, Claude Pro/Max, Gemini Advanced, Copilot, Cursor, 무료 티어, "API 사용"). 각 플랜 → 접근 가능한 모델 목록 매핑. **수동 관리 데이터**이며 앱에 "마지막 확인일" 표시. 사용자가 편집 가능.
   - 구독은 월 정액이라 토큰 단가가 없다 → My Plan 모드에서는 가격 대신 "성능 순위"를 기본으로 하고, API 가격은 참고 표시.

추천 결과는 항상 **근거 문장**과 함께 표시: "코딩 분야 점수 71.2 (1위 73.5 의 96.9%), 블렌디드 $0.6/1M — 허용치 90% 이상 중 최저가".

---

## 5. 갱신 스케줄 (매일 08:00)

앱이 꺼져 있어도 갱신되어야 하므로 두 겹으로 구성:

1. **Windows 작업 스케줄러**에 `LLMPulse.exe --refresh` 매일 08:00 작업 등록. UI 없이 갱신 후 종료(헤드리스). 설정 화면의 "자동 갱신 등록" 버튼이 `schtasks`(또는 `TaskScheduler` NuGet)로 등록/해제. **`.bat` 파일은 만들지 않는다.**
2. 앱 실행 중이면 **인앱 타이머**도 08:00 에 갱신 (트레이 상주 옵션).
3. **캐치업**: 앱 시작 시 `last_refresh_at` 가 오늘 08:00 이전이고 현재 시각이 08:00 이후면 즉시 1회 갱신 (PC 가 꺼져 있던 경우).

갱신 절차:
```
Fetch(각 소스, 병렬, 소스별 실패 격리)
 → alias 매칭 → raw_metric upsert
 → snapshot(date) 저장 (일별 이력)
 → 분야 점수/추천 재계산
 → 전일 대비 변동 감지 (신규 모델, 1위 교체, 가격 변경)
 → 토스트 알림 (설정으로 on/off)
```
- 소스 하나 실패 시 이전 데이터 유지 + UI 에 "소스별 마지막 성공 시각 / 오류" 배지.
- 네트워크 오프라인이어도 SQLite 캐시로 전체 화면 동작.
- 일 1회 + 수동 새로고침(최소 간격 10분)으로 API 한도 보호.
- API 키는 **Windows Credential Manager / DPAPI** 로 저장 (평문 설정 파일 금지, 저장소에 커밋 금지).

---

## 6. 화면 설계

좌측 네비게이션(WPF-UI `NavigationView`) + 상단 바(검색, 언어, 테마, 새로고침, 마지막 갱신 시각).

1. **대시보드 (오늘의 추천)**
   - 분야별 카드 그리드: 분야명 / 🏆 최고 성능 모델 / 💰 가성비 모델 / 내 구독 기준 모델 / 전일 대비 변화 배지.
   - 카드 클릭 → 해당 분야 상세.
   - 상단 요약: "오늘 신규 모델 N개, 1위 변경 M건".
2. **리더보드**
   - 참고 사이트 수준의 표: 모델, 제작사, 분야 점수들, 가격, 속도, 컨텍스트, 라이선스.
   - 정렬, 컬럼 선택, 제작사/오픈가중치/가격대/내 구독 필터, 검색, 모델 다중 선택 → 비교.
   - 셀 색상 스케일(점수 히트맵)은 색 + 숫자 병기(색약 고려).
3. **분야 상세**
   - 순위 막대 차트, **가격 vs 성능 산점도(파레토 프론티어 표시)**, 추천 근거, 사용 지표/가중치 표시.
4. **모델 상세 / 비교**
   - 레이더 차트(분야별), 지표 원본값 표, 가격·속도, 일별 추이 라인(스냅샷 기반).
   - 최대 4개 모델 나란히 비교.
5. **지표 정보 (기준 설명)**
   - §4 의 지표 카드 목록. 대시보드/표 헤더의 ⓘ 아이콘에서 해당 카드로 바로 이동.
   - "점수 산정 방식" 설명 페이지(§4.2, §4.3 을 사용자 언어로 서술) + 계산 예시.
6. **내 구독 설정**: 플랜 체크박스, 플랜→모델 매핑 확인/편집, 마지막 확인일.
7. **설정**: 언어, 테마(시스템/라이트/다크), 갱신 시각(기본 08:00), 작업 스케줄러 등록, 트레이 상주, 알림, 가성비 허용치(기본 90%), API 키 입력, 데이터 소스 상태, 내보내기(CSV/PNG).
8. **About**: 데이터 출처·라이선스·attribution 목록.

UX 원칙: 기본은 쉬운 요약(카드), 깊게 보려면 표/차트. 숫자는 항상 단위·방향(↑좋음/↓좋음) 표시. 최소 글자 크기 12pt, 대비 WCAG AA, 키보드 탐색 가능. 데이터 없음/오래됨(48h+)은 명시적 배지.

---

## 7. 아키텍처 / 프로젝트 구조

단일 솔루션, 프로젝트 3개로 최소화:

```
LLMPulse.sln
├─ src/
│  ├─ LLMPulse.Core/            # 순수 로직 (UI 무의존, 테스트 대상)
│  │   ├─ Models/               # Model, MetricDef, RawMetric, CategoryScore, Plan
│  │   ├─ Sources/              # ISourceAdapter + ArtificialAnalysis/OpenRouter/LmArena/Aider...
│  │   ├─ Scoring/              # Normalizer, CategoryScorer, Recommender (§4)
│  │   ├─ Matching/             # AliasResolver
│  │   ├─ Storage/              # SqliteStore (Dapper), 마이그레이션
│  │   └─ RefreshService.cs     # §5 파이프라인
│  ├─ LLMPulse.App/             # WPF (View, ViewModel, Resources/Strings.*.resx, Themes)
│  └─ LLMPulse.Tests/           # xUnit: Normalizer, Recommender, AliasResolver
├─ data/                        # metrics.json, categories.json, plans.json, overrides.json (다국어 설명 포함)
├─ docs/DESIGN.md               # 이 문서
└─ README.md                    # 빌드 방법 (명령줄), 키 설정, 출처 표기
```

빌드/실행 (README 에 명령줄로 기재):
```
dotnet restore
dotnet build -c Release
dotnet publish src/LLMPulse.App -c Release -r win-x64 --self-contained false -o Release
```

### SQLite 스키마(초안)

```
model(id PK, canonical_name, vendor, release_date, open_weights, context_len, ...)
model_alias(source, source_name, model_id FK)           -- 매칭 테이블
metric_def(id PK, ...)                                  -- metrics.json 미러
raw_metric(model_id, metric_id, value, source, fetched_at)   -- 최신값
snapshot(date, model_id, metric_id, value)              -- 일별 이력 (추이 차트)
category_score(date, category_id, model_id, score, rank, coverage)
price(model_id, input_per_m, output_per_m, blended_per_m, source, fetched_at)
refresh_log(id, started_at, finished_at, source, status, message)
setting(key PK, value)
```

---

## 8. 다국어 (EN / KO / ZH / FR)

- 문자열: `Resources/Strings.resx` (영어 기본), `.ko`, `.zh-Hans`, `.fr`. 런타임 언어 전환은 `CultureInfo` 설정 + `{loc:Loc Key}` 마크업 확장(또는 `WPF-UI` 로컬라이즈 패턴)으로 **재시작 없이** 반영.
- 지표/분야 설명(`metrics.json`, `categories.json`)은 `{ "en": "...", "ko": "...", "zh": "...", "fr": "..." }` 구조. 누락 언어는 영어로 폴백.
- 숫자/날짜/통화는 현재 culture 형식. 가격 기준 통화는 USD 고정(환산은 비목표).
- 폰트: 한/중 글리프 폴백 위해 `Segoe UI, Malgun Gothic, Microsoft YaHei UI` 체인.
- 번역 초안은 AI 로 작성하되 `docs/i18n-review.md` 에 "원어민 검수 필요" 체크리스트 유지.

---

## 9. 마일스톤 (Cursor 작업 순서)

| M | 내용 | 완료 기준 |
|---|---|---|
| M0 | 저장소/솔루션 생성, 프로젝트 3개, DI·로깅·SQLite 마이그레이션 | 빈 창이 뜨고 DB 파일 생성 |
| M1 | AA 어댑터 + 저장 + `RefreshService` (`--refresh` CLI 포함) | 명령줄 갱신 후 DB 에 모델/지표/가격 적재 |
| M2 | Normalizer / CategoryScorer / Recommender + 단위 테스트 | §4 규칙 테스트 통과 (누락·역방향 지표·허용치 경계) |
| M3 | 리더보드 + 대시보드 + 지표 정보 화면 (EN/KO 먼저) | 추천 카드·표·ⓘ 설명 동작 |
| M4 | 차트(산점도·레이더·추이) + 모델 비교 | 파레토 프론티어 표시 |
| M5 | OpenRouter 어댑터 + alias 매칭 UI | 매칭 실패 목록 수정 가능 |
| M6 | 작업 스케줄러 등록, 트레이, 캐치업, 알림 | PC 재시작 후에도 08:00 갱신 |
| M7 | 구독 플랜(My Plan 모드) | 구독 체크 시 추천 변경 |
| M8 | LMArena / Aider / 한국어 등 2차 소스, ZH/FR 번역, 내보내기 | 분야 14개 중 가용 분야 확대 |

---

## 10. 리스크 / 확인 필요

1. **AA API 약관·쿼터·응답 필드명**은 키 발급 후 실제 응답으로 확정 (이 문서의 필드는 예상치). attribution 필수.
2. **LMArena 등 공식 API 없는 소스**: 접근 방식이 바뀔 수 있음 → 어댑터 격리, 실패 시 해당 분야만 "데이터 없음".
3. **모델명 매칭 오류**가 추천 신뢰도를 좌우 → alias 테이블 + 수동 보정 UI 필수.
4. **구독 → 모델 매핑은 수동 데이터**로 자주 바뀜 → "마지막 확인일" 노출, 사용자 편집 허용.
5. **벤치마크 한계 고지**: 데이터 오염, 제작사 자체 보고, 벤치 점수 ≠ 실제 업무 성능. 지표 정보 화면과 About 에 명시.
6. **보안**: API 키는 DPAPI 저장, 로그에 키 출력 금지, `.gitignore` 에 `*.db`, 사용자 설정 포함.
7. **반입(보안 구역) 용도라면**: 배포물은 `Release/` 폴더(exe + dll) + 소스만. `.bat` 금지(빌드 방법은 README 명령줄로).

---

## 11. 저장소 생성 (사용자 수행)

```
gh repo create mhbae-semi-lab/llm-pulse --private --description "LLM performance & value tracker (WPF)" --clone
```
`.gitignore` 는 `dotnet new gitignore` 로 생성. 라이선스는 사내 정책에 따라 결정(데이터 소스 attribution 은 별도 유지).

## 구현 현황 (2026-11-19)

| 마일스톤 | 상태 | 메모 |
|---|---|---|
| M0–M3 | 완료 | 스키마/AA Free 어댑터/점수·추천/대시보드·리더보드·지표 정보(EN/KO) |
| M4 | 완료 | 분야 상세(막대 + 파레토 산점도), 모델 비교(레이더 + 추이, 최대 4개), 리더보드 다중 선택 |
| M5 | 완료 | OpenRouter(MatchOnly) 어댑터 + 보수적 AliasResolver + 모델 매칭 화면(수동 연결/해제), 긴 컨텍스트 분야 |
| M6 | 완료 | 작업 스케줄러(schtasks + 임시 XML, `StartWhenAvailable`), 앱 시작 캐치업, 1분 주기 인앱 확인, 트레이/토스트, 단일 인스턴스 |
| M7 | 완료 | `plans.json`(제작사 단위 근사), `plans.user.json` 으로 대체 가능, 카드 "내 구독 기준 최고" + 리더보드 "내 구독만" |
| M8 | 부분 | **완료**: Aider 폴리글랏 소스, ZH/FR UI 번역, CSV(리더보드)/PNG(현재 화면) 내보내기. **미구현**: LMArena(안정적인 공식 API 없음), 한국어 벤치마크(검증된 공개 소스 미확인) |

설계와 달라진 점:
- MatchOnly 소스(OpenRouter, Aider)는 새 모델을 만들지 않고 이미 알려진 모델에만 지표를 붙인다. 이름이 모호하면 자동 매칭하지 않고 "모델 매칭" 화면에 남긴다.
- 갱신은 주 소스(AA)가 하나라도 성공해야 "갱신됨"으로 기록한다 (보강 소스만 성공해도 기록하지 않음).
- 구독은 정액이라 My Plan 은 가격 대신 성능 순위를 보여준다.
