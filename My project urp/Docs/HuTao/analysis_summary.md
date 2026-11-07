# Hu Tao 이식 – 분석 요약 (재사용용)

작성: 2026-11-07. 이후 작업은 이 파일과 `hutao_rig_analysis.json`을 먼저 읽고, 프로젝트를 다시 전체 탐색하지 않는다.
상세 본 표(305개, 부모·길이·웨이트·메시)는 `hutao_rig_analysis.json`.

## 0. 작업 환경의 사실
- 이번 세션에는 **Blender MCP / Unity MCP 도구가 없었다**(`localMcpServers` 비어 있음, 기기 셸 없음). 파일 읽기/쓰기만 가능.
- 따라서 `.blend`는 Blender 없이 직접 파싱(zstd 해제 → 순수 Python 블록/DNA 파서)했고, Unity 컴파일·Play Mode 테스트는 **수행하지 못했다**.
- 원본 `.blend`, FBX, 기존 스크립트는 수정하지 않았다(읽기 전용).

## 1. 기존 캐릭터(Furina) 로직 – 코드로 확인한 것
| 질문 | 답 |
|---|---|
| 구현 방식 | 혼합. 몸 동작은 `UntitledCharacterMotion`이 만드는 **절차적 모션 스켈레톤**("Original Motion Skeleton", Bone_NNN)을 `BlenderCharacterRetarget`이 Furina 본에 리타게팅. 옷은 `SilkClothDynamics`(실시간 PBD), 땋은머리는 리타게팅 단계의 `UpdateBraids`(SmoothDamp 지연 추종 + 가속도 항, 변위 한도 약 2cm) 절차식. 키프레임 .anim 로코모션은 없음(프로젝트의 .anim은 물레방아·풍차 2개뿐). |
| 이동/가속/회전 영향 | 예. `SilkClothDynamics`가 허리 속도·가속도·각속도·각가속도를 직접 샘플링해 관성(`-a`), 원심/오일러 항, 속도 비례 공기저항에 반영. |
| 관절별 다른 파라미터 | 예. `BoneSettings` 11개 값이 체인 깊이(root→tip)에 따라 Lerp, 레이어(HB2/3/4)별 보정. |
| 부모→자식 전달 | `parentMotionInfluence`(부모 이동 이송), 각도 제약 + 곡률 유지(curvatureRetention), 길이 제약(PBD), 정/역방향 길이 패스. |
| 충돌 | 캡슐 근사 7개(몸통·허벅지·정강이·발)와 파티클 구 충돌, 체인 간 거리 쌍 분리, 초기 접촉 제외 목록. |
| 알고리즘 | 60 Hz 고정 서브스텝(최대 6), Verlet 유사 적분 + PBD 길이/각도 제약 + 스프링 + 비틀림 스프링. 실행 순서: 모션 100 → 리타게팅 195 → 물리 198 → 카메라 200. |
| 한계 | `HB` 접두어·Furina 본 이름("Hips","Chest","Left leg"…)에 하드코딩 → 다른 리그에 그대로 못 씀. 머리카락(땋은머리)은 PBD가 아니라 변위가 작게 제한된 SmoothDamp 지연 추종이라 충돌·체인 제약이 없음. |
| 규모 | Furina 시뮬레이션 본 113개(HB, 웨이트 있음) + 땋은머리 36관절(SmoothDamp 지연 추종) = **149**. |

## 2. 신규 .blend(Hu Tao, MMD 계열) 분석
- 본 **305개**, 전부 Blender에서 deform 플래그가 켜져 있으나 **스킨 웨이트가 실제로 있는 본은 188개**. 메시 오브젝트 21개(정점 약 7만, 버텍스 그룹 231 엔트리), Constraint 0, Action 0, 드라이버/NLA 없음.
- 계층: `全ての親 → センター → センター2 → グルーブ → グルーブ2 → 腰 → {下半身, 上半身}`. IK 6개는 웨이트 없음.
- **다리 웨이트는 D본(足D/ひざD/足首D/足先EX, 8개)에 있고 足/ひざ/足首는 웨이트 0** → D본이 원본 다리 회전을 1:1 따라가야 함(구현됨).
- 팔 비틀림 보조(腕捩/手捩 및 1~3) 16개 중 14개 웨이트 있음 → 현재는 rest 유지(비틀림 분배는 후속).
- 2차 체인 **155본 / 17체인**(웨이트 있는 본 117, 나머지 38은 `_end`·말단 무웨이트 본):

| 종류 | 체인 | 본 수 | 웨이트 본 | 위치(부모) |
|---|---|---|---|---|
| HairBack 후발3 | 좌우 각 1 | 34 | 30 | 頭, 체인당 17본(웨이트 15) |
| HairSide 측발2 | 좌우 각 1 | 16 | 12 | 頭 |
| HairFront 발1 | 좌우 각 1 | 14 | 10 | 頭 |
| Tassel 帽流苏 | 1 | 7 | 5 | 頭 |
| HatStrap 帽带 | 좌우 각 1 | 16 | 12 | 頭 |
| Belt 带 | 좌우 각 1 | 14 | 10 | 上半身 |
| Hem 后摆(코트 뒷자락) | 좌우 각 3열 | 54 | 38 | 下半身 |

- 머리카락 체인(웨이트 정점 수): 후발3 143→690→180(체인당 4,727정점), 측발2/발1/모자끈/띠는 48~170정점대.
- 텍스처는 3장(Face/Outfit/Hair, 2048², RGBA). 재질→텍스처: 发·目·帽·帽_饰=Hair, 新規·鞋袜·戒指·带1·神之眼AL·肌·服·裤=Outfit, 나머지=Face.
- FBX(`Hu tao.fbx`)는 `.blend`와 동일 본 구조(Model 330 = 305본 + 21메시 + 4)로 확인. Unity 입력은 FBX 사용(기존 Furina와 동일한 방식).

## 3. 설계 결정
- 알고리즘: **기존 애니메이션(절차 모션) + 다중 관절 PBD 혼합**. Furina `SilkClothDynamics`와 같은 설계(스프링+각도 제한+PBD 길이+곡률 유지+캡슐 충돌+체인 분리)를 새 컴포넌트 `HuTaoChainPhysics`로 이식(기존 클래스는 sealed·Furina 하드코딩이라 수정 대신 독립 구현).
- 새로 추가한 머리카락 품질 요소: 종류별 프로파일(뒷머리 길고 느슨 / 앞·옆머리 단단), 체인별 결정적 편차(좌우 비대칭), 머리·목·팔 캡슐 충돌 + 초기 접촉 제외, 인접 가닥 분리 쌍, 가닥 비틀림 스프링, 체인별 위상이 다른 느린 돌풍 노이즈(대기 중 미세 흔들림, `idleBreeze`로 0 가능), 끝으로 갈수록 자유도 증가(stiffness·limit·parent 영향 Lerp).
- 중복 제어 방지: `HuTaoRetarget`은 몸/손가락/다리/D본만, 체인 155본은 `HuTaoChainPhysics`만 쓴다.
- 새 스크립트는 독립 컴포넌트이며 기존 스크립트·프리팹·씬은 **수정하지 않음**. 전환기는 씬 로드 후 스스로 부트스트랩.

## 4. 생성/추가한 파일
- `Assets/Game/Scripts/ThirdPerson/HuTao/HuTaoChainPhysics.cs` – 2차 체인 PBD (런타임 활용률 리포트 `F9`)
- `Assets/Game/Scripts/ThirdPerson/HuTao/HuTaoRetarget.cs` – 모션 스켈레톤 → MMD 본 리타게팅(방향/크기/위치 자동 맞춤 포함)
- `Assets/Game/Scripts/ThirdPerson/HuTao/CharacterSwitcher.cs` – 1=Furina, 2=Hu Tao
- `Assets/Game/Editor/HuTaoImportSettings.cs` – FBX 임포트 설정 자동 적용
- `Assets/Game/Characters/HuTao/Resources/HuTao/{HuTao.fbx, Face.png, Hair.png, Outfit.png}`
- `Docs/HuTao/{analysis_summary.md, hutao_rig_analysis.json}`

## 5. 검증 상태(정직한 기록)
- 수행: `.blend` 구조/웨이트 파싱, FBX 재질·본 대응 확인, 기존 코드 정독, 새 코드 수기 검토.
- **미수행**: Unity 컴파일, Play Mode, 모든 동작 시나리오(Idle~전환), 성능 측정, Blender 내 메시 변형 테스트. 관절 활용률의 "실행 중 유의미 이동 본 수"는 Play Mode에서 `F9`로 측정해야 한다.

## 6. 후속(미착수)
파라미터 튜닝, 팔 비틀림 분배, 눈 깜빡임/모프, 발 IK, Furina 대비 비교 녹화, 성능 프로파일, 의상(후마) 관통 점검.

## 7. 2차 수정 (2026-11-07 01:00~)
- **다리 X자 꼬임**: 원인 = 발 목표를 원본 스켈레톤의 (좁은) 발 위치 + 발 로컬 오프셋으로 잡아, 골반이 넓은 Hu Tao 다리가 안쪽으로 모임. 수정 = 발 목표를 `Hu Tao 고관절 + 원본 고관절→발목 벡터 × 다리비 + rest 보정(root 공간)`으로, 무릎 굽힘 방향은 원본 무릎 평면 + 약한 전방 바이어스. Play에서 대기 자세 다리 교차 해소 확인(저해상도 육안).
- **키**: Hu Tao 모자 제외 키 = Furina 모자 제외 키. 비율(정점 기준): Furina 1.5634/다리 0.8062 = 1.93935(帽 본 가중 정점·DM 장식 제외, 정수리 기준), Hu Tao 18.921/9.915 = 1.90838(帽·帽_饰 메시 제외). 런타임 로그: `hatless height=1.384 (Furina hatless=1.384) motionScale=0.898`. 모션 스켈레톤 이동량(골반·보폭)도 0.898배로 축소.
- **뒷자락(Hem)**: 끝단 강성 4.5→3.6, 감쇠 5→3.8, 각도 한도 52→64°, 공기저항·가속 반응 상향, 달릴 때(지면속도 1.6~5 m/s 블렌드) 강성 ×0.5·한도 ×1.4, 속도² 비례 진행파 펄럭임 + 양력(`flutter`, 전역 `runFlutter`). 급정지 후 천천히 감쇠(runBlend 하강 2.2/s). 띠(Belt)도 약하게 적용.
- **눈 깜박임**: FBX/.blend 모두 셰이프 키 없음 → `HuTaoBlink`가 런타임에 "Blink" 블렌드셰이프 생성(白目 타원 기준, 윗눈꺼풀 面/面1/睫/二重 압축, 안구 白目/目/目星 접힘, 眉 제외). Furina와 동일하게 모션 스켈레톤 IdleBlink 가중치(또는 BlinkWeight)로 구동. 오프라인 렌더로 형상 검증, 게임 내 클로즈업 육안 확인은 미수행.
- Play 중 스크립트 재컴파일 시 물리 배열 불일치 예외 → `initialized` 비직렬화로 재초기화되게 수정.
- 측정: 활용률 155본 중 138 이동(웨이트 117 전부), layerPairs 14, 물리 1스텝 ~4.4ms(에디터) — 최적화 필요.

## 8. 3차 수정 (2026-11-07 01:25~)
- **Hu Tao 소스 변경**: 원본 모션 스켈레톤 대신 **Furina의 최종 포즈**(BlenderCharacterRetarget 적용 후)를 리타게팅. `HuTaoRetarget` 실행 순서 197. Hu Tao 표시 중에도 Furina는 활성 상태로 포즈만 계산(렌더러·SilkClothDynamics·Furina 머리 물리 off). → 대기 시 팔 벌림 해소(팔이 몸 옆), 걸을 때 발 교차 해소(정면 연속 캡처로 확인). motionScale=1.016(Hu Tao 다리/Furina 다리).
- **손 모양**: 손가락을 국소 변화량 대신 Furina 손가락 절대 방향으로 정렬(마지막 마디는 중간 마디 방향을 연장).
- **Furina 머리카락**: `FurinaHairRig`가 CF_2_L/R에 4개씩, 바보털 DM_5_1에 3개 힌지 추가(SetupCurlTips와 같은 웨이트 분할, 해당 본 웨이트만 재분배) → 머리 관절 67개(땋은머리 18×2 + 컬 힌지 5×2, 옆머리 2×2+4×2, 바보털 6+3)를 `HuTaoChainPhysics`(Furina 이름 모드, 캡슐 14개)로 시뮬레이션. 바보털 전용 프로파일(높은 강성·낮은 감쇠로 튕김, 중력 영향 작음).
- 기존 방식: 땋은머리는 SmoothDamp 지연 추종(변위 약 2cm 제한), CF는 원본 회전 복사, 바보털(DM 6관절)은 웨이트는 있었지만 아무도 움직이지 않았음.
- 체인 끝 본 중 웨이트가 있는 본에 가상 끝 파티클 추가 → Furina 머리 67/67 관절이 1° 이상 움직임(F9 측정). 물리 1스텝 Furina 약 2.0ms, Hu Tao 약 4.4ms(에디터).
- 기존 스크립트(BlenderCharacterRetarget 등)는 수정하지 않음. Furina 머리 본은 retarget이 먼저 쓰고 물리(198)가 덮어씀.

## 9. 4차 수정 (2026-11-07) – 옆모습 걸음걸이, 양갈래 굵은 부분
- **옆에서 본 걸음걸이**(`HuTaoRetarget`)
  - 원인 1: 발 목표 = `Hu Tao 고관절 + 원본 고관절→발목 × 다리비 + restCorrection`에서 restCorrection이 **root 공간 고정 벡터**였다. 여기에 Hu Tao의 곧게 편 MMD rest 다리와 Furina의 살짝 굽은 대기 다리의 차이(세로 약 0.28u, 앞뒤 약 0.6u, 1u≈7.3cm)가 들어 있었다. 다리가 앞뒤로 흔들려도 이 벡터는 같이 돌지 않으므로, 다리를 25° 뒤로 뻗으면 목표 거리가 10.13u로 다리 길이(9.915u)를 넘어 무릎이 펴진 채 발이 끌리고, 앞으로 뻗으면 9.63u로 짧아졌다(앞뒤 비대칭 보폭). 발 전체도 몸보다 약 4cm 뒤에 놓였다.
  - 수정: `BalanceLegCorrection()`이 양다리 보정의 공통 성분(세로·앞뒤, 평균)을 골반 오프셋(`hipOffset`)으로 옮긴다. 대기 시 발 위치는 그대로이고 무릎은 Furina와 같은 각도로 굽는다. 다리별로는 좌우(stance 폭) 성분만 발 목표에 남는다(X자 꼬임 수정 유지).
  - 원인 2: `Fit()`이 위쪽 방향을 `두 고관절 중점 → 頭`로 잡았는데, MMD 頭 관절이 고관절보다 약 3.5° 뒤에 있어 리그 전체가 앞으로 3.5° 기울었다. 그래서 골반과 머리가 앞으로 숙었고 발은 걷는 내내 발끝이 아래로 향했다. 수정: 위쪽 방향을 모델의 가장 가까운 주축으로 스냅(`SnapToAxis`)하고 yaw 맞춤만 유지한다.
- **양갈래(후발3) 굵은 부분이 통째로 움직이던 문제**(`HuTaoChainPhysics`, `HairChainSmoother`)
  - 원인: 3차 이후의 `ApplyThickness`가 굵은 관절을 강성 ×1.6, 관절당 굽힘 9°, 달릴 때는 그 0.45배로 묶었고, 곡률 유지 스프링(약 16×강성)까지 강해서 굵은 덩어리가 막대처럼 한 번에 회전했다. 머리 흔들림도 체인 전체에 같은 위상으로 들어갔다.
  - 수정 1(풀림): 본별 `moveRelease`(뒷머리 뿌리 0.3 → 끝 0.85, 굵은 관절은 1까지)와 이동 블렌드 `moveBlend`(걷기부터 1, 지면·비수영·비등반)를 추가했다. 이동 중에는 강성 ×0.4, 곡률 유지 ×0.3, 감쇠 ×0.7, 회전 한도 ×1.3이 되고, 관절당 굽힘 한도는 28°까지 넓어진다. 서 있을 때의 굵은 부분은 굽힘 한도 12°, 강성 ×1.25로 유지하고, `runBendScale` 축소는 없앴다.
  - 수정 2(진행파): Furina 모션의 `GaitPhase`(한 주기 = 좌→좌 걸음)에 동기화한 가속도를 넣는다. 좌우 성분 `sin(φ)`, 앞뒤 성분 `0.45·sin(2φ+0.6)`이고 세그먼트 방향 성분은 제거한다. 관절마다 `2π/strideWaveJoints`(기본 9관절 = 1파장)씩 위상이 늦어져 파형이 뿌리에서 끝으로 전달된다. 세기 `strideWave`는 뿌리 0.45 → 끝 3.0 m/s²이고 전역 배율은 `hairStrideWave`(0이면 끔)다. 서브스텝마다 위상을 보간한다.
  - `HairChainSmoother` 가우시안 σ 상한을 1.4 → 1.0으로 줄였다(파형 곡률이 흐려지지 않게, 굽힘은 여전히 약 3관절에 분산).
  - 적용 범위: Hu Tao 이름 모드의 HairBack(후발3 좌우)만. Furina 땋은머리와 다른 체인은 변화 없음.
- 검증: Unity가 없어 컴파일과 Play Mode는 하지 못했다. tree-sitter C# 파서로 3개 파일 구문 오류 0을 확인했다. 솔버 핵심(적분, 각도·곡률·굽힘 제약, 길이 패스)을 Python으로 옮겨 88cm·17관절 꼬리를 보행 머리 흔들림으로 시뮬레이션했다.

  | | 끝 좌우 RMS | 굽힘 비율(강체 회전 제외) | 뿌리→끝 위상차 | 변곡점/프레임 |
  |---|---|---|---|---|
  | 이전 | 2.1cm | 0.07(거의 통째 회전) | 9° | 1.1 |
  | 풀림만 | 2.3cm | 0.11 | 18° | 1.0 |
  | 풀림+진행파(걷기) | 4.0cm | 0.98 | 196° | 3.2 |
  | 풀림+진행파(달리기) | 3.1cm | 0.85 | 61° | 1.8 |

  풀림만으로는 여전히 통째로 흔들리므로 진행파 구동이 필요하다. Play Mode에서 옆·뒤 시점 육안 확인과 `hairStrideWave`·`strideWaveJoints` 튜닝은 남은 작업이다.

## 10. 5차 수정 (2026-11-07) – 이동 중 다리 간격, 발목 꺾임 (`HuTaoRetarget`, Hu Tao 전용)
- **다리 간격**: 이동 중 발을 바깥으로 미는 양(다리당)을 `gaitWidening` 0.03 → **0.07m**(걷기)로 늘리고, 질주 시 `runWidening` **+0.03m**를 더했다(질주 시 다리당 0.10m, 양발 사이 0.20m 증가). 블렌드는 걷기 `InverseLerp(0.1, walkSpeed)`, 달리기 `InverseLerp(walkSpeed, sprintSpeed)`이며 대기 자세는 변하지 않는다. Furina는 영향 없음.
- **발목 꺾임**
  - 원인: 발은 Furina 발의 **월드 회전**을 그대로 복사하는데, 그 회전은 Furina 자신의 정강이에만 맞는다. Hu Tao 정강이가 다르게 기울면(넓힌 보폭, 다른 허벅지·정강이 비율, 다리 길이 초과로 인한 clamp) 그 차이가 모두 발목 관절 꺾임으로 나타났다. 보폭을 넓히면 옆으로 더 꺾이게 되어 있었다.
  - 수정: 빌드 직후(Furina 대기 자세) 한 번 포즈를 잡고 기준을 기록한다(`CaptureAnkleReference`): 양쪽 정강이 기울기(좌우·앞뒤), Furina 발 높이, Hu Tao 발목 로컬 회전.
  - 매 프레임 `FitAnkle`이 "Hu Tao 정강이 기울기 변화 − Furina 정강이 기울기 변화"만큼 발을 정강이와 같이 돌린다. 발이 공중에 있으면(Furina 발이 대기 높이보다 1.2~5cm 위) 100%, 디딘 상태에서는 `plantedAnkleFollow`=50%다(발바닥은 거의 평평하게, 발목은 덜 꺾이게).
  - 마지막으로 발목 관절이 대기 각도에서 `maxAnkleBend`=38° 이상 벗어나지 않게 제한한다.
  - 회전 부호는 수치로 확인했다(정강이가 옆 8°·앞 5° 기울면 발 아래 방향도 (8.0°, 5.05°)).
- 검증: tree-sitter 구문 검사 통과. Unity 컴파일·Play Mode 미수행. 옆·정면 시점에서 보폭과 발목을 육안으로 확인해야 하고, 너무 넓거나 좁으면 `gaitWidening`/`runWidening`, 디딘 발이 기울어 보이면 `plantedAnkleFollow`를 조정한다.

## 11. 6차 수정 (2026-11-07) – 바위 세트 속 풀 충돌 제거, 발목 비틀림 보정 강화
- **풀 충돌**
  - 원인: 스크린샷의 뾰족한 풀은 별도 오브젝트가 아니라 `Art/Meshes/Compositions/*`(바위·계단·벽 세트) 메시 **안에** 들어 있다. `WorldTraversalFix`가 LOD0 렌더러 메시 전체를 MeshCollider로 구웠기 때문에 풀잎도 벽처럼 막았다. 확인 과정: TerrainData 외부 참조는 지형 레이어 4개뿐(tree prototype 없음), 프리팹 에셋 안 콜라이더 없음, 씬의 활성 MeshCollider 메시는 Compositions/Houses/TreeWood/Town Wall/Doors뿐이었다.
  - FBX 파싱 + 아틀라스 색 분석: 연결 조각 중 초록·올리브(hue 45~155°, s≥0.25, v≥0.1)이면서 작은 것(최대 1.8m, 160삼각형 이하)은 메시 85개에 4,030개(71,905삼각형, 전체 979,305의 7.3%, 크기 중앙값 0.48m)다. 1.8m를 넘는 초록 구조물 118개(생울타리, 풀 덮인 경사 등)는 충돌을 유지한다.
  - 도구: `Assets/Game/Editor/CompositionPlantCollision.cs`, 메뉴 **Tools/World/Remove Plant Collision From Compositions**(Demo 씬에서 1회 실행). 같은 기준으로 풀 조각을 뺀 충돌 메시를 `Assets/Game/Collision/CompositionNoPlants/`에 굽고 씬 콜라이더에 할당한 뒤 저장한다(실행 전 `Logs/WorldTraversalBackups/`에 씬 백업). 렌더링 메시는 그대로다. 확인용 메뉴는 `Validate Composition Plant Collision`, 상세 로그는 `Logs/CompositionPlantCollision.txt`.
  - FBX·아틀라스가 Read/Write off라 빌드 런타임에서는 메시를 읽을 수 없으므로, 기존 `TreeWoodCollisionBaker`와 같은 에디터 굽기 방식을 택했다.
- **발목 비틀림**(`HuTaoRetarget`)
  - 발끝 방향 고정: 대기 자세에서 기록한 발끝(足首→つま先) 방향을 몸(root) 기준으로 `footYawLock`=75% 유지한다. Furina는 대기 시 발이 바깥으로 벌어져 있고 걸을 때 발 방향이 흔들려서, Hu Tao 발이 무릎과 다른 쪽을 향했다.
  - 무릎이 발끝을 따라감: 무릎 pole 바이어스를 root.forward 3%에서 발끝 방향 `kneeTrackToes`=6%로 바꿨다.
  - 비틀림 제거: 대기 이후 발목 회전을 정강이 축 기준 swing(굽힘)과 twist(비틀림)로 분해해 twist를 `ankleTwistRemoval`=80% 제거하고, swing은 `maxAnkleBend` 38° → **25°**로 제한한다.
  - `plantedAnkleFollow` 0.5 → **0.85**(디딘 발도 정강이 기울기를 더 많이 따라감).
  - 분해 수식은 수치로 확인했다(30° 굽힘 + 40° 비틀림 → 30.00°/40.00° 복원).
- 검증: 두 C# 파일 tree-sitter 구문 오류 0. Unity 컴파일·실행은 미수행. 풀 충돌은 메뉴 실행 후 해당 풀 사이를 지나가 보고, 발목은 정면·옆에서 확인해야 한다.

## 12. 7차 수정 (2026-11-07) – 영상 기반 걸음걸이 재작업, 풀 충돌 자동 제거
- **영상 분석**(사용자 녹화 9.5초, 옆모습 걷기, 10fps로 잘라 확인)
  - 무릎이 거의 펴진 채 다리가 컴퍼스처럼 벌어진다.
  - 앞발은 발끝이 약 25° 들리고 뒷발은 약 35° 아래로 꺾인다. 발이 다리에 고정된 막대처럼 돌았다.
  - 발이 지면보다 떠 보인다.
- **원인과 수정**(`HuTaoRetarget`)
  - 발 목표가 "Hu Tao 고관절 + Furina 고관절→발목"이었다. 그래서 골반을 내려도 발이 같이 내려가 무릎이 굽지 않았고, Furina 발목 높이(굽 있는 신발)를 그대로 써서 발이 떴다. 이제 발 목표는 **지면 기준**이다(`GroundLegs`/`FootGoal`).
    - 수평: Furina 발목 위치 × 다리비 + Hu Tao 자기 좌우 간격.
    - 수직: Furina 발목 높이 + (Hu Tao 발목~발바닥 높이 − Furina 발목~지면 높이). Furina 쪽은 빌드 시 레이캐스트로 재고, 실패하면 CharacterController 바닥 기준으로 잰다. Hu Tao 쪽은 全ての親(바닥) 기준이며, 이상하면 つま先 기준으로 잰다.
  - 골반 높이(`FitPelvisHeight`): 대기 자세에서 고관절~발목 거리 / 다리 길이가 Furina와 같아지도록 맞춘다(무릎 굽힘이 같아짐). 이동 중에는 `movingPelvisDrop`=0.8cm를 더 내려 디딘 무릎이 약 18° 굽는다. 발을 들 때는 Furina가 든 높이의 `swingLiftBoost`=50%를 더 들어 스윙 무릎이 더 굽는다.
  - 발 각도: 정강이를 따라가는 비율을 디딘 발 0.85 → **0.25**, 공중 1.0 → **0.6**으로 낮췄다. 마지막에 지면 기준 발끝 피치를 제한한다: 디딘 상태 ±6°, 공중 발끝 들림 ≤15°, 내림 ≤30°(`plantedToePitch`/`maxToeUp`/`maxToeDown`). 회전 부호는 수치로 확인했다.
  - 이전 `BalanceLegCorrection`(고관절 기준 보정)은 제거했다.
- **풀 충돌**(규칙 v2, 수동 실행 불필요)
  - 규칙 재조정: 237개 FBX 전체 조각의 색 분포를 분석했다. 회녹색 돌·벽(색상 92~96°, 채도 약 0.26, 3~25m)은 제외한다.
    - 초록 판정: 색상 40~150°, 그리고 (채도 ≥0.33, 또는 색상 ≥52°이면서 채도 ≥0.29, 또는 색상 60~85°이면서 채도 ≥0.18).
    - 크기: 최대 3.2m, 400삼각형 이하(1.8~3.1m 갈대·키 큰 풀·덤불 포함).
    - 1.2m 넘게 바닥에 깔린 평평한 풀밭 판은 밟는 바닥이므로 유지한다.
  - 결과(오프라인): 메시 143개, 식물 조각 6,480개, 삼각형 112,659개(11.5%). 이전 v1은 1.8m·160삼각형 기준이라 키 큰 풀 238개가 남아 있었다.
  - 집(Houses) 메시는 같은 색 규칙에 2.5m 벽 판재가 걸려서 대상에서 뺐다. 성벽에는 식물이 없다.
  - 구조: 에디터가 컴파일 후 자동으로(규칙 버전이 바뀌면 1회) 모든 컴포지션 메시의 무식물 충돌 복사본(`Assets/Game/Collision/CompositionNoPlants/`)과 매핑(`Assets/Game/Collision/Resources/CompositionPlantCollisionMap.asset`)을 만든다. 런타임 `Game.World.CompositionPlantCollisionMap`이 씬 로드마다 해당 MeshCollider를 복사본으로 바꾼다. 씬 파일은 수정하지 않는다.
  - 메뉴: `Tools/World/Regenerate Composition Plant Collision`(재생성), `Remove Plant Collision From Compositions`(선택: 씬에 직접 굽기), `Validate ...`.
- 검증: tree-sitter 구문 검사 3개 파일 통과. Unity 컴파일·Play 미수행.
